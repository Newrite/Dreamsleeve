# Нативные asset delta: production, 08.10.2026

Протокол26; asset2 и самостоятельные позы без изменений. Новый renderer не
добавлен. Raw asset остаётся непрозрачным контейнером NIF/каналов; Zstd prefix
использует предыдущий raw asset как базу. Delta выбирается только если меньше
полного compressed архива. Восстановление возвращает **тот же полный архив**,
проверенный SHA-256, затем используется прежний ReadAsset/NiStream Load.

## Реальные данные

Исходная сессия `1791459268558659-18408`, восемь локальных поколений из trace.
Исходные записи не изменялись. Генерация и apply — production C++ Delta;
независимое восстановление — production .NET PhantomDeltaCodec/ZstdSharp.Port0.8.6.
Обе стороны получили исходный compressed target побайтово для всех семи пар.
Дополнительно все10 архивов сессии совпали после канонической перекомпрессии .NET.

| Поколения | Полный архив, B | Delta, B |
|---|---:|---:|
| 1 → 2 | 13,255,556 | 103,143 |
| 2 → 3 | 13,184,107 | 114,998 |
| 3 → 4 | 13,226,041 | 143,128 |
| 4 → 5 | 13,312,925 | 195,810 |
| 5 → 6 | 13,308,817 | 109,495 |
| 6 → 7 | 13,311,877 | 73,951 |
| 7 → 8 | 13,313,497 | 106,620 |

Сумма обновлений: 92,912,820 → 847,145 B, сокращение 99.09%.
Первая полная модель, HTTP/TCP/ENet overhead и поток поз в эту сумму не входят.
При N получателях с точной базой модельный body составляет (1+N)×deltaBytes
вместо (1+N)×fullBytes. Для получателей без базы применяется fullBytes.
Это расчёт payload, не измерение WAN throughput или 512-player load.

## Время и границы измерения

Пара6→7: raw27 739 625B, full13 311 877B, patch73 951B. Отдельный C++ прогон:
encode95,45ms, apply157,87ms. Полная серия дала encode111–162ms/apply181–243ms
на фоне сборки; это единичные выборки, не steady-state p95. Python только запускал
C++ тест и копировал fixtures: кодирование/таймеры не реализованы в Python.
Первый холодный .NET apply этой пары:531,78ms вместе с JIT, не сравним напрямую
с прогретым C++. CPU-время восстановления платится даже при маленькой дельте.

Сквозной synthetic smoke: два production C++ Streaming/WinHTTP клиента,
настоящий ENet и HTTP сервер с controlled-auth fixture. Генерация4: delta32 465B;
upload → server full reconstruction → delta download → клиентский asset load
148,115ms на loopback,3334 assertions. В этом тесте нет Skyrim и NiStream Load.
В тестах HTTP/storage проверены также реальные13MiB архивы, full download новому
получателю, delta по точной базе, полный ответ при другой базе, испорченное тело,
неверный target hash, неверный raw size, отсутствующая база.

## Владение и ограничения

Client.Core ModelRun вычисляет/apply delta вне игрового потока; очередь учитывает
дополнительный bounded scratch базы. База скачивания читается из кеша лишь при
apply, hash перепроверяется; пропавшая база даёт full retry. Streaming владеет
выбором тела и retry; Exchange — моделью/поколением. Нехватка бюджета delta также
переводит запрос в full. Готовность новой сцены не дублируется.

Server actor допускает base только от текущего Ready публикации; Storage pin
защищает базу от eviction. Один reconstruction semaphore на store ограничивает
одновременные тяжёлые задачи. Квота диска учитывает full+delta; RamBytes — кеш,
не scratch-бюджет декомпрессора. Scratch ограничен raw/compressed limits и одним
активным восстановлением. Actor не разбирает геометрию и не сжимает данные.

Сервер хранит полный результат и одну входящую delta. Клиент с более старой
базой получает полный архив, цепочка delta не передаётся. При перезапуске сервера
full cache сохраняется, delta metadata не восстанавливается. После изменения
контекста без прежнего Ready upload тоже полный. При несовпадении канонического
сжатия безопасный fallback полный; обновления Zstd требуют проверки fixture.

## Проверки и оставшееся

Normal native:399passed/4skipped; managed:640passed как с реальными fixtures,
так и с автоматической synthetic delta. Smoke покрывает также supersession,
отменённые HTTP callbacks, повторную подписку, потерю fragmented pose и rollover.
Артефакты проверки: `build/native-delta/`; исходные игровые assets не включены
в Git/dist. Новый ABI/адрес движка не добавлен.

Новая версия пока не проверена в игре SE/AE/VR и между двумя компьютерами.
NiStream capture/load остаются полными, дельта не патчит живые vertex buffers.
Не заявляется улучшение игрового frametime или поведения WAN маршрута.
512-player benchmark новой реконструкции не проведён: предыдущий pose benchmark
не является измерением одновременного восстановления512 моделей.


## Финальная сборка и установка

Diagnostic:414passed/5skipped,50 679 assertions; включает отсутствие базы с full
retry и чтение старых protocol25 offline записей (при неизменном asset2).
Diagnostic actual-server smoke:149,475ms,32 465B,4043 assertions, PASS.
Опциональный игровой replay fixture не был задан; это не считается игровым тестом.

Normal DLL и сервер собраны в `dist/`. Обычный клиент, diagnostic клиент и
самодостаточные серверы Windows/Linux упакованы штатными package_dist/package_release
функциями в `build/native-delta/release/`; рядом SHA256SUMS.txt. Diagnostic DLL
не подменяет normal DLL внутри dist. ESP, UI, примеры конфигов и managed ZstdSharp
dependency включены, игровые архивы и IDB исключены. Шесть сохранённых конфигов
рабочего дерева/dist проверены по SHA-256, включая Plugin/client.toml.

Diagnostic DLL/PDB установлены в `F:/MO2 - Skyrim - VanillaLike/mods/Dreamsleeve`,
сервер — `S:/Dreamsleeve`; процессы игры/сервера отсутствовали при установке.
Десять установленных/пользовательских конфигов сохранены побайтово. Backup и
хеши: `build/native-delta/deployment-backup`, `deployment.json`. Другая сторона
должна обновиться до protocol26. На Linux проверена упаковка, запуск не проверен.
