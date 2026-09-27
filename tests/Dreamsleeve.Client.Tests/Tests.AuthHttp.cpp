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

TEST_SUITE_END();
