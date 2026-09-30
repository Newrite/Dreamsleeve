#pragma once

// The bridge as the web UI sees it, for assertions: JSON views of events and
// typed parsed commands. The including file includes doctest and glaze and
// imports Dreamsleeve.Host.Bridge first.

// Minimal JSON view; the UI parser is the real contract owner.
inline glz::generic Parse(const std::string& json)
{
  glz::generic value;
  REQUIRE_FALSE(glz::read_json(value, json));
  return value;
}

// The JSON text the page receives.
inline std::string Json(const Dreamsleeve::Host::Bridge::HostEvent& event)
{
  auto json = Dreamsleeve::Host::Bridge::Encode(event);
  REQUIRE(json);
  return std::move(*json);
}

inline glz::generic Parse(const Dreamsleeve::Host::Bridge::HostEvent& event)
{
  return Parse(Json(event));
}

inline std::string Type(const Dreamsleeve::Host::Bridge::HostEvent& event)
{
  return std::string{Dreamsleeve::Host::Bridge::TypeOf(event)};
}

// The command of kind Command that json parses to; anything else fails the test.
template <class Command>
Command CommandOf(std::string_view json)
{
  auto command = Dreamsleeve::Host::Bridge::ParseCommand(json);
  REQUIRE_MESSAGE(command, command.error());
  const auto* value = std::get_if<Command>(&*command);
  REQUIRE(value);
  return *value;
}
