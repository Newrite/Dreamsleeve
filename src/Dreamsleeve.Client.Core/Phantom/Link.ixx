export module Dreamsleeve.Client.Phantom.Link;

import std;
import Dreamsleeve.Client.Phantom.Http;
export import Dreamsleeve.Client.Phantom.Exchange;

export namespace Dreamsleeve::Client::Phantom
{

  // An encoded message for the model or pose lane.
  struct Outbound
  {
    std::uint8_t lane{};
    Bytes        bytes;
  };

  // What the publisher and the downloader share on the network thread: the
  // server's policy and the context they act in, the requests toward the
  // server and the HTTP transfers. Streaming owns the link and both users.
  struct Link
  {
    using Clock = std::chrono::steady_clock;

    // A request the server has not answered, or a transfer that stopped moving.
    static constexpr auto Timeout = std::chrono::seconds(30);
    // A transfer that could not start or stalled is tried again after this.
    static constexpr auto        RetryDelay  = std::chrono::seconds(1);
    static constexpr std::size_t MaxRequests = 128;

    // Waiting for its turn, then for the server's answer to the request.
    struct Pending
    {
      Clock::time_point at;
    };

    struct Awaiting
    {
      RequestId         request;
      Clock::time_point deadline;
    };

    Exchange& exchange;
    Http&     http;

    std::optional<Wire::Policy> policy;
    std::uint64_t               context{};
    bool                        active{};
    std::deque<Wire::Request>   requests;
    RequestId                   lastRequest;

    bool Request(Wire::Request request)
    {
      if (requests.size() >= MaxRequests)
      {
        exchange.Failed("Очередь передачи фантомов заполнена");
        return false;
      }
      requests.push_back(std::move(request));
      return true;
    }

    // Zero once the identifiers run out: the caller sends nothing.
    RequestId NextRequest()
    {
      if (lastRequest.value == std::numeric_limits<std::uint64_t>::max()) return {};
      return RequestId{++lastRequest.value};
    }

    void Cancel(TransferId id)
    {
      http.Cancel(id);
      Request(Wire::Cancel{id});
    }
  };

}
