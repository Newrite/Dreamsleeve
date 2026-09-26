# Проверка MSVC: protobuf в интерфейсе модуля

Повторно проверено 26.09.2026 после обновления Visual Studio.

| Компонент | Фактически установленная версия |
|---|---|
| Visual Studio Community 2026 | 18.10.2, installationVersion 18.10.12217.157 |
| cl.exe, c1xx.dll, c2.dll | 19.51.36260.0 |
| MSVC product version | 14.51.36260.0 |
| Каталог toolset | VC/Tools/MSVC/14.51.36231 |
| protoc / protobuf-cpp | 33.2 |
| Режим пробы | x64, /std:c++23preview /EHsc /MD /O2 |

Имя каталога MSVC после обновления не изменилось. Версию нужно проверять через
`cl /Bv` или FileVersion, а не по пути из кэша xmake.

## Результат

| Проба | Результат |
|---|---|
| Обычный .cpp с chat.pb.h | Компилируется |
| .ixx с network.pb.h в global module fragment, без экспорта protobuf-типов | C1001 |
| .ixx с network.pb.h и экспортируемым alias DisconnectReason | C1001 |
| .ixx с chat.pb.h и экспортируемым alias ClientPacket | C1001 |
| .cpp с network.pb.h и export module, /interface | C1001 |
| .cpp с chat.pb.h и export module, /interface | C1001 |

Во всех пяти неуспешных пробах: `msc1.cpp`, строка 1672, код процесса
3221225477 (0xC0000005). Сбой происходит при создании интерфейса модуля,
до компиляции потребителя. Частично записанный .ifc не означает успеха.

Минимальный воспроизводящий исходник `network_include.ixx`:

```cpp
module;
#include "network.pb.h"
export module probe.network_include;
export int value() { return 1; }
```

Для повторной проверки открыть x64 Developer Command Prompt обновлённой VS,
задать include-пути к protobuf, Abseil и src/Dreamsleeve.Protocol.Native:

```text
cl /Bv
cl /nologo /c /std:c++23preview /EHsc /MD /O2 /utf-8 /interface
   /I<protobuf-include> /I<abseil-include> /I<Protocol.Native>
   /ifcOutput network_include.ifc /Fonetwork_include.obj network_include.ixx
```

Команда компиляции приведена с переносами для чтения; запускать одной строкой.
`cl /Bv` без исходника дополнительно печатает D8003 — это не проверяемый ICE.
Заголовки и зависимости в пробах совпадали с текущим проектом; импорт std,
старые .ifc и compiler cache в минимальных пробах не использовались.

## Что оставлено в проекте

Представление DisconnectReason и RequestRejectionCode без protobuf-заголовков
и его static_assert-проверки пока необходимы. Теперь Dreamsleeve.Protocol.Native.ixx
и ProtocolContract.cpp создаёт Scripts/generate_protocol.py из вывода protoc:
имена и номера задаются только в .proto, ручного дублирования enum нет. Неожиданный
формат generated enum останавливает генерацию с ошибкой.
Generated protobuf headers остаются в обычных .cpp и реализации ChatCodec.cpp, вне интерфейса
модуля. Расширение файла само по себе не обходит ошибку: тот же минимальный
исходник, сохранённый как .cpp и собранный с /interface, также вызывает C1001.

ChatCodec.cpp уже является частью C++-модуля: `module Dreamsleeve.Client.Codec;`
объявляет единицу реализации. Отдельный ChatCodec.ixx содержит
`export module Dreamsleeve.Client.Codec;` и публичные объявления без protobuf.
Такое разделение работает. Объединение в один интерфейсный файл с protobuf
не становится рабочим от смены расширения на .cpp.

При воспроизведении Windows может показать окно ошибки приложения cl.exe:
код 0xC0000005 соответствует нарушению доступа к памяти. Это падение процесса
компилятора во время минимальной пробы, а не ошибка выполнения Client.Dev.

После проверки выполнена очистка `xmake clean -a` и пересборка всех native-целей,
включая std.ifc, с отключённым compiler cache. Результаты тестов указаны в
[tests/README](../tests/README.md). После следующего обновления сначала повторить
минимальную пробу, затем проверить импорт типов потребителем и полную сборку;
только после успеха убирать зеркало и ProtocolContract.cpp.
