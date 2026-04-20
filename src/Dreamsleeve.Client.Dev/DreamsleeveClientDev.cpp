#include <memory>
#include <vector>
#include <filesystem>

#include <spdlog/spdlog.h>
#include <spdlog/sinks/stdout_color_sinks.h>
#include <spdlog/sinks/rotating_file_sink.h>

import DreamNet.Host;

void InitializeLogging()
{
    std::filesystem::create_directories("logs");

    auto consoleSink = std::make_shared<spdlog::sinks::stdout_color_sink_mt>();
    consoleSink->set_level(spdlog::level::info);
    consoleSink->set_pattern("[%H:%M:%S] [%^%l%$] [%n] %v");

    auto fileSink = std::make_shared<spdlog::sinks::rotating_file_sink_mt>(
        "logs/dreamsleeve-client-dev.log",
        1024 * 1024 * 5, // 5 MB
        3 // keep 3 files
    );
    fileSink->set_level(spdlog::level::trace);
    fileSink->set_pattern("[%Y-%m-%d %H:%M:%S.%e] [%l] [%n] [thread %t] %v");

    std::vector<spdlog::sink_ptr> sinks{consoleSink, fileSink};

    auto logger = std::make_shared<spdlog::logger>(
        "client",
        sinks.begin(),
        sinks.end()
    );

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
    
    auto enetClient = DreamNetHost::TryCreateServer(ServerConfig::Default());
    
    spdlog::info("Client starting...");
    spdlog::warn("This goes to console and file");
    
    ShutdownLogger();
    return 0;
}
