module;

#include "Prelude.hpp"
#include "spdlog/pattern_formatter.h"
#include "spdlog/sinks/basic_file_sink.h"

export module Dreamsleeve.Logging;

import std;

namespace Logging
{

  class FormatterFlag : public spdlog::custom_flag_formatter
  {
public:

    void format(const spdlog::details::log_msg& msg, const std::tm&, spdlog::memory_buf_t& dest) override
    {
      size_t longestFileName       = "DamageResistSystemSettingsTest.ixx"s.size();
      size_t maxDigitsInLineNumber = 5;
      size_t digitsInLineNumber    = std::to_string(msg.source.line).size();

      std::string filepath(msg.source.filename);
      std::string filename(filepath.substr(filepath.rfind("\\") + 1));

      longestFileName -= filename.size() - (maxDigitsInLineNumber - digitsInLineNumber);

      std::string whitespace(longestFileName, ' ');
      dest.append(whitespace.data(), whitespace.data() + whitespace.size());
    }

    std::unique_ptr<custom_flag_formatter> clone() const override
    {
      return spdlog::details::make_unique<FormatterFlag>();
    }
  };

  export enum class ErrorKind
  {
    MissingDirectory,
    PathEncoding,
    Sink
  };

  export struct Error
  {
    ErrorKind   kind;
    std::string detail;
  };

  std::expected<std::string, Error> LogFileName(const std::filesystem::path& path)
  {
    // MSVC path.string() can reject a native filename in the current code page.
    try
    {
      return path.string();
    }
    catch (const std::system_error& failure)
    {
      return std::unexpected(Error{ErrorKind::PathEncoding, std::format("Cannot encode the SKSE log filename: {}", failure.what())});
    }
  }

  std::expected<std::shared_ptr<spdlog::sinks::basic_file_sink_mt>, Error> OpenLog(const std::string& filename)
  {
    // The sink dependency reports ordinary directory/open failures by exception.
    try
    {
      return std::make_shared<spdlog::sinks::basic_file_sink_mt>(filename, true);
    }
    catch (const spdlog::spdlog_ex& failure)
    {
      return std::unexpected(Error{ErrorKind::Sink, std::format("Cannot open the SKSE log: {}", failure.what())});
    }
  }

  export auto SetupLog() -> std::expected<void, Error>
  {
    auto logs_folder = SKSE::log::log_directory();
    if (!logs_folder) return std::unexpected(Error{ErrorKind::MissingDirectory, "SKSE log directory is unavailable."});

    auto plugin_name   = SKSE::PluginDeclaration::GetSingleton()->GetName();
    auto log_file_path = *logs_folder / std::format("{}.log", plugin_name);
    auto filename      = LogFileName(log_file_path);
    if (!filename) return std::unexpected(std::move(filename.error()));
    auto sink = OpenLog(*filename);
    if (!sink) return std::unexpected(std::move(sink.error()));

    auto logger_ptr = std::make_shared<spdlog::logger>("log", std::move(*sink));
    auto formatter  = std::make_unique<spdlog::pattern_formatter>();
    formatter->add_flag<FormatterFlag>('*').set_pattern("[%H:%M:%S.%e][%s:%#]%*%v");
    logger_ptr->set_formatter(std::move(formatter));

    spdlog::set_default_logger(std::move(logger_ptr));
    spdlog::set_level(spdlog::level::debug);
    spdlog::flush_on(spdlog::level::trace);
    return {};
  }

}
