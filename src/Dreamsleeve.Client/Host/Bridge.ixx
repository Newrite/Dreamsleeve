module;

#include <glaze/glaze.hpp>

export module Dreamsleeve.Host.Bridge;

import std;
import Dreamsleeve.Client.Exchange;
export import Dreamsleeve.Host.UiSettings;

// In-process JSON contract with the web UI (src/Dreamsleeve.Client.UI/src/bridge/types.ts).
// Host -> UI payloads are handed to InteropCall as a string argument and parsed
// with JSON.parse; they are never evaluated as JavaScript. uint64 IDs are strings.
export namespace Dreamsleeve::Host::Bridge
{

  using Dreamsleeve::Client::AuthOperation;
  using Dreamsleeve::Client::ClientStatus;
  using Dreamsleeve::Client::CommandFailureCode;
  using Dreamsleeve::Client::SessionPhase;
  namespace ClientAuth = Dreamsleeve::Client::Auth;

  struct UiResource
  {
    double current{};
    double maximum{};
  };

  struct UiActorValue
  {
    std::string                      key;
    std::string                      name;
    std::variant<double, UiResource> value{0.0};
  };

  struct UiPlayer
  {
    std::string                              id;
    std::string                              displayName;
    std::string                              username;
    std::optional<std::string>               character;
    std::optional<std::uint32_t>             level;
    std::optional<std::string>               location;
    std::optional<std::string>               zone;
    std::optional<std::string>               race;
    std::optional<std::string>               nearbyMarker;
    std::optional<std::string>               markerKind;
    std::optional<bool>                      interior;
    std::optional<std::string>               activity;
    std::optional<std::string>               activityTarget;
    std::optional<std::string>               lockDifficulty;
    std::optional<std::string>               menu;
    std::optional<std::int64_t>              gameStartedAt;
    std::optional<std::vector<UiActorValue>> actorValues;
  };

  struct UiChannel
  {
    std::string id;
    std::string kind;
    std::string name;
    bool        writable{};
  };

  struct UiMessage
  {
    std::string  id;
    std::string  channelId;
    std::string  text;
    std::int64_t time{};
    std::string  source{"player"};
    UiPlayer     author;
  };

  struct SnapshotEvent
  {
    std::string               type{"snapshot"};
    std::vector<UiChannel>    channels;
    std::vector<UiMessage>    messages;
    std::vector<UiPlayer>     players;
    std::string               selfId;
    std::string               serverName;
    std::optional<UiSettings> settings;
  };

  struct MessagesEvent
  {
    std::string            type{"messages"};
    std::vector<UiMessage> messages;
  };

  struct PlayersEvent
  {
    std::string           type{"players"};
    std::vector<UiPlayer> players;
  };

  struct ConnectionEvent
  {
    std::string type{"connection"};
    bool        connected{};
    std::string phase{"disconnected"};
  };

  struct SendResultEvent
  {
    std::string                type{"sendResult"};
    std::string                requestId;
    std::optional<std::string> messageId;
    std::optional<std::string> error;
  };

  struct SettingsResultEvent
  {
    std::string                type{"settingsResult"};
    std::int64_t               revision{};
    std::optional<std::string> error;
  };

  // Typed authentication state; no password or token ever crosses this boundary.
  struct AuthEvent
  {
    std::string type{"auth"};
    bool        authenticating{};
    std::string operation{"none"};
    std::string failure{"none"};
    std::string error;
    bool        savedLogin{};
    std::string savedUsername;
    std::string phase{"disconnected"};
  };

  struct SimpleEvent
  {
    std::string type;
  };

  // Sent when a page is (re)created so window position and options apply
  // before any snapshot; a snapshot repeats them.
  struct SettingsEvent
  {
    std::string type{"settings"};
    UiSettings  settings;
  };

  // UI -> host. Unknown keys are ignored so a newer UI does not break an older host.
  struct UiCommand
  {
    std::string               type;
    std::string               requestId;
    std::string               channelId;
    std::string               text;
    std::optional<UiSettings> settings;
    std::int64_t              revision{};
    std::string               username;
    std::string               password;
    std::string               displayName;
    bool                      remember{};
  };

  constexpr std::size_t MaxChatText     = 16000;
  constexpr std::size_t MaxSnapshotRows = 500;

  using Encoded = std::expected<std::string, std::string>;

  namespace Detail
  {

    // Instantiated only inside this module: glaze internals live in its global
    // module fragment and are not reachable from importers' template instantiations.
    template <class Event>
    Encoded Write(const Event& event)
    {
      auto json = glz::write_json(event);
      if (!json) return std::unexpected{"Cannot encode UI event"};
      return std::move(*json);
    }

  }

  Encoded Encode(const SnapshotEvent& event)
  {
    return Detail::Write(event);
  }

  Encoded Encode(const MessagesEvent& event)
  {
    return Detail::Write(event);
  }

  Encoded Encode(const PlayersEvent& event)
  {
    return Detail::Write(event);
  }

  Encoded Encode(const ConnectionEvent& event)
  {
    return Detail::Write(event);
  }

  Encoded Encode(const SendResultEvent& event)
  {
    return Detail::Write(event);
  }

  Encoded Encode(const SettingsResultEvent& event)
  {
    return Detail::Write(event);
  }

  Encoded Encode(const AuthEvent& event)
  {
    return Detail::Write(event);
  }

  Encoded Encode(const SimpleEvent& event)
  {
    return Detail::Write(event);
  }

  Encoded Encode(const SettingsEvent& event)
  {
    return Detail::Write(event);
  }

  std::expected<UiCommand, std::string> ParseCommand(std::string_view json)
  {
    if (json.size() > 1 << 20) return std::unexpected{"UI command exceeds limit"};
    UiCommand command;
    if (auto error = glz::read<glz::opts{.error_on_unknown_keys = false}>(command, json))
      return std::unexpected{"Invalid UI command: " + glz::format_error(error, json)};

    const auto& type = command.type;
    if (type == "sendChat")
    {
      if (command.requestId.empty() || command.channelId.empty()) return std::unexpected{"sendChat requires requestId and channelId"};
      if (command.text.empty() || command.text.size() > MaxChatText) return std::unexpected{"sendChat text is empty or too long"};
      return command;
    }
    if (type == "saveSettings")
    {
      if (!command.settings) return std::unexpected{"saveSettings requires settings"};
      command.settings = Normalize(*command.settings);
      return command;
    }
    if (type == "signIn")
    {
      if (command.username.empty() || command.password.empty()) return std::unexpected{"signIn requires username and password"};
      return command;
    }
    if (type == "close" || type == "signInSaved" || type == "signOut" || type == "forgetLogin" || type == "disconnect") return command;
    return std::unexpected{"Unknown UI command: " + type};
  }

  std::string Id(std::uint64_t value)
  {
    return std::to_string(value);
  }

  std::optional<std::uint64_t> ParseId(std::string_view text)
  {
    std::uint64_t value{};
    const auto    parsed = std::from_chars(text.data(), text.data() + text.size(), value);
    if (parsed.ec != std::errc{} || parsed.ptr != text.data() + text.size() || value == 0) return std::nullopt;
    return value;
  }

  // Labels are display text for the Russian UI; keys stay in the protocol.
  std::string_view ActivityLabel(Domain::ActivityKind kind)
  {
    using Domain::ActivityKind;
    switch (kind)
    {
      case ActivityKind::Exploring:
        return "Исследование";
      case ActivityKind::Combat:
        return "Бой";
      case ActivityKind::Talking:
        return "Разговор";
      case ActivityKind::Bartering:
        return "Торговля";
      case ActivityKind::Training:
        return "Обучение";
      case ActivityKind::Reading:
        return "Чтение";
      case ActivityKind::Lockpicking:
        return "Взлом замка";
      case ActivityKind::Crafting:
        return "Ремесло";
      case ActivityKind::UsingObject:
        return "Использует объект";
      case ActivityKind::Riding:
        return "Верхом";
      case ActivityKind::Sneaking:
        return "Скрытность";
      case ActivityKind::Swimming:
        return "Плавание";
      case ActivityKind::Flying:
        return "Полёт";
      case ActivityKind::Dead:
        return "Погиб";
      case ActivityKind::Ragdoll:
        return "Сбит с ног";
      case ActivityKind::Menu:
        return "В меню";
      case ActivityKind::NewGame:
        return "Новая игра";
      case ActivityKind::Loading:
        return "Загрузка";
      case ActivityKind::Unknown:
        break;
    }
    return "";
  }

  std::string_view LockLabel(Domain::LockDifficulty level)
  {
    using Domain::LockDifficulty;
    switch (level)
    {
      case LockDifficulty::Unlocked:
        return "Открыт";
      case LockDifficulty::VeryEasy:
        return "Очень лёгкий";
      case LockDifficulty::Easy:
        return "Лёгкий";
      case LockDifficulty::Average:
        return "Средний";
      case LockDifficulty::Hard:
        return "Сложный";
      case LockDifficulty::VeryHard:
        return "Очень сложный";
      case LockDifficulty::RequiresKey:
        return "Нужен ключ";
      case LockDifficulty::Unknown:
        break;
    }
    return "";
  }

  std::string_view MenuLabel(std::string_view key)
  {
    static const std::pair<std::string_view, std::string_view> labels[] = {
        {"main",          "Главное меню"    },
        {"inventory",     "Инвентарь"       },
        {"magic",         "Магия"           },
        {"map",           "Карта"           },
        {"journal",       "Журнал"          },
        {"stats",         "Навыки"          },
        {"tween",         "Меню персонажа"  },
        {"sleepwait",     "Сон и ожидание"  },
        {"favorites",     "Избранное"       },
        {"levelup",       "Повышение уровня"},
        {"console",       "Консоль"         },
        {"messagebox",    "Сообщение"       },
        {"racesex",       "Внешность"       },
        {"container",     "Контейнер"       },
        {"tutorial",      "Обучение"        },
        {"creationclub",  "Creation Club"   },
        {"modmanager",    "Модификации"     },
        {"credits",       "Титры"           },
        {"loading",       "Загрузка"        },
        {"titlesequence", "Заставка"        },
        {"dialogue",      "Диалог"          },
    };
    for (const auto& [name, label] : labels)
      if (name == key) return label;
    return key;
  }

  std::string_view MarkerLabel(std::string_view kind)
  {
    static const std::pair<std::string_view, std::string_view> labels[] = {
        {"city",               "Город"              },
        {"town",               "Городок"            },
        {"settlement",         "Поселение"          },
        {"cave",               "Пещера"             },
        {"camp",               "Лагерь"             },
        {"fort",               "Форт"               },
        {"nordicruin",         "Нордские руины"     },
        {"dwemer",             "Двемерские руины"   },
        {"shipwreck",          "Кораблекрушение"    },
        {"grove",              "Роща"               },
        {"landmark",           "Ориентир"           },
        {"dragonlair",         "Логово дракона"     },
        {"farm",               "Ферма"              },
        {"woodmill",           "Лесопилка"          },
        {"mine",               "Шахта"              },
        {"imperialcamp",       "Имперский лагерь"   },
        {"stormcloakcamp",     "Лагерь Братьев Бури"},
        {"doomstone",          "Камень судьбы"      },
        {"wheatmill",          "Мельница"           },
        {"smelter",            "Плавильня"          },
        {"stable",             "Конюшня"            },
        {"imperialtower",      "Имперская башня"    },
        {"clearing",           "Поляна"             },
        {"pass",               "Перевал"            },
        {"altar",              "Алтарь"             },
        {"rock",               "Скала"              },
        {"lighthouse",         "Маяк"               },
        {"orcstronghold",      "Орочья крепость"    },
        {"giantcamp",          "Лагерь великанов"   },
        {"shack",              "Хижина"             },
        {"nordictower",        "Нордская башня"     },
        {"nordicdwelling",     "Нордское жилище"    },
        {"docks",              "Причал"             },
        {"shrine",             "Святилище"          },
        {"castle",             "Замок"              },
        {"capital",            "Столица"            },
        {"templeofmiraak",     "Храм Мираака"       },
        {"redoransettlement",  "Поселение Редоран"  },
        {"allmakerstone",      "Камень Всесоздателя"},
        {"telvannisettlement", "Поселение Телванни" },
    };
    for (const auto& [name, label] : labels)
      if (name == kind) return label;
    return kind;
  }

  std::optional<std::string> Text(const std::string& value)
  {
    if (value.empty()) return std::nullopt;
    return value;
  }

  std::optional<std::string> Label(std::string_view value)
  {
    if (value.empty()) return std::nullopt;
    return std::string{value};
  }

  UiPlayer ToUiAuthor(const Domain::PlayerData& data)
  {
    UiPlayer player;
    player.id          = Id(data.playerId);
    player.displayName = data.displayName;
    player.username    = data.username;
    return player;
  }

  UiPlayer ToUiPlayer(const Domain::Player& source)
  {
    auto        player  = ToUiAuthor(source.data);
    const auto& details = source.details;
    player.character    = source.characterName;
    player.level        = details.level;
    if (details.race) player.race = Text(details.race->name);
    if (details.place)
    {
      player.zone         = Text(details.place->worldspaceName);
      player.location     = Text(details.place->locationName);
      player.nearbyMarker = Text(details.place->nearbyMarkerName);
      player.markerKind   = Label(MarkerLabel(details.place->markerKind));
      player.interior     = details.place->isInterior;
    }
    else if (source.location)
      player.location = Text(source.location->location.locationName);

    player.activity       = Label(ActivityLabel(details.activity.kind));
    player.activityTarget = details.activity.targetName;
    player.lockDifficulty = Label(LockLabel(details.activity.lockDifficulty));
    if (details.activity.menuKey) player.menu = Label(MenuLabel(*details.activity.menuKey));
    player.gameStartedAt = details.gameStartedAtUnixMs;

    if (!source.actorValues.empty())
    {
      std::vector<UiActorValue> values;
      values.reserve(source.actorValues.size());
      for (const auto& [key, info] : source.actorValues)
      {
        UiActorValue value{key, info.displayName};
        if (const auto* resource = std::get_if<Domain::ResourceActorValue>(&info.state))
          value.value = UiResource{resource->current, resource->maximum};
        else
          value.value = static_cast<double>(std::get<Domain::ScalarActorValue>(info.state).value);
        values.push_back(std::move(value));
      }
      std::ranges::sort(values, {}, &UiActorValue::key);
      player.actorValues = std::move(values);
    }
    return player;
  }

  UiMessage ToUiMessage(const Domain::ChatMessage& message)
  {
    UiMessage result;
    result.id        = Id(message.messageId);
    result.channelId = Id(message.channelId);
    result.text      = message.messageText;
    result.time      = Domain::ToUnixMilliseconds(message.sentAt);
    result.author    = ToUiAuthor(message.author);
    return result;
  }

  std::string_view PhaseName(const ClientStatus& status)
  {
    if (status.stopped) return "disconnected";
    switch (status.phase)
    {
      case SessionPhase::Disconnected:
        return status.authenticating ? "authenticating" : "disconnected";
      case SessionPhase::Connecting:
        return "connecting";
      case SessionPhase::Opening:
        return "opening";
      case SessionPhase::Ready:
        return "connected";
      case SessionPhase::Disconnecting:
        return "disconnecting";
      case SessionPhase::Faulted:
        return "faulted";
    }
    return "disconnected";
  }

  std::string_view OperationName(AuthOperation operation)
  {
    switch (operation)
    {
      case AuthOperation::PasswordLogin:
        return "passwordLogin";
      case AuthOperation::Resume:
        return "resume";
      case AuthOperation::SignOut:
        return "signOut";
      case AuthOperation::ForgetSavedLogin:
        return "forgetSavedLogin";
      case AuthOperation::ResetPassword:
        return "resetPassword";
      case AuthOperation::None:
        break;
    }
    return "none";
  }

  std::string_view FailureName(ClientAuth::FailureCode code)
  {
    using ClientAuth::FailureCode;
    switch (code)
    {
      case FailureCode::InvalidCredentials:
        return "invalidCredentials";
      case FailureCode::UsernameTaken:
        return "usernameTaken";
      case FailureCode::InvalidRequest:
        return "invalidRequest";
      case FailureCode::RegistrationDisabled:
        return "registrationDisabled";
      case FailureCode::Busy:
        return "busy";
      case FailureCode::Unavailable:
        return "unavailable";
      case FailureCode::InvalidResponse:
        return "invalidResponse";
      case FailureCode::CredentialStorage:
        return "credentialStorage";
      case FailureCode::Canceled:
        return "canceled";
      case FailureCode::None:
        break;
    }
    return "none";
  }

  ConnectionEvent ConnectionState(const ClientStatus& status)
  {
    ConnectionEvent event;
    event.phase     = PhaseName(status);
    event.connected = event.phase == "connected";
    return event;
  }

  AuthEvent AuthState(const ClientStatus& status)
  {
    AuthEvent event;
    event.authenticating = status.authenticating;
    event.operation      = OperationName(status.authOperation);
    event.failure        = FailureName(status.authFailure);
    event.error          = status.error;
    event.savedLogin     = status.savedLogin;
    event.savedUsername  = status.savedUsername;
    event.phase          = PhaseName(status);
    return event;
  }

  std::string_view FailureText(CommandFailureCode code)
  {
    switch (code)
    {
      case CommandFailureCode::StaleGeneration:
        return "Сессия сменилась, сообщение не отправлено";
      case CommandFailureCode::SessionNotReady:
        return "Нет соединения с сервером";
      case CommandFailureCode::Busy:
        return "Слишком много ожидающих сообщений";
      case CommandFailureCode::InvalidRequest:
        return "Некорректный запрос";
      case CommandFailureCode::EncodingFailed:
        return "Не удалось закодировать сообщение";
    }
    return "Сообщение не отправлено";
  }

}
