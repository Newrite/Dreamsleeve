module;
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <winhttp.h>
#include <glaze/glaze.hpp>

export module Dreamsleeve.Client.Auth;

import std;
import Dreamsleeve.Client.Domain;

export namespace Dreamsleeve::Client::Auth
{

  template <class T>
  using Result = std::expected<T, std::string>;

  enum class FailureCode
  {
    None,
    InvalidCredentials,
    UsernameTaken,
    InvalidRequest,
    RegistrationDisabled,
    Busy,
    Unavailable,
    InvalidResponse,
    CredentialStorage,
    Canceled,
    NameNotAllowed  // Registration: the server word list refused a name.
  };

  struct Failure
  {
    FailureCode code{};
    std::string message;
  };

  struct Grant
  {
    std::string sessionTicket;
    std::string rememberToken;
    std::string username;
  };

  using GrantResult = std::expected<Grant, Failure>;

  struct Credentials
  {
    std::string username;
    std::string password;
  };

}

namespace Dreamsleeve::Client::Auth
{

  struct LoginRequest
  {
    std::string_view username;
    std::string_view password;
    bool             rememberMe{};
  };

  struct TokenRequest
  {
    std::string_view token;
  };

  struct ResetRequest
  {
    std::string_view code;
    std::string_view password;
  };

  struct RegisterRequest
  {
    std::string_view username;
    std::string_view displayName;
    std::string_view password;
  };

  struct ErrorResponse
  {
    std::string code;
  };

  struct LoginResponse
  {
    std::string   sessionTicket;
    std::uint64_t expiresInSeconds{};
    std::uint64_t playerId{};
    std::string   rememberToken;
    std::string   username;
  };

  namespace
  {

    struct HandleCloser
    {
      void operator()(void* value) const noexcept
      {
        if (value) WinHttpCloseHandle(value);
      }
    };

    using Handle = std::unique_ptr<void, HandleCloser>;

    struct Endpoint
    {
      std::wstring  host;
      INTERNET_PORT port{};
      bool          secure{};
    };

    struct HttpResponse
    {
      DWORD       status{};
      std::string body;
    };

    auto SystemError(std::string_view operation)
    {
      return std::unexpected{std::string(operation) + " failed (Windows " + std::to_string(GetLastError()) + ")"};
    }

    Result<std::wstring> Wide(std::string_view text)
    {
      if (text.size() > 8192) return std::unexpected{"Auth URL is too long"};
      const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0);
      if (count == 0) return SystemError("UTF-8 URL conversion");
      std::wstring result(count, L'\0');
      if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), result.data(), count) == 0)
        return SystemError("UTF-8 URL conversion");
      return result;
    }

    Result<Endpoint> ParseUrl(std::string_view url, bool allowInsecureRemote = false)
    {
      auto wide = Wide(url);
      if (!wide) return std::unexpected{wide.error()};
      if (wide->find(L'\0') != std::wstring::npos) return std::unexpected{"Invalid auth URL"};

      URL_COMPONENTS parts{};
      parts.dwStructSize   = sizeof(parts);
      parts.dwSchemeLength = parts.dwHostNameLength = parts.dwUserNameLength = parts.dwPasswordLength = parts.dwUrlPathLength =
        parts.dwExtraInfoLength                                                                       = static_cast<DWORD>(-1);
      if (!WinHttpCrackUrl(wide->c_str(), static_cast<DWORD>(wide->size()), 0, &parts)) return SystemError("Auth URL parsing");
      if (parts.dwUserNameLength != 0 || parts.dwPasswordLength != 0 || parts.dwExtraInfoLength != 0 || parts.dwHostNameLength == 0)
        return std::unexpected{"Auth URL must not contain credentials, query or fragment"};
      const std::wstring_view path{parts.lpszUrlPath, parts.dwUrlPathLength};
      if (!path.empty() && path != L"/") return std::unexpected{"Auth URL must be an origin without a path"};
      if (parts.nScheme != INTERNET_SCHEME_HTTP && parts.nScheme != INTERNET_SCHEME_HTTPS)
        return std::unexpected{"Auth URL requires HTTP or HTTPS"};

      Endpoint result{
          std::wstring{parts.lpszHostName, parts.dwHostNameLength},
          parts.nPort,
          parts.nScheme == INTERNET_SCHEME_HTTPS
      };
      if (!result.secure)
      {
        if (_wcsicmp(result.host.c_str(), L"localhost") == 0) result.host = L"127.0.0.1";
        if (!allowInsecureRemote && result.host != L"127.0.0.1" && result.host != L"::1" && result.host != L"[::1]")
          return std::unexpected{"Plain HTTP authentication is permitted only on loopback; use HTTPS remotely"};
      }
      return result;
    }

    Result<HttpResponse> Post(std::string_view url, const wchar_t* path, const std::string& body, bool allowInsecureRemote = false)
    {
      auto endpoint = ParseUrl(url, allowInsecureRemote);
      if (!endpoint) return std::unexpected{endpoint.error()};
      if (body.size() > 16384) return std::unexpected{"Authentication request is too large"};

      Handle session{WinHttpOpen(L"Dreamsleeve.Client/6", WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0)};
      if (!session) return SystemError("WinHttpOpen");
      if (!WinHttpSetTimeouts(session.get(), 5000, 5000, 5000, 5000)) return SystemError("Auth timeout configuration");
      Handle connection{WinHttpConnect(session.get(), endpoint->host.c_str(), endpoint->port, 0)};
      if (!connection) return SystemError("WinHttpConnect");
      Handle request{WinHttpOpenRequest(
        connection.get(),
        L"POST",
        path,
        nullptr,
        WINHTTP_NO_REFERER,
        WINHTTP_DEFAULT_ACCEPT_TYPES,
        endpoint->secure ? WINHTTP_FLAG_SECURE : 0)};
      if (!request) return SystemError("WinHttpOpenRequest");

      DWORD redirects = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
      DWORD disabled  = WINHTTP_DISABLE_COOKIES | WINHTTP_DISABLE_AUTHENTICATION;
      if (
        !WinHttpSetOption(request.get(), WINHTTP_OPTION_REDIRECT_POLICY, &redirects, sizeof(redirects)) ||
        !WinHttpSetOption(request.get(), WINHTTP_OPTION_DISABLE_FEATURE, &disabled, sizeof(disabled)))
        return SystemError("Auth request policy");
      // HTTPS uses WinHTTP's normal certificate and hostname validation.
      const auto        deadline  = std::chrono::steady_clock::now() + std::chrono::seconds(15);
      constexpr wchar_t headers[] = L"Content-Type: application/json\r\nAccept: application/json\r\n";
      if (
        !WinHttpSendRequest(
          request.get(),
          headers,
          static_cast<DWORD>(-1),
          const_cast<char*>(body.data()),
          static_cast<DWORD>(body.size()),
          static_cast<DWORD>(body.size()),
          0) ||
        !WinHttpReceiveResponse(request.get(), nullptr))
        return SystemError("Authentication request");

      HttpResponse result;
      DWORD        statusSize = sizeof(result.status);
      if (!WinHttpQueryHeaders(
            request.get(),
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX,
            &result.status,
            &statusSize,
            WINHTTP_NO_HEADER_INDEX))
        return SystemError("Authentication status");
      for (;;)
      {
        if (std::chrono::steady_clock::now() >= deadline) return std::unexpected{"Authentication response timed out"};
        char  chunk[4096];
        DWORD read{};
        if (!WinHttpReadData(request.get(), chunk, sizeof(chunk), &read)) return SystemError("Authentication response");
        if (read == 0) break;
        if (result.body.size() + read > 16384) return std::unexpected{"Authentication response is too large"};
        result.body.append(chunk, read);
      }
      return result;
    }

  }

  // A session ticket or a saved login token: 32 random bytes in unpadded base64url.
  export bool ValidToken(std::string_view token)
  {
    constexpr std::size_t Length = 43;
    return token.size() == Length && std::ranges::all_of(token, [](unsigned char c) {
             return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
           });
  }

  // Failures only the player can resolve: retrying with the same saved login
  // cannot succeed. Transport and server trouble may pass and are retried.
  export bool NeedsUser(FailureCode code)
  {
    return code == FailureCode::InvalidCredentials || code == FailureCode::CredentialStorage || code == FailureCode::InvalidRequest ||
           code == FailureCode::RegistrationDisabled;
  }

  export Result<void> ValidatePassword(std::string_view password)
  {
    if (password.size() < 12 || password.size() > 128) return std::unexpected{"Password must be 12 to 128 UTF-8 bytes"};
    return {};
  }

  // Same URL policy for configuration checks and actual HTTP requests.
  export Result<void> ValidateUrl(std::string_view url, bool allowInsecureRemote = false)
  {
    auto parsed = ParseUrl(url, allowInsecureRemote);
    if (!parsed) return std::unexpected{parsed.error()};
    return {};
  }

  export Result<std::wstring> CredentialTarget(std::string_view url, bool allowInsecureRemote = false)
  {
    auto endpoint = ParseUrl(url, allowInsecureRemote);
    if (!endpoint) return std::unexpected{endpoint.error()};
    for (auto& c : endpoint->host)
      if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
    return L"Dreamsleeve/Auth/v1/" + std::wstring(endpoint->secure ? L"https/" : L"http/") + endpoint->host + L"/" +
           std::to_wstring(endpoint->port);
  }

  Failure HttpFailure(DWORD status)
  {
    FailureCode code = FailureCode::Unavailable;
    switch (status)
    {
      case 400:
        code = FailureCode::InvalidRequest;
        break;
      case 401:
        code = FailureCode::InvalidCredentials;
        break;
      case 403:
        code = FailureCode::RegistrationDisabled;
        break;
      case 409:
        code = FailureCode::UsernameTaken;
        break;
      case 429:
        code = FailureCode::Busy;
        break;
    }
    return {code, "Authentication failed (HTTP " + std::to_string(status) + ")"};
  }

  export std::expected<void, Failure> RegisterAccount(
    std::string_view   url,
    const Credentials& credentials,
    std::string_view   displayName,
    bool               allowInsecureRemote = false)
  {
    if (auto checked = ValidatePassword(credentials.password); !checked)
      return std::unexpected{
          Failure{FailureCode::InvalidRequest, checked.error()}
      };
    auto body = glz::write_json(RegisterRequest{credentials.username, displayName, credentials.password});
    if (!body)
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Cannot encode registration request"}
      };
    auto response = Post(url, L"/auth/register", *body, allowInsecureRemote);
    SecureZeroMemory(body->data(), body->size());
    if (!response)
      return std::unexpected{
          Failure{FailureCode::Unavailable, response.error()}
      };
    if (response->status == 400)
    {
      ErrorResponse error;
      if (!glz::read<glz::opts{.error_on_unknown_keys = false}>(error, response->body))
      {
        if (error.code == "username_not_allowed")
          return std::unexpected{
              Failure{FailureCode::NameNotAllowed, "Username contains words that are not allowed"}
          };
        if (error.code == "display_name_not_allowed")
          return std::unexpected{
              Failure{FailureCode::NameNotAllowed, "Display name contains words that are not allowed"}
          };
      }
    }
    if (response->status != 201) return std::unexpected{HttpFailure(response->status)};
    return {};
  }

  export Result<void> Register(
    std::string_view   url,
    const Credentials& credentials,
    std::string_view   displayName,
    bool               allowInsecureRemote = false)
  {
    auto result = RegisterAccount(url, credentials, displayName, allowInsecureRemote);
    if (!result) return std::unexpected{result.error().message};
    return {};
  }

  GrantResult RequestGrant(std::string_view url, const wchar_t* path, std::string body, bool allowInsecureRemote = false)
  {
    auto response = Post(url, path, body, allowInsecureRemote);
    SecureZeroMemory(body.data(), body.size());
    if (!response)
      return std::unexpected{
          Failure{FailureCode::Unavailable, response.error()}
      };
    if (response->status != 200) return std::unexpected{HttpFailure(response->status)};

    LoginResponse decoded;
    const auto    error = glz::read<glz::opts{.error_on_unknown_keys = false}>(decoded, response->body);
    SecureZeroMemory(response->body.data(), response->body.size());
    if (
      error || decoded.playerId == Domain::InvalidId || decoded.expiresInSeconds == 0 || !ValidToken(decoded.sessionTicket) ||
      (!decoded.rememberToken.empty() && !ValidToken(decoded.rememberToken)))
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Invalid authentication response"}
      };
    return Grant{std::move(decoded.sessionTicket), std::move(decoded.rememberToken), std::move(decoded.username)};
  }

  export GrantResult LoginGrant(std::string_view url, const Credentials& credentials, bool remember, bool allowInsecureRemote = false)
  {
    if (auto checked = ValidatePassword(credentials.password); !checked)
      return std::unexpected{
          Failure{FailureCode::InvalidCredentials, checked.error()}
      };
    auto body = glz::write_json(LoginRequest{credentials.username, credentials.password, remember});
    if (!body)
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Cannot encode login request"}
      };
    auto grant = RequestGrant(url, L"/auth/login", std::move(*body), allowInsecureRemote);
    if (grant && remember && grant->rememberToken.empty())
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Server did not issue a saved login token"}
      };
    return grant;
  }

  export GrantResult Resume(std::string_view url, std::string_view token, bool allowInsecureRemote = false)
  {
    auto body = glz::write_json(TokenRequest{token});
    if (!body)
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Cannot encode resume request"}
      };
    return RequestGrant(url, L"/auth/resume", std::move(*body), allowInsecureRemote);
  }

  std::expected<void, Failure> RequestCompletion(
    std::string_view url,
    const wchar_t*   path,
    std::string      body,
    bool             allowInsecureRemote = false)
  {
    auto response = Post(url, path, body, allowInsecureRemote);
    SecureZeroMemory(body.data(), body.size());
    if (!response)
      return std::unexpected{
          Failure{FailureCode::Unavailable, response.error()}
      };
    if (response->status != 204) return std::unexpected{HttpFailure(response->status)};
    return {};
  }

  export std::expected<void, Failure> Logout(std::string_view url, std::string_view token, bool allowInsecureRemote = false)
  {
    auto body = glz::write_json(TokenRequest{token});
    if (!body)
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Cannot encode logout request"}
      };
    return RequestCompletion(url, L"/auth/logout", std::move(*body), allowInsecureRemote);
  }

  export std::expected<void, Failure> ResetPassword(
    std::string_view url,
    std::string_view code,
    std::string_view password,
    bool             allowInsecureRemote = false)
  {
    if (auto checked = ValidatePassword(password); !checked)
      return std::unexpected{
          Failure{FailureCode::InvalidCredentials, checked.error()}
      };
    auto body = glz::write_json(ResetRequest{code, password});
    if (!body)
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Cannot encode password reset"}
      };
    return RequestCompletion(url, L"/auth/reset-password", std::move(*body), allowInsecureRemote);
  }

}
