export module Dreamsleeve.Host.GameDates;

import std;
import Dreamsleeve.Client.Domain;

// The one formatter of a mark's in-game date, for the HUD header and the web
// UI lists alike. No game headers, so the tests compile it. Names follow the
// Russian edition of Skyrim; the era and the year are Tamriel's in every style,
// there is nothing to translate them to.
export namespace Dreamsleeve::Host
{

  // "earth" names the weekday and the month as our calendar does; any other
  // value, "tamriel" included, uses the in-game names.
  std::string FormatGameDate(const Domain::GameDate& date, std::string_view style)
  {
    static constexpr std::array<std::string_view, 7>  TamrielDays{"Сандас", "Морндас", "Тирдас", "Миддас", "Турдас", "Фредас", "Лордас"};
    static constexpr std::array<std::string_view, 12> TamrielMonths{
        "Утренней звезды",
        "Восхода солнца",
        "Первого зерна",
        "Руки дождя",
        "Второго зерна",
        "Середины года",
        "Высокого солнца",
        "Последнего зерна",
        "Огня очага",
        "Начала морозов",
        "Заката солнца",
        "Вечерней звезды"
    };
    static constexpr std::array<std::string_view, 7>
      EarthDays{"Воскресенье", "Понедельник", "Вторник", "Среда", "Четверг", "Пятница", "Суббота"};
    static constexpr std::array<std::string_view, 12>
      EarthMonths{"января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"};

    const bool earth = style == "earth";
    // The codec checked the ranges; a damaged value still never indexes out of bounds.
    const auto weekday = date.dayOfWeek < 7 ? (earth ? EarthDays : TamrielDays)[date.dayOfWeek] : std::string_view{"?"};
    const auto month = date.month >= 1 && date.month <= 12 ? (earth ? EarthMonths : TamrielMonths)[date.month - 1] : std::string_view{"?"};
    return std::format("{}, {} {} {}Э {}, {:02}:{:02}", weekday, date.day, month, date.era, date.year, date.hour, date.minute);
  }

}
