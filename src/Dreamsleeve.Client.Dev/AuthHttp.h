#pragma once

#include <expected>
#include <string>
#include <string_view>

namespace Dreamsleeve::Client::Dev::Auth
{
  template<class T>
  using Result = std::expected<T, std::string>;

  struct Credentials
  {
    std::string username;
    std::string password;
  };

  // URL policy is shared by validation and HTTP; no DNS lookup or request here.
  Result<void> ValidateUrl(std::string_view url);
  Result<std::string> ReadPassword();
  Result<void> Register(std::string_view url, const Credentials& credentials, std::string_view displayName);
  Result<std::string> Login(std::string_view url, const Credentials& credentials);
}
