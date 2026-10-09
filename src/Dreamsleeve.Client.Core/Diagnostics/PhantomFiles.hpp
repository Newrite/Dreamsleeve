#pragma once

namespace Dreamsleeve::Client::Diagnostics::Files
{

  // Native paths stay native for filesystem operations; only UI status uses UTF-8.
  inline Phantom::Result<std::string> DisplayPath(const std::filesystem::path& path)
  {
    try
    {
      const auto text = path.u8string();
      return std::string(reinterpret_cast<const char*>(text.data()), text.size());
    }
    catch (const std::system_error& error)
    {
      return std::unexpected(Phantom::Error{Phantom::Failure::Storage, error.code().message()});
    }
  }

  inline Phantom::Result<bool> IsRegularFile(const std::filesystem::path& path)
  {
    std::error_code error;
    const auto      regular = std::filesystem::is_regular_file(path, error);
    if (error && error != std::errc::no_such_file_or_directory && error != std::errc::not_a_directory)
      return std::unexpected(Phantom::Error{Phantom::Failure::Storage, error.message()});
    return regular;
  }

}
