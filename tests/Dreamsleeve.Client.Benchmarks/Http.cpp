import std;
import Dreamsleeve.Client.Phantom.Http;
namespace P = Dreamsleeve::Client::Phantom;

int main(int argc, char** argv)
{
  if (argc != 3) return 2;
  std::ifstream input(argv[2], std::ios::binary);
  if (!input) return 3;
  std::vector<std::uint8_t> expected((std::istreambuf_iterator<char>(input)), {});
  P::Http                   http;
  http.Configure(argv[1], false);
  std::string token;
  while (std::getline(std::cin, token) && !token.empty())
  {
    std::string rate;
    if (!std::getline(std::cin, rate)) return 2;
    P::Wire::Transfer transfer{};
    transfer.transfer              = P::TransferId{1};
    transfer.request               = P::RequestId{1};
    transfer.asset.compressedBytes = static_cast<std::uint32_t>(expected.size());
    transfer.httpToken             = token;
    const auto    start            = std::chrono::steady_clock::now();
    std::uint32_t bytesPerSecond{};
    const auto    parsed = std::from_chars(rate.data(), rate.data() + rate.size(), bytesPerSecond);
    if (parsed.ec != std::errc{} || parsed.ptr != rate.data() + rate.size() || !bytesPerSecond) return 2;
    if (!http.Start(transfer, {}, bytesPerSecond)) return 4;
    while (std::chrono::steady_clock::now() - start < std::chrono::seconds(90))
    {
      auto completed = http.Poll();
      if (!completed.empty())
      {
        const auto& result = completed.front();
        const bool  valid  = !result.error && result.bytes && *result.bytes == expected;
        std::cout << std::format(
          "{{\"elapsedMs\":{},\"valid\":{}}}",
          std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count(),
          valid) << std::endl;
        if (!valid) return 5;
        break;
      }
      std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    if (std::chrono::steady_clock::now() - start >= std::chrono::seconds(90)) return 6;
  }
  return 0;
}
