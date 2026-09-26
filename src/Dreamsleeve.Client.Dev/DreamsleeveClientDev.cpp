#include <memory>
#include <vector>
#include <filesystem>
#include <string_view>

#include <spdlog/spdlog.h>
#include <spdlog/sinks/stdout_color_sinks.h>
#include <spdlog/sinks/rotating_file_sink.h>

int RunStateConsole(bool demo);
int RunNetworkConsole(int argc, char* argv[]);

void InitializeLogging()
{
  std::filesystem::create_directories("logs");

  auto consoleSink = std::make_shared<spdlog::sinks::stdout_color_sink_mt>();
  consoleSink->set_level(spdlog::level::info);
  consoleSink->set_pattern("[%H:%M:%S] [%^%l%$] [%n] %v");

  auto fileSink = std::make_shared<spdlog::sinks::rotating_file_sink_mt>(
    "logs/dreamsleeve-client-dev.log",
    1024 * 1024 * 5,  // 5 MB
    3                 // keep 3 files
  );
  fileSink->set_level(spdlog::level::trace);
  fileSink->set_pattern("[%Y-%m-%d %H:%M:%S.%e] [%l] [%n] [thread %t] %v");

  std::vector<spdlog::sink_ptr> sinks{consoleSink, fileSink};

  auto logger = std::make_shared<spdlog::logger>("client", sinks.begin(), sinks.end());

  logger->set_level(spdlog::level::trace);
  logger->flush_on(spdlog::level::warn);

  spdlog::set_default_logger(logger);
  spdlog::set_pattern("[%H:%M:%S] [%^%l%$] [%n] %v");

  spdlog::info("spdlog initialized");
}

void ShutdownLogger() noexcept
{
  spdlog::shutdown();
}

int main(int argc, char* argv[])
{
  InitializeLogging();

  if (argc >= 2 && std::string_view{argv[1]} == "--connect")
  {
    const int result = RunNetworkConsole(argc, argv);
    ShutdownLogger();
    return result;
  }
  if (argc > 2 || (argc == 2 && std::string_view{argv[1]} != "--state-demo"))
  {
    spdlog::error("Usage: Dreamsleeve.Client.Dev [--state-demo] | --connect <IPv4> <port> <username> [displayName]");
    ShutdownLogger();
    return 2;
  }
  const int result = RunStateConsole(argc == 2);
  ShutdownLogger();
  return result;
}
