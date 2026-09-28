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

  export auto SetupLog() -> void
  {
    auto logs_folder = SKSE::log::log_directory();
    if (!logs_folder) SKSE::stl::report_and_fail("SKSE log_directory not provided, logs disabled.");

    auto plugin_name     = SKSE::PluginDeclaration::GetSingleton()->GetName();
    auto log_file_path   = *logs_folder / std::format("{}.log", plugin_name);
    auto file_logger_ptr = std::make_shared<spdlog::sinks::basic_file_sink_mt>(log_file_path.string(), true);
    auto logger_ptr      = std::make_shared<spdlog::logger>("log", std::move(file_logger_ptr));

    spdlog::set_default_logger(std::move(logger_ptr));
    spdlog::set_level(spdlog::level::debug);
    spdlog::flush_on(spdlog::level::trace);

    auto formatter = std::make_unique<spdlog::pattern_formatter>();
    formatter->add_flag<FormatterFlag>('*').set_pattern("[%H:%M:%S.%e][%s:%#]%*%v");
    spdlog::set_formatter(std::move(formatter));
    //spdlog::set_pattern("[%H:%M:%S.%e] %16s:%-5# | %v"); // %<x>s: x = # of characters in longest file name//https://github.com/gabime/spdlog/wiki/Custom-formatting
  }

}
