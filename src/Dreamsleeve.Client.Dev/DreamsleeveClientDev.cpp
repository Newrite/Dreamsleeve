#include <memory>
#include <vector>
#include <filesystem>
#include <string_view>
#include <expected>
#include <iostream>
#include <string>

#include <spdlog/spdlog.h>
#include <spdlog/sinks/stdout_color_sinks.h>
#include <spdlog/sinks/rotating_file_sink.h>

int RunStateConsole(bool demo);
int RunMovementDemo();
int RunMovementBenchmark();
int RunNetworkConsole(int argc, char* argv[]);

std::expected<void, std::string> InitializeLogging()
{
  std::error_code error;
  std::filesystem::create_directories("logs", error);
  if (error) return std::unexpected(error.message());

  auto consoleSink = std::make_shared<spdlog::sinks::stdout_color_sink_mt>();
  consoleSink->set_level(spdlog::level::info);
  consoleSink->set_pattern("[%H:%M:%S] [%^%l%$] [%n] %v");

  std::shared_ptr<spdlog::sinks::rotating_file_sink_mt> fileSink;
  try
  {
    fileSink = std::make_shared<spdlog::sinks::rotating_file_sink_mt>(
      "logs/dreamsleeve-client-dev.log",
      1024 * 1024 * 5,
      3);  // 5 MB; keep three files.
  }
  catch (const spdlog::spdlog_ex& failure)
  {
    return std::unexpected(std::string(failure.what()));
  }
  fileSink->set_level(spdlog::level::trace);
  fileSink->set_pattern("[%Y-%m-%d %H:%M:%S.%e] [%l] [%n] [thread %t] %v");

  std::vector<spdlog::sink_ptr> sinks{consoleSink, fileSink};

  auto logger = std::make_shared<spdlog::logger>("client", sinks.begin(), sinks.end());

  logger->set_level(spdlog::level::trace);
  logger->flush_on(spdlog::level::warn);

  spdlog::set_default_logger(logger);
  spdlog::set_pattern("[%H:%M:%S] [%^%l%$] [%n] %v");

  spdlog::info("spdlog initialized");
  return {};
}

void ShutdownLogger() noexcept
{
  spdlog::shutdown();
}

int main(int argc, char* argv[])
{
  if (const auto logging = InitializeLogging(); !logging)
  {
    std::cerr << "Cannot initialize client logging: " << logging.error() << '\n';
    return 1;
  }

  if (argc == 2 && std::string_view{argv[1]} == "--movement-benchmark")
  {
    const int result = RunMovementBenchmark();
    ShutdownLogger();
    return result;
  }

  if (argc == 2 && std::string_view{argv[1]} == "--movement-demo")
  {
    const int result = RunMovementDemo();
    ShutdownLogger();
    return result;
  }

  if (argc >= 2 && (std::string_view{argv[1]} == "--connect" || std::string_view{argv[1]} == "--config"))
  {
    const int result = RunNetworkConsole(argc, argv);
    ShutdownLogger();
    return result;
  }

  if (argc > 2 || (argc == 2 && std::string_view{argv[1]} != "--state-demo"))
  {
    spdlog::error(
      "Usage: Dreamsleeve.Client.Dev [--state-demo | --movement-demo | --movement-benchmark] | --connect <host> <port> [username] [--config <path>] [--auth-url <origin>] [--register <displayName>] [--remember] [--hide | --hide-except-marks] | --config <path> [username] [--remember] [--hide | --hide-except-marks]");
    ShutdownLogger();
    return 2;
  }

  const int result = RunStateConsole(argc == 2);
  ShutdownLogger();
  return result;
}
