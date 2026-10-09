#include <string>
#include <doctest/doctest.h>
import std;
import Dreamsleeve.Client.CredentialStore;
import Dreamsleeve.Client.Application;

using namespace Dreamsleeve::Client;

namespace
{
  struct SavedLoginFixture
  {
    std::string origin = "https://credential-test-" + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) + ".invalid";

    ~SavedLoginFixture() { (void)CredentialStore::Forget(origin); }
  };
}

TEST_CASE("Credential Manager stores only the selected server credential and supports replacement")
{
  SavedLoginFixture fixture;
  auto absent = CredentialStore::Load(fixture.origin);
  REQUIRE(absent);
  CHECK_FALSE(absent->has_value());

  const std::string secret(43, 'a');
  REQUIRE(CredentialStore::Save(fixture.origin, {"player", secret}));
  auto saved = CredentialStore::Load(fixture.origin + ":443/");
  REQUIRE(saved);
  REQUIRE(saved->has_value());
  CHECK((**saved).username == "player");
  CHECK((**saved).token == secret);

  REQUIRE(CredentialStore::Save(fixture.origin, {"another", std::string(43, 'b')}));
  saved = CredentialStore::Load(fixture.origin);
  REQUIRE(saved);
  REQUIRE(saved->has_value());
  CHECK((**saved).username == "another");

  REQUIRE(CredentialStore::Forget(fixture.origin));
  REQUIRE(CredentialStore::Forget(fixture.origin));
  CHECK_FALSE(CredentialStore::Load(fixture.origin)->has_value());
}

TEST_CASE("Saved connection without a credential tells the UI to request login")
{
  SavedLoginFixture fixture;
  ClientSettings settings;
  settings.authUrl = fixture.origin;

  auto created = ClientApplication::TryCreate(settings);
  REQUIRE(created);
  auto& app = **created;
  REQUIRE(app.ConnectSaved());

  const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds{3};
  while (app.Status().authenticating && std::chrono::steady_clock::now() < deadline)
    std::this_thread::sleep_for(std::chrono::milliseconds{1});

  CHECK_FALSE(app.Status().authenticating);
  CHECK(app.Status().authOperation == AuthOperation::Resume);
  CHECK(app.Status().authFailure == Auth::FailureCode::InvalidCredentials);
  CHECK(app.Status().phase == SessionPhase::Disconnected);

  app.Stop();
}
