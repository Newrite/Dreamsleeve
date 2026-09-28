module;
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <wincred.h>
#include <glaze/glaze.hpp>

export module Dreamsleeve.Client.CredentialStore;
import std;
import Dreamsleeve.Client.Auth;

export namespace Dreamsleeve::Client::CredentialStore
{

  struct SavedLogin
  {
    std::string username;
    std::string token;
  };

  template <class T>
  using Result = std::expected<T, Auth::Failure>;

  auto Error(std::string message)
  {
    return std::unexpected{
        Auth::Failure{Auth::FailureCode::CredentialStorage, std::move(message)}
    };
  }

  Result<std::optional<SavedLogin>> Load(std::string_view origin)
  {
    auto target = Auth::CredentialTarget(origin);
    if (!target) return Error(target.error());
    PCREDENTIALW credential{};
    if (!CredReadW(target->c_str(), CRED_TYPE_GENERIC, 0, &credential))
    {
      if (GetLastError() == ERROR_NOT_FOUND) return std::nullopt;
      return Error("Cannot read Windows Credential Manager");
    }
    auto release = [](CREDENTIALW* value) {
      if (value->CredentialBlob) SecureZeroMemory(value->CredentialBlob, value->CredentialBlobSize);
      CredFree(value);
    };
    std::unique_ptr<CREDENTIALW, decltype(release)> owned{credential, release};
    SavedLogin                                      saved;
    const std::string_view bytes{reinterpret_cast<char*>(credential->CredentialBlob), credential->CredentialBlobSize};
    if (glz::read_json(saved, bytes) || saved.token.size() != 43) return Error("Invalid saved credential; forget it and sign in again");
    return saved;
  }

  Result<void> Save(std::string_view origin, const SavedLogin& saved)
  {
    if (saved.token.size() != 43) return Error("Invalid saved login token");
    auto target = Auth::CredentialTarget(origin);
    if (!target) return Error(target.error());
    auto blob = glz::write_json(saved);
    if (!blob) return Error("Cannot encode saved login");
    if (blob->size() > CRED_MAX_CREDENTIAL_BLOB_SIZE) return Error("Saved login is too large");
    CREDENTIALW credential{};
    credential.Type               = CRED_TYPE_GENERIC;
    credential.TargetName         = target->data();
    credential.Persist            = CRED_PERSIST_LOCAL_MACHINE;
    credential.CredentialBlobSize = static_cast<DWORD>(blob->size());
    credential.CredentialBlob     = reinterpret_cast<BYTE*>(blob->data());
    const bool written            = CredWriteW(&credential, 0) != FALSE;
    SecureZeroMemory(blob->data(), blob->size());
    if (!written) return Error("Cannot save login in Windows Credential Manager");
    return {};
  }

  Result<void> Forget(std::string_view origin)
  {
    auto target = Auth::CredentialTarget(origin);
    if (!target) return Error(target.error());
    if (!CredDeleteW(target->c_str(), CRED_TYPE_GENERIC, 0) && GetLastError() != ERROR_NOT_FOUND)
      return Error("Cannot remove saved login from Windows Credential Manager");
    return {};
  }

}
