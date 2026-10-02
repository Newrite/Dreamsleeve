#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.Auth;

namespace Auth = Dreamsleeve::Client::Auth;

TEST_SUITE_BEGIN("Client.Auth");

TEST_CASE("Password endpoints require HTTPS except explicitly loopback development origins")
{
  CHECK(Auth::ValidateUrl("http://127.0.0.1:8779"));
  CHECK(Auth::ValidateUrl("http://localhost:8779/"));
  CHECK(Auth::ValidateUrl("http://[::1]:8779"));
  CHECK(Auth::ValidateUrl("https://auth.example.test:443"));
  CHECK_FALSE(Auth::ValidateUrl("http://auth.example.test"));
  CHECK_FALSE(Auth::ValidateUrl("http://192.168.1.1:8779"));
  CHECK_FALSE(Auth::ValidateUrl("http://127.0.0.1.example.test"));
  CHECK_FALSE(Auth::ValidateUrl("http://2130706433"));
}

TEST_CASE("Auth origins reject embedded credentials and URL components that change the endpoint")
{
  CHECK_FALSE(Auth::ValidateUrl("https://user:secret@example.test"));
  CHECK_FALSE(Auth::ValidateUrl("https://example.test?redirect=http://host"));
  CHECK_FALSE(Auth::ValidateUrl("https://example.test/#fragment"));
  CHECK_FALSE(Auth::ValidateUrl("https://example.test/auth/login"));
  CHECK_FALSE(Auth::ValidateUrl("ftp://127.0.0.1"));
  CHECK_FALSE(Auth::ValidateUrl(""));
}

TEST_CASE("Sign-in methods read the registration mode; a mode this client does not know stays unknown")
{
  using Auth::RegistrationMode;
  CHECK(Auth::DecodeMethods(R"({"registration":"open","steam":false})") == Auth::Methods{RegistrationMode::Open, false});
  CHECK(Auth::DecodeMethods(R"({"registration":"steam","steam":true})") == Auth::Methods{RegistrationMode::Steam, true});
  CHECK(Auth::DecodeMethods(R"({"registration":"manual","steam":true,"later":1})") == Auth::Methods{RegistrationMode::Manual, true});
  CHECK(Auth::DecodeMethods(R"({"registration":"invite","steam":true})") == Auth::Methods{RegistrationMode::Unknown, true});
  CHECK_FALSE(Auth::DecodeMethods("not json"));
  CHECK_FALSE(Auth::DecodeMethods(R"({"registration":1})"));
}

TEST_CASE("A Steam sign-in opens only Steam's OpenID login page")
{
  CHECK(
    Auth::SteamPage(
      "https://steamcommunity.com/openid/login?openid.ns=http%3A%2F%2Fspecs.openid.net%2Fauth%2F2.0&openid.mode=checkid_setup"));
  CHECK_FALSE(Auth::SteamPage("https://steamcommunity.com/openid/login?"));
  CHECK_FALSE(Auth::SteamPage("https://steamcommunity.com/openid/login"));
  CHECK_FALSE(Auth::SteamPage("http://steamcommunity.com/openid/login?openid.mode=checkid_setup"));
  CHECK_FALSE(Auth::SteamPage("https://steamcommunity.com.example.test/openid/login?openid.mode=checkid_setup"));
  CHECK_FALSE(Auth::SteamPage("https://steamcommunity.com/openid/login?a=b c"));
  CHECK_FALSE(Auth::SteamPage("https://steamcommunity.com/openid/login?a=b#fragment"));
  CHECK_FALSE(Auth::SteamPage("https://steamcommunity.com/openid/login?a=\"b\" c"));
  CHECK_FALSE(Auth::SteamPage("file:///C:/Windows/System32/calc.exe"));
  CHECK_FALSE(Auth::SteamPage("https://steamcommunity.com/openid/login?" + std::string(4096, 'a')));
  CHECK_FALSE(Auth::OpenSteamPage("calc.exe"));
}

TEST_SUITE_END();

TEST_CASE("Remote HTTP authentication requires explicit opt-in")
{
  for (const auto url : {"http://auth.example.test:8779", "http://192.168.1.10:8779"})
  {
    CHECK_FALSE(Auth::ValidateUrl(url));
    CHECK(Auth::ValidateUrl(url, true));
    CHECK(Auth::CredentialTarget(url, true));
  }
  CHECK_FALSE(Auth::ValidateUrl("http://user:secret@example.test", true));
  CHECK_FALSE(Auth::ValidateUrl("http://example.test/auth/login", true));
  CHECK_FALSE(Auth::ValidateUrl("ftp://example.test", true));
  CHECK(Auth::CredentialTarget("http://example.test", true).value() != Auth::CredentialTarget("https://example.test", true).value());
}
