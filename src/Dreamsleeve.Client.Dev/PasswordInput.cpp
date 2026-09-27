#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
import std;
import Dreamsleeve.Client.Auth;

namespace Dreamsleeve::Client::Dev
{

  using Auth::Result;

  namespace
  {

    auto SystemError(std::string_view operation)
    {
      return std::unexpected{std::string(operation) + " failed (Windows " + std::to_string(GetLastError()) + ")"};
    }

    Result<std::string> PasswordUtf8(std::wstring& value)
    {
      const int count =
        WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(), static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
      std::string result(count, '\0');
      const bool  converted = count > 0 && WideCharToMultiByte(
                                             CP_UTF8,
                                             WC_ERR_INVALID_CHARS,
                                             value.data(),
                                             static_cast<int>(value.size()),
                                             result.data(),
                                             count,
                                             nullptr,
                                             nullptr) != 0;
      SecureZeroMemory(value.data(), value.size() * sizeof(wchar_t));
      if (!converted) return std::unexpected{"Password must contain valid Unicode"};
      if (auto checked = Auth::ValidatePassword(result); !checked) return std::unexpected{checked.error()};
      return result;
    }

  }

  Result<std::string> ReadPassword()
  {
    SetLastError(ERROR_SUCCESS);
    const DWORD required = GetEnvironmentVariableW(L"DREAMSLEEVE_PASSWORD", nullptr, 0);
    if (required > 0)
    {
      if (required > 129) return std::unexpected{"Password exceeds the maximum length"};
      std::wstring value(required, L'\0');
      const DWORD  read = GetEnvironmentVariableW(L"DREAMSLEEVE_PASSWORD", value.data(), required);
      if (read == 0 || read >= required) return SystemError("Password environment read");
      value.resize(read);
      return PasswordUtf8(value);
    }

    if (GetLastError() == ERROR_SUCCESS) return std::unexpected{"Password environment value is empty"};

    const HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
    DWORD        mode{};
    if (!GetConsoleMode(input, &mode)) return std::unexpected{"Set DREAMSLEEVE_PASSWORD for redirected input"};
    if (!SetConsoleMode(input, mode & ~ENABLE_ECHO_INPUT)) return SystemError("Password prompt");
    std::cout << "Password: " << std::flush;
    std::wstring value(130, L'\0');
    DWORD        read{};
    const bool   readOk = ReadConsoleW(input, value.data(), static_cast<DWORD>(value.size()), &read, nullptr) != 0;
    SetConsoleMode(input, mode);
    std::cout << '\n';
    if (!readOk) return SystemError("Password input");
    if (read == value.size()) return std::unexpected{"Password exceeds the maximum length"};
    value.resize(read);
    while (!value.empty() && (value.back() == L'\r' || value.back() == L'\n'))
      value.pop_back();
    return PasswordUtf8(value);
  }

}
