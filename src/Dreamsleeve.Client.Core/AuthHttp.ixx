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
    RegistrationClosed,  // The server creates accounts only in its admin panel.
    Busy,
    Unavailable,
    InvalidResponse,
    CredentialStorage,
    Canceled,
    NameNotAllowed,        // Registration: the server word list refused a name.
    Banned,                // Sign-in and resume while a ban holds; see Failure::ban.
    RegistrationSteamOnly,  // New accounts come only from a Steam sign-in.
    AddressBanned,          // The server banned the IP range of this computer; see Failure::ban.
    DeviceBanned,           // An account ban covers this computer.
    SteamExpired            // The Steam sign-in was not finished in the browser in time.
  };

  // Who may create an account on the server (GET /auth/methods); Unknown until
  // the server answers or for a mode this client does not know.
  enum class RegistrationMode
  {
    Unknown,
    Open,    // Registration in the game and the first Steam sign-in.
    Steam,   // New accounts only from a Steam sign-in.
    Manual   // Administrators create accounts in the admin panel.
  };

  struct Methods
  {
    RegistrationMode registration{};
    bool             steam{};  // The server signs in through Steam.

    bool operator==(const Methods&) const = default;
  };

  // A Steam sign-in begun on the server: the browser opens page, the client
  // polls with flow and secret until the server says how it ended.
  struct SteamFlow
  {
    std::string          flow;
    std::string          secret;
    std::string          page;
    std::chrono::seconds lifetime{};
  };

  struct Failure
  {
    FailureCode code{};
    std::string message;
    // The ban that refused sign-in: its reason and end.
    std::optional<Domain::SessionEnd> ban;
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

  // device: the hash of this computer for the server (Device::Identify), absent when it has none.
  struct LoginRequest
  {
    std::string_view           username;
    std::string_view           password;
    bool                       rememberMe{};
    std::optional<std::string> device;
  };

  struct TokenRequest
  {
    std::string_view token;
  };

  struct ResumeRequest
  {
    std::string_view           token;
    std::optional<std::string> device;
  };

  struct ResetRequest
  {
    std::string_view code;
    std::string_view password;
  };

  struct RegisterRequest
  {
    std::string_view           username;
    std::string_view           displayName;
    std::string_view           password;
    std::optional<std::string> device;
  };

  struct SteamBeginRequest
  {
    bool                       rememberMe{};
    std::optional<std::string> device;
  };

  struct SteamPollRequest
  {
    std::string_view flow;
    std::string_view secret;
  };

  struct ErrorResponse
  {
    std::string code;
  };

  struct MethodsResponse
  {
    std::string registration;
    bool        steam{};
  };

  struct SteamBeginResponse
  {
    std::string   flow;
    std::string   secret;
    std::string   browserUrl;
    std::uint64_t expiresInSeconds{};
  };

  // 403 "banned" (the account) or "address_banned" (its IP range) of a sign-in or a registration.
  struct BanResponse
  {
    std::string                 code;
    std::string                 reason;
    std::optional<std::int64_t> untilUnixMs;
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

    // A request without a body is a GET.
    Result<HttpResponse> Send(std::string_view url, const wchar_t* path, const std::string& body, bool allowInsecureRemote = false)
    {
      auto endpoint = ParseUrl(url, allowInsecureRemote);
      if (!endpoint) return std::unexpected{endpoint.error()};
      if (body.size() > 16384) return std::unexpected{"Authentication request is too large"};

      Handle session{WinHttpOpen(
        L"Dreamsleeve.Client/" DREAMSLEEVE_VERSION,
        WINHTTP_ACCESS_TYPE_NO_PROXY,
        WINHTTP_NO_PROXY_NAME,
        WINHTTP_NO_PROXY_BYPASS,
        0)};
      if (!session) return SystemError("WinHttpOpen");
      if (!WinHttpSetTimeouts(session.get(), 5000, 5000, 5000, 5000)) return SystemError("Auth timeout configuration");
      Handle connection{WinHttpConnect(session.get(), endpoint->host.c_str(), endpoint->port, 0)};
      if (!connection) return SystemError("WinHttpConnect");
      Handle request{WinHttpOpenRequest(
        connection.get(),
        body.empty() ? L"GET" : L"POST",
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
      const auto     deadline = std::chrono::steady_clock::now() + std::chrono::seconds(15);
      const wchar_t* headers  = body.empty() ? L"Accept: application/json\r\n" : L"Content-Type: application/json\r\nAccept: application/json\r\n";
      if (
        !WinHttpSendRequest(
          request.get(),
          headers,
          static_cast<DWORD>(-1),
          body.empty() ? WINHTTP_NO_REQUEST_DATA : const_cast<char*>(body.data()),
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
           code == FailureCode::RegistrationClosed || code == FailureCode::RegistrationSteamOnly || code == FailureCode::Banned ||
           code == FailureCode::AddressBanned || code == FailureCode::DeviceBanned || code == FailureCode::SteamExpired;
  }

  // The answer of GET /auth/methods; a registration mode this client does not
  // know stays Unknown.
  export std::optional<Methods> DecodeMethods(std::string_view body)
  {
    MethodsResponse decoded;
    if (glz::read<glz::opts{.error_on_unknown_keys = false}>(decoded, body)) return std::nullopt;
    Methods result{.steam = decoded.steam};
    if (decoded.registration == "open") result.registration = RegistrationMode::Open;
    if (decoded.registration == "steam") result.registration = RegistrationMode::Steam;
    if (decoded.registration == "manual") result.registration = RegistrationMode::Manual;
    return result;
  }

  // The only page a Steam sign-in opens: Steam's OpenID login, so a server
  // cannot have the client open anything else. It becomes one command-line
  // argument: no spaces or quotes.
  export bool SteamPage(std::string_view url)
  {
    constexpr std::string_view Login = "https://steamcommunity.com/openid/login?";
    return url.size() > Login.size() && url.size() <= 4096 && url.starts_with(Login) &&
           std::ranges::all_of(url, [](unsigned char c) { return c > ' ' && c < 0x7f && c != '#' && c != '"'; });
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
        code = FailureCode::RegistrationClosed;
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

  // A 403 from a banned IP range or device: the reason, and for a range the end, shown like an account ban.
  std::optional<Failure> AddressBan(std::string_view body)
  {
    BanResponse ban;
    if (glz::read<glz::opts{.error_on_unknown_keys = false}>(ban, body)) return std::nullopt;
    if (ban.code == "device_banned") return Failure{FailureCode::DeviceBanned, ban.reason};
    if (ban.code != "address_banned") return std::nullopt;
    return Failure{
        FailureCode::AddressBanned,
        ban.reason,
        Domain::SessionEnd{Domain::SessionEndReason::AddressBanned, ban.reason, ban.untilUnixMs}
    };
  }

  export std::expected<void, Failure> RegisterAccount(
    std::string_view                  url,
    const Credentials&                credentials,
    std::string_view                  displayName,
    bool                              allowInsecureRemote = false,
    const std::optional<std::string>& device              = std::nullopt)
  {
    if (auto checked = ValidatePassword(credentials.password); !checked)
      return std::unexpected{
          Failure{FailureCode::InvalidRequest, checked.error()}
      };
    auto body = glz::write_json(RegisterRequest{credentials.username, displayName, credentials.password, device});
    if (!body)
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Cannot encode registration request"}
      };
    auto response = Send(url, L"/auth/register", *body, allowInsecureRemote);
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
    if (response->status == 403)
    {
      if (auto banned = AddressBan(response->body)) return std::unexpected{std::move(*banned)};
      ErrorResponse error;
      if (!glz::read<glz::opts{.error_on_unknown_keys = false}>(error, response->body) && error.code == "registration_steam_only")
        return std::unexpected{
            Failure{FailureCode::RegistrationSteamOnly, "Registration is open only through Steam"}
        };
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

  // A sign-in answer: 200 with the grant, a 403 ban or another failure.
  GrantResult DecodeGrant(HttpResponse& response)
  {
    if (response.status == 403)
    {
      if (auto banned = AddressBan(response.body)) return std::unexpected{std::move(*banned)};
      BanResponse ban;
      if (!glz::read<glz::opts{.error_on_unknown_keys = false}>(ban, response.body) && ban.code == "banned")
        return std::unexpected{
            Failure{FailureCode::Banned, ban.reason, Domain::SessionEnd{Domain::SessionEndReason::Banned, ban.reason, ban.untilUnixMs}}
        };
    }
    if (response.status != 200) return std::unexpected{HttpFailure(response.status)};

    LoginResponse decoded;
    const auto    error = glz::read<glz::opts{.error_on_unknown_keys = false}>(decoded, response.body);
    SecureZeroMemory(response.body.data(), response.body.size());
    if (
      error || decoded.playerId == Domain::InvalidId || decoded.expiresInSeconds == 0 || !ValidToken(decoded.sessionTicket) ||
      (!decoded.rememberToken.empty() && !ValidToken(decoded.rememberToken)))
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Invalid authentication response"}
      };
    return Grant{std::move(decoded.sessionTicket), std::move(decoded.rememberToken), std::move(decoded.username)};
  }

  GrantResult RequestGrant(std::string_view url, const wchar_t* path, std::string body, bool allowInsecureRemote = false)
  {
    auto response = Send(url, path, body, allowInsecureRemote);
    SecureZeroMemory(body.data(), body.size());
    if (!response)
      return std::unexpected{
          Failure{FailureCode::Unavailable, response.error()}
      };
    return DecodeGrant(*response);
  }

  export GrantResult LoginGrant(
    std::string_view                  url,
    const Credentials&                credentials,
    bool                              remember,
    bool                              allowInsecureRemote = false,
    const std::optional<std::string>& device              = std::nullopt)
  {
    if (auto checked = ValidatePassword(credentials.password); !checked)
      return std::unexpected{
          Failure{FailureCode::InvalidCredentials, checked.error()}
      };
    auto body = glz::write_json(LoginRequest{credentials.username, credentials.password, remember, device});
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

  export GrantResult Resume(
    std::string_view                  url,
    std::string_view                  token,
    bool                              allowInsecureRemote = false,
    const std::optional<std::string>& device              = std::nullopt)
  {
    auto body = glz::write_json(ResumeRequest{token, device});
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
    auto response = Send(url, path, body, allowInsecureRemote);
    SecureZeroMemory(body.data(), body.size());
    if (!response)
      return std::unexpected{
          Failure{FailureCode::Unavailable, response.error()}
      };
    if (response->status != 204) return std::unexpected{HttpFailure(response->status)};
    return {};
  }

  // Who may register and whether Steam sign-in is on.
  export std::expected<Methods, Failure> ReadMethods(std::string_view url, bool allowInsecureRemote = false)
  {
    auto response = Send(url, L"/auth/methods", {}, allowInsecureRemote);
    if (!response)
      return std::unexpected{
          Failure{FailureCode::Unavailable, response.error()}
      };
    if (response->status != 200) return std::unexpected{HttpFailure(response->status)};
    auto methods = DecodeMethods(response->body);
    if (!methods)
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Invalid authentication methods"}
      };
    return *methods;
  }

  // Starts a Steam sign-in; the page goes to the browser (OpenSteamPage).
  export std::expected<SteamFlow, Failure> BeginSteam(
    std::string_view                  url,
    bool                              remember,
    bool                              allowInsecureRemote = false,
    const std::optional<std::string>& device              = std::nullopt)
  {
    auto body = glz::write_json(SteamBeginRequest{remember, device});
    if (!body)
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Cannot encode Steam sign-in request"}
      };
    auto response = Send(url, L"/auth/steam/begin", *body, allowInsecureRemote);
    if (!response)
      return std::unexpected{
          Failure{FailureCode::Unavailable, response.error()}
      };
    if (response->status == 403)
      if (auto banned = AddressBan(response->body)) return std::unexpected{std::move(*banned)};
    if (response->status == 404)
      return std::unexpected{
          Failure{FailureCode::Unavailable, "Steam sign-in is not enabled on this server"}
      };
    if (response->status != 200) return std::unexpected{HttpFailure(response->status)};
    SteamBeginResponse decoded;
    if (
      glz::read<glz::opts{.error_on_unknown_keys = false}>(decoded, response->body) || !ValidToken(decoded.flow) ||
      !ValidToken(decoded.secret) || !SteamPage(decoded.browserUrl) || decoded.expiresInSeconds == 0 || decoded.expiresInSeconds > 3600)
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Invalid Steam sign-in response"}
      };
    return SteamFlow{
        std::move(decoded.flow),
        std::move(decoded.secret),
        std::move(decoded.browserUrl),
        std::chrono::seconds{decoded.expiresInSeconds}
    };
  }

  // How a Steam sign-in ended; empty while the player is still in the browser.
  export std::expected<std::optional<Grant>, Failure> PollSteam(
    std::string_view url,
    std::string_view flow,
    std::string_view secret,
    bool             allowInsecureRemote = false)
  {
    auto body = glz::write_json(SteamPollRequest{flow, secret});
    if (!body)
      return std::unexpected{
          Failure{FailureCode::InvalidResponse, "Cannot encode Steam sign-in poll"}
      };
    auto response = Send(url, L"/auth/steam/poll", *body, allowInsecureRemote);
    SecureZeroMemory(body->data(), body->size());
    if (!response)
      return std::unexpected{
          Failure{FailureCode::Unavailable, response.error()}
      };
    if (response->status == 202) return std::optional<Grant>{};
    if (response->status == 410)
      return std::unexpected{
          Failure{FailureCode::SteamExpired, "The Steam sign-in expired"}
      };
    auto grant = DecodeGrant(*response);
    if (!grant) return std::unexpected{std::move(grant.error())};
    return std::optional<Grant>{std::move(*grant)};
  }

  // The default browser opens the Steam page; nothing else is opened. A
  // separate process asks the shell, so nothing in the game process (COM
  // without a message loop, shell extensions, overlay hooks) holds up sign-in.
  export Result<void> OpenSteamPage(std::string_view page)
  {
    if (!SteamPage(page)) return std::unexpected{"Not a Steam sign-in page"};
    auto url = Wide(page);
    if (!url) return std::unexpected{url.error()};
    wchar_t    system[MAX_PATH];
    const UINT length = GetSystemDirectoryW(system, MAX_PATH);
    if (length == 0 || length >= MAX_PATH) return SystemError("System directory lookup");
    const std::wstring  program = std::wstring{system, length} + L"\\rundll32.exe";
    std::wstring        command = L"\"" + program + L"\" url.dll,FileProtocolHandler " + *url;
    STARTUPINFOW        startup{.cb = sizeof(STARTUPINFOW)};
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(program.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process))
      return SystemError("Opening the browser");
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
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
