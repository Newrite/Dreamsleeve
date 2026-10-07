import std;
import Dreamsleeve.Client.Phantom.NifOutput;
import Dreamsleeve.Client.Phantom.Nif;

namespace P = Dreamsleeve::Client::Phantom;

// Replays detached writes, NOT native SaveBinary or engine allocation. The
// baseline reproduces NiMemStream's verified doubling and final vector copy.
struct GrowingOutput
{
  std::unique_ptr<std::uint8_t[]> data{new std::uint8_t[1024]};
  std::size_t                     capacity{1024}, size{};
  std::uint64_t                   allocations{1}, copied{};

  void Write(std::span<const std::uint8_t> part)
  {
    if (size + part.size() > capacity)
    {
      capacity  = std::max(capacity * 2, size + part.size());
      auto next = std::unique_ptr<std::uint8_t[]>(new std::uint8_t[capacity]);
      std::memcpy(next.get(), data.get(), size);
      copied += size;
      ++allocations;
      data = std::move(next);
    }
    std::memcpy(data.get() + size, part.data(), part.size());
    size += part.size();
  }

  std::vector<std::uint8_t> Take() &&
  {
    ++allocations;
    copied += size;
    return {data.get(), data.get() + size};
  }
};

struct Measurement
{
  std::string_view    name;
  std::vector<double> milliseconds;
  std::uint64_t       allocations{}, copied{};
};

int main(int argc, char** argv)
{
  if (argc != 2)
  {
    std::cerr << "Usage: Dreamsleeve.NifOutput.Benchmark <native.nif>\n";
    return 2;
  }
  std::ifstream input(argv[1], std::ios::binary);
  if (!input) return 2;
  const std::vector<std::uint8_t> bytes{std::istreambuf_iterator<char>(input), {}};
  if (!P::Nif::Inspect(bytes))
  {
    std::cerr << "Invalid NIF fixture\n";
    return 2;
  }
  std::array<Measurement, 3> results{
      {{"doubling-plus-copy"}, {"direct-first"}, {"direct-size-hint"}}
  };
  // Interleave orders to avoid giving one variant all cold/warm iterations.
  for (unsigned iteration = 0; iteration < 33; ++iteration)
    for (unsigned order = 0; order < results.size(); ++order)
    {
      const auto                variant = (iteration + order) % results.size();
      auto&                     result  = results[variant];
      std::vector<std::uint8_t> output;
      std::uint64_t             allocations{}, copied{};
      const auto                start  = std::chrono::steady_clock::now();
      auto                      replay = [&](auto write) {
        for (std::size_t at = 0; at < bytes.size();)
        {
          const auto size = std::min<std::size_t>(at < 4096 ? 4 : 65536, bytes.size() - at);
          write(std::span(bytes).subspan(at, size));
          at += size;
        }
      };
      if (variant == 0)
      {
        GrowingOutput buffer;
        replay([&](auto part) { buffer.Write(part); });
        output      = std::move(buffer).Take();
        allocations = buffer.allocations;
        copied      = buffer.copied;
      }
      else
      {
        P::NifOutput buffer(P::Limits{}.assetBytes, variant == 2 ? static_cast<std::uint32_t>(bytes.size()) : 0);
        auto*        previous = buffer.Bytes().data();
        allocations           = previous ? 1 : 0;
        replay([&](auto part) {
          const auto oldSize = buffer.Bytes().size();
          buffer.Write(part);
          if (buffer.Bytes().data() != previous)
          {
            ++allocations;
            copied   += oldSize;
            previous  = buffer.Bytes().data();
          }
        });
        output = std::move(buffer).Take();
      }
      const auto elapsed = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
      if (output != bytes)
      {
        std::cerr << "Byte mismatch\n";
        return 1;
      }
      if (iteration >= 3)
      {
        result.milliseconds.push_back(elapsed);
        result.allocations = allocations;
        result.copied      = copied;
      }
    }
  std::cout << "{\"scope\":\"detached-output-only\",\"bytes\":" << bytes.size() << ",\"iterations\":30,\"results\":[";
  for (std::size_t i = 0; i < results.size(); ++i)
  {
    auto& r = results[i];
    std::ranges::sort(r.milliseconds);
    if (i) std::cout << ',';
    std::cout << "{\"name\":\"" << r.name << "\",\"median_ms\":" << r.milliseconds[15] << ",\"p95_ms\":" << r.milliseconds[28]
              << ",\"allocations\":" << r.allocations << ",\"extra_copy_bytes\":" << r.copied << '}';
  }
  std::cout << "]}\n";
}
