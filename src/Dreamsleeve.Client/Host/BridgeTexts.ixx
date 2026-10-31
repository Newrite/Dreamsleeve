export module Dreamsleeve.Host.BridgeTexts;

import std;
import Dreamsleeve.Client.Exchange;
import Dreamsleeve.Client.Utils;
import Dreamsleeve.Host.Bridge;

// What the web UI shows in words, in the UI language: labels of game states and
// server refusals. Keys and codes stay in the contract (Bridge).
export namespace Dreamsleeve::Host::Bridge
{

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
        {"main",         "Главное меню"    },
        {"inventory",    "Инвентарь"       },
        {"magic",        "Магия"           },
        {"map",          "Карта"           },
        {"journal",      "Журнал"          },
        {"stats",        "Навыки"          },
        {"tween",        "Меню персонажа"  },
        {"sleepwait",    "Сон и ожидание"  },
        {"favorites",    "Избранное"       },
        {"levelup",      "Повышение уровня"},
        {"console",      "Консоль"         },
        {"messagebox",   "Сообщение"       },
        {"racesex",      "Внешность"       },
        {"container",    "Контейнер"       },
        {"gift",         "Подарок"         },
        {"tutorial",     "Обучение"        },
        {"creationclub", "Creation Club"   },
        {"modmanager",   "Модификации"     },
        {"credits",      "Титры"           },
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

  std::string ClipError(std::string_view value)
  {
    return std::string{Dreamsleeve::Utils::Text::ClipBytes(value, MaxErrorBytes)};
  }

  // Known server refusals in the UI language; unknown codes keep the server text.
  std::string RejectionText(Dreamsleeve::Client::RequestRejectionCode code, std::string_view message)
  {
    using Code = Dreamsleeve::Client::RequestRejectionCode;
    switch (code)
    {
      case Code::TextNotAllowed:
        return "Сообщение содержит запрещённые слова";
      case Code::Muted:
        if (message.starts_with("Muted in this guild")) return "Мут в этой гильдии: можно только читать";
        return "Вы в муте: писать сейчас нельзя";
      case Code::RateLimited:
        return "Слишком часто или повтор того же сообщения. Подождите немного";
      case Code::AnnouncementNotAllowed:
        return "Сервер не принимает объявления от этого источника";
      case Code::GroundMarkAreaFull:
        return "Здесь уже слишком много меток";
      case Code::GroundMarkNotFound:
        return "Метка не найдена или уже удалена";
      case Code::HiddenIdentityNotAllowed:
        return "Сервер не разрешает скрывать имя";
      case Code::NotChannelMember:
        if (message.starts_with("Player is not a member of this guild")) return "Вы не состоите в этой гильдии";
        break;
      case Code::InvalidRequest:
        if (message.starts_with("Message exceeds")) return "Сообщение слишком длинное";
        if (message.starts_with("Note exceeds")) return "Текст метки слишком длинный";
        if (message.starts_with("The mark is not where")) return "Метку нельзя оставить здесь";
        break;
      default:
        break;
    }
    return message.empty() ? std::string{"Сервер отклонил сообщение"} : std::string{message};
  }

  // Refusals of a display name change, in the UI language.
  std::string DisplayNameRejectionText(Dreamsleeve::Client::RequestRejectionCode code, std::string_view message)
  {
    using Code = Dreamsleeve::Client::RequestRejectionCode;
    switch (code)
    {
      case Code::TextNotAllowed:
        return "Имя содержит запрещённые слова";
      case Code::DisplayNameChangeNotAllowed:
        return "Сервер не разрешает менять имя";
      case Code::RateLimited: {
        // "The display name can be changed again in N min."
        constexpr std::string_view prefix = "The display name can be changed again in ";
        if (message.starts_with(prefix))
        {
          std::uint64_t minutes{};
          const auto*   begin  = message.data() + prefix.size();
          const auto    parsed = std::from_chars(begin, message.data() + message.size(), minutes);
          if (parsed.ec == std::errc{} && minutes > 0)
          {
            if (minutes < 120) return std::format("Имя можно сменить снова через {} мин", minutes);
            return std::format("Имя можно сменить снова через {} ч", (minutes + 59) / 60);
          }
        }
        return "Имя меняли недавно. Попробуйте позже";
      }
      case Code::InvalidRequest:
        if (message.starts_with("Display name exceeds")) return "Имя слишком длинное";
        return "Имя пустое или содержит недопустимые символы";
      case Code::Overloaded:
        return "Сервер занят. Попробуйте позже";
      default:
        break;
    }
    return message.empty() ? std::string{"Сервер отклонил имя"} : std::string{message};
  }

  // Refusals of a name color change, in the UI language.
  std::string NameColorRejectionText(Dreamsleeve::Client::RequestRejectionCode code, std::string_view message)
  {
    using Code = Dreamsleeve::Client::RequestRejectionCode;
    switch (code)
    {
      case Code::NameColorUnreadable:
        return "Цвет слишком тёмный: имя будет плохо видно в чате";
      case Code::RateLimited: {
        // "The name color can be changed again in N s."
        constexpr std::string_view prefix = "The name color can be changed again in ";
        std::uint64_t              seconds{};
        if (
          message.starts_with(prefix) &&
          std::from_chars(message.data() + prefix.size(), message.data() + message.size(), seconds).ec == std::errc{} && seconds > 0)
          return std::format("Цвет можно сменить снова через {} с", seconds);
        return "Цвет меняли только что. Попробуйте чуть позже";
      }
      case Code::InvalidRequest:
        return "Цвет задаётся как #RRGGBB";
      case Code::Overloaded:
        return "Сервер занят. Попробуйте позже";
      default:
        break;
    }
    return message.empty() ? std::string{"Сервер отклонил цвет"} : std::string{message};
  }

  // Refusals of a guild request, in the UI language.
  std::string GuildRejectionText(Dreamsleeve::Client::RequestRejectionCode code, std::string_view message)
  {
    using Code = Dreamsleeve::Client::RequestRejectionCode;
    // "A guild name has at least N characters." and "... at most N ...".
    const auto count = [&](std::string_view prefix) -> std::optional<std::uint64_t> {
      if (!message.starts_with(prefix)) return std::nullopt;
      std::uint64_t value{};
      const auto*   begin = message.data() + prefix.size();
      if (std::from_chars(begin, message.data() + message.size(), value).ec != std::errc{}) return std::nullopt;
      return value;
    };
    switch (code)
    {
      case Code::GuildNameTaken:
        return "Гильдия с таким названием уже есть";
      case Code::GuildFull:
        return "В гильдии нет свободных мест";
      case Code::GuildPlayerLimit:
        return "Достигнут предел гильдий на игрока";
      case Code::GuildServerLimit:
        return "На сервере уже предельное число гильдий";
      case Code::GuildInvitesFull:
        return "У гильдии слишком много ожидающих приглашений";
      case Code::GuildAlreadyMember:
        return "Игрок уже состоит в гильдии";
      case Code::GuildAlreadyInvited:
        return "Игрок уже приглашён";
      case Code::GuildMasterStays:
        return "Глава не может выйти: сначала передайте роль или распустите гильдию";
      case Code::GuildTitleNameTaken:
        return "В гильдии уже есть роль с таким названием";
      case Code::GuildTitlesFull:
        return "В гильдии уже предельное число ролей";
      case Code::GuildRequestsFull:
        return "У гильдии слишком много ожидающих заявок";
      case Code::GuildAlreadyApplied:
        return "Заявка в эту гильдию уже отправлена";
      case Code::GuildApplicationLimit:
        return "У вас уже предельное число заявок";
      case Code::NotPermitted:
        return "Ваша роль в гильдии этого не позволяет";
      case Code::TextNotAllowed:
        if (message.starts_with("The guild information")) return "Текст содержит запрещённые слова";
        return "Название содержит запрещённые слова";
      case Code::TargetNotFound:
        if (message.starts_with("The player is not online")) return "Игрок не в сети";
        if (message.starts_with("No such guild")) return "Гильдия не найдена";
        if (message.starts_with("No such custom role")) return "Такой роли в гильдии уже нет";
        return "Игрока, приглашения или заявки уже нет";
      case Code::InvalidRequest:
        if (const auto minimum = count("A guild name has at least ")) return std::format("Название не короче {} символов", *minimum);
        if (const auto maximum = count("A guild name has at most ")) return std::format("Название не длиннее {} символов", *maximum);
        if (message.starts_with("A guild name has letters")) return "В названии только буквы, цифры и пробелы";
        if (const auto minimum = count("A custom role name has at least "))
          return std::format("Название роли не короче {} символов", *minimum);
        if (const auto maximum = count("A custom role name has at most "))
          return std::format("Название роли не длиннее {} символов", *maximum);
        if (message.starts_with("A custom role name has letters")) return "В названии роли только буквы, цифры и пробелы";
        if (message.starts_with("A guild role")) return "Роль: участник или офицер";
        if (const auto maximum = count("The guild information has at most "))
          return std::format("Информация о гильдии не длиннее {} символов", *maximum);
        if (message.starts_with("The guild information contains")) return "В тексте недопустимые символы";
        break;
      case Code::Overloaded:
        return "Сервер занят. Попробуйте позже";
      default:
        break;
    }
    return message.empty() ? std::string{"Сервер отклонил запрос"} : std::string{message};
  }

  // Refusals of a moderator request, in the UI language.
  std::string ModerationRejectionText(Dreamsleeve::Client::RequestRejectionCode code, std::string_view message, std::string_view field)
  {
    using Code = Dreamsleeve::Client::RequestRejectionCode;
    switch (code)
    {
      case Code::NotPermitted:
        if (message.starts_with("The guild role")) return "Ваша роль в гильдии не позволяет удалить это сообщение";
        if (message.starts_with("Only a moderator")) return "Это может только модератор";
        return "Модератор не может наказать себя или другого модератора";
      case Code::TargetNotFound:
        if (message.starts_with("No such sanction")) return "Такого наказания уже нет";
        if (message.starts_with("No such message")) return "Сообщение не найдено или уже удалено";
        return "Игрок не найден";
      case Code::ChannelNotFound:
        return "Канал не найден";
      case Code::Overloaded:
        return "Сервер занят. Попробуйте позже";
      case Code::InvalidRequest:
        if (field == "reason") return "Нужна короткая причина в одну строку";
        if (field == "minutes") return "Срок — от минуты до десяти лет";
        if (field == "kinds") return "Выберите заметки, метки смерти или оба вида";
        return "Некорректный запрос";
      default:
        break;
    }
    return message.empty() ? std::string{"Сервер отклонил запрос"} : std::string{message};
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
