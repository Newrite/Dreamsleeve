export module Dreamsleeve.Client.Phantom.Streaming;

import std;
import Dreamsleeve.Client.Domain;
import Dreamsleeve.Client.Phantom.Http;
import Dreamsleeve.Client.Phantom.Publisher;
import Dreamsleeve.Client.Phantom.Downloader;
export import Dreamsleeve.Client.Phantom.Link;
export import Dreamsleeve.Client.Phantom.Worker;

export namespace Dreamsleeve::Client::Phantom
{

  // The phantom lanes on the network thread: decodes the server's messages and
  // hands them to the publisher (the own phantom) or the downloader (remote
  // models), keeps the context both act in and sends their requests in order.
  class Streaming final
  {
    using Clock = Link::Clock;

    // The server's answer to a failed HTTP transfer is to retry after this.
    static constexpr std::uint32_t TransferRetryMs = 1000;
    // Requests encoded per poll; the rest wait for the next one.
    static constexpr std::size_t MaxOutboundPerPoll = 16;

    Exchange&  exchange;
    Worker     worker;
    Http       http;
    Link       link{exchange, http};
    Publisher  publisher{link};
    Downloader downloader{link, worker};

    std::size_t Transfers() const
    {
      return (publisher.Transferring() ? 1 : 0) + downloader.Transferring();
    }

    void Preferences()
    {
      if (!link.policy) return;
      const auto settings = exchange.Settings();
      link.Request(
        Wire::Preferences{
            link.active && link.policy->enabled && settings.publish,
            link.active && link.policy->enabled && settings.receive,
            std::min(settings.maximum, link.policy->maximumVisible),
            std::min(settings.distance, link.policy->distance)
        });
    }

    // A repeated Policy starts a new server phantom session after the server
    // lost queued responses. Its offers, transfers and pending publication
    // admission are gone; the server offers the current selection again.
    void Resynchronize()
    {
      downloader.Resynchronize();
      // Preferences and Withdraw still describe this client; the rest name old offers or transfers.
      std::erase_if(link.requests, [](const auto& request) {
        return !std::holds_alternative<Wire::Preferences>(request) && !std::holds_alternative<Wire::Withdraw>(request);
      });
      exchange.ClearRemotes();
      publisher.Resynchronize();
    }

    void Receive(const Wire::Policy& value)
    {
      if (link.policy) Resynchronize();
      link.policy = value;
      exchange.SampleRate(value.sampleRate);
      Preferences();
      publisher.Publish(Clock::now(), Transfers());
    }

    void Receive(const Wire::PoseDemand& value)
    {
      if (exchange.PoseDemand(value)) publisher.ForgetPose();
    }

    void Receive(const Wire::Settled& value)
    {
      exchange.Settled(value);
    }

    void Receive(const Wire::Offer& value)
    {
      downloader.Receive(value);
    }

    void Receive(const Wire::Transfer& value)
    {
      if (!link.policy || !link.active)
      {
        link.Cancel(value.transfer);
        return;
      }
      if (value.upload)
        publisher.Receive(value);
      else
        downloader.Receive(value);
    }

    void Receive(const Wire::Complete& value)
    {
      const auto now = Clock::now();
      if (!publisher.Complete(value, now)) downloader.Complete(value, now);
    }

    void Receive(const Wire::Remove& value)
    {
      exchange.Remove(value);
      downloader.Receive(value);
    }

    // A failed transfer completes as the server's refusal with a retry would;
    // a finished download hands its bytes over.
    void PollHttp(Clock::time_point now)
    {
      publisher.Progress(now);
      downloader.Progress(now);
      for (auto& done : http.Poll())
      {
        const auto& transfer = done.transfer;
        if (transfer.upload ? !publisher.Owns(transfer) : !downloader.Owns(transfer)) continue;
        if (done.error)
        {
          link.Request(Wire::Cancel{transfer.transfer});
          Receive(
            Wire::Complete{
                transfer.transfer,
                false,
                *done.error,
                transfer.player,
                transfer.asset.generation,
                TransferRetryMs,
                transfer.upload,
                transfer.request
            });
        }
        else if (!transfer.upload)
          downloader.Arrived(transfer, std::move(done.bytes), now);
      }
    }

    void DrainRequests(std::vector<Outbound>& output)
    {
      while (!link.requests.empty() && output.size() < MaxOutboundPerPoll)
      {
        auto encoded = Wire::Encode(link.requests.front());
        link.requests.pop_front();
        if (encoded)
        {
          exchange.Count(encoded->size(), 0);
          output.push_back({Wire::ModelsLane, std::move(*encoded)});
        }
        else
          exchange.Failed("Не удалось сформировать запрос фантома");
      }
    }

public:

    Streaming(Exchange& owner, std::filesystem::path cache) : exchange(owner), worker(owner, std::move(cache)) {}

    void ConfigureHttp(std::string url, bool insecure)
    {
      http.Configure(std::move(url), insecure);
    }

    void Reset()
    {
      http.Reset();
      exchange.Reset();
      link.policy.reset();
      publisher.Reset();
      downloader.Reset();
      link.requests.clear();
      link.context = 0;
      link.active  = false;
    }

    void Context(std::uint64_t revision, bool ready, std::optional<Domain::LocationId> location = {})
    {
      if (link.context != revision || link.active != ready)
      {
        publisher.Leave(link.context != revision);
        link.context = revision;
        link.active  = ready;
        std::erase_if(link.requests, [](const auto& request) {
          return std::holds_alternative<Wire::Publish>(request) || std::holds_alternative<Wire::Download>(request);
        });
        downloader.Clear();
        if (ready && link.policy && exchange.Settings().receive)
          for (const auto& remote : exchange.Read().remotes)
            downloader.Receive(Wire::Offer{remote.player, remote.view, remote.descriptor});
        publisher.Restart();
        if (link.policy)
        {
          Preferences();
          if (!link.active)
            link.Request(Wire::Withdraw{});
          else
            publisher.Publish(Clock::now(), Transfers());
        }
      }
      exchange.Context(revision, ready && link.policy && link.policy->enabled, std::move(location));
    }

    Result<void> ReceiveAsset(std::span<const std::uint8_t> bytes)
    {
      auto response = Wire::DecodeAsset(bytes);
      if (!response) return std::unexpected(response.error());
      std::visit([&](const auto& value) { Receive(value); }, *response);
      return {};
    }

    Result<void> ReceivePose(std::span<const std::uint8_t> bytes, std::uint64_t arrivalUs)
    {
      auto pose = Wire::DecodePose(bytes);
      if (!pose) return std::unexpected(pose.error());
      const auto remote = exchange.Find(pose->player);
      if (remote && remote->view == pose->view)
      {
        const auto queue = [&](const Wire::Pose& sample, const RemoteVersion& version) {
          if (version.Asset() && version.descriptor.generation == sample.generation)
            worker.Queue(Wire::RemotePose{pose->player, pose->view, sample}, version.Asset(), arrivalUs);
        };
        queue(pose->sample, *remote);
        if (pose->sample.previous)
        {
          queue(*pose->sample.previous, *remote);
          if (remote->previous) queue(*pose->sample.previous, *remote->previous);
        }
      }
      return {};
    }

    // One network turn, in the order the server expects the requests.
    std::vector<Outbound> Poll(Clock::time_point now = Clock::now())
    {
      PollHttp(now);
      std::vector<Outbound> output;
      auto                  outgoing = exchange.TakeOutput();
      for (const auto& ready : outgoing.displayed)
        link.Request(ready);
      publisher.Revise(outgoing);

      if (outgoing.changed)
      {
        Preferences();
        if (!outgoing.settings.publish) publisher.Stop();
        if (!outgoing.settings.receive) downloader.Clear();
      }

      if (outgoing.publication) publisher.Adopt(std::move(*outgoing.publication), now);
      publisher.Capture(std::move(outgoing.pose));
      publisher.Retry(now, Transfers());
      downloader.Poll(now, publisher.Transferring() ? 1 : 0);
      publisher.Refill(now);
      downloader.Expire(now);
      DrainRequests(output);
      publisher.SendPose(now, outgoing.settings, output);
      return output;
    }
  };

}
