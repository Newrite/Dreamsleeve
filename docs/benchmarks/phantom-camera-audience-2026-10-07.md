# Камера и аудитория: protocol24, 07.10.2026

Реализован широкий серверный сектор по реальной камере и остановка live poses
без выбранных зрителей. Native asset2 / pose3 / renderer не менялись.
Сектор: вход ±105°, удержание ±120°, ближняя зона 512 units; неизвестная/вертикальная
камера не ограничивает видимость. Phantoms.CameraCulling=false отключает сектор.

## Проверки

- Server: 613/613; настоящий ENet E2E включает camera-only movement на расстоянии
  1000 units, Remove + PoseDemand(false), обратный поворот, Offer того же поколения
  и PoseDemand(true). Проверены cold/warm chunks, ACK, отмена и fragmented poses.
- C++ normal: 389/389. Два новых tests закрывают pause/resume stale worker и ранний
  demand из Models до активации контекста через Control. Ограниченное ревью сначала
  нашло initial-demand race; исправление отдельно проверено повторным ревью.
- C++ diagnostic: 402/402; production replay7архивов/2107кадров — PASS.
  Оригинальные записи не изменялись.
- C++ production Streaming UDP (normal и diagnostic): 742923 B cold asset, window32, fragmented pose3446 B,
  warmChunks0, request correlation/loss/rollover — PASS.
- Один источник, настоящий сервер, 10 Hz, 10.001 s: модель опубликована, 0 зрителей,
  0 отправленных поз, 0 pose-envelope bytes. Модель не заблокирована спросом.
- Камера SE1.5.97/AE1.6.1170/VR1.4.15 статически сверена по NiCamera ray projection;
  матрица читается через CommonLib. Игровой тест новой DLL ещё не выполнен.

## Сопоставимый сценарий 512 игроков

8 процессов клиентов; spatial groups не более25, maximum4, 10 Hz, 30 s измерения,
4 Hz actor values. Те же301 production pose payloads; raw model27740619 B,
compressed13288628 B,327каналов. Один общий hash заранее в cache. Нативный decoder
на сервере не запускается. Направление камеры у генератора отсутствует, а группа
умещается в ближнюю зону; это контроль накладных расходов, не демонстрация выигрыша
сектора. У источников почти постоянно есть зрители.

| Метрика | До (bacff8f, v23) | После (v24) |
|---|---:|---:|
| Source Hz, на всех512 | 9.9994 | 9.9892 |
| Получено Hz на секунду активной подписки | 9.3250 | 9.1952 |
| Pose envelope C→S, MiB/s | 19.2891 | 19.2694 |
| Pose envelope S→C, MiB/s | 71.5446 | 70.1275 |
| CPU сервера, эквивалент ядер | 1.3700 | 1.3959 |
| Private peak, MiB | 123.398 | 123.867 |
| Tick p95 / p99, ms | 0.184 / 11.646 | 0.208 / 12.424 |
| Delivery age p95 по workers, ms | 149.7–157.3 | 172.2–186.5 |
| Receive gap p95 по workers, ms | 251.7–316.7 | 323.1–414.7 |

Оба прогона PASS, disconnect/error нет. Это по одному прогону: выигрыш общей
производительности не подтверждён; tail latency в новом прогоне хуже. Gap включает
смену подписок и повторное появление пар, поэтому не равен packet loss. Предел
плавности при512 активных источниках остаётся отдельным предметом оптимизации.

В измеряемой фазе model payload равен0 в обоих направлениях. Во время warmup
upload25,165,824 B против23,199,744 B (состязание первых публикаций одного hash).
Это не постоянный поток моделей и не стоимость 512 разных внешностей.

Transport all lanes, за30s: ENet C→S642229501 B, S→C3401019994 B; UDP packets
1284364 /2882179. При допущении IPv4+UDP28 B на datagram суммарно21.558 /110.676
MiB/s против21.526 /111.939. Включены control, movement, ACK и fragmentation;
нельзя весь transport delta называть overhead поз. Ethernet/туннели не измерялись.

Исходные агрегаты: [JSON](phantom-camera-audience-2026-10-07.json).
Локальные подробные logs/provenance: build/native-nif/camera-v24.

## Ограничения и следующий шаг

Сектор снимает подписку и нативную сцену, но сохраняет content cache. На возврате
камеры нужен native Load; широкий угол/near zone/hysteresis уменьшают переключения,
однако момент появления следует проверить в игре. Камера не учитывает стены.

NIF delta всё ещё не реализована в сети. Офлайн block-copy/XOR показывает потенциал,
но raw reconstruction + серверная перекомпрессия не гарантирует исходный compressed
hash. Нужен самостоятельный контракт базы и результата, cancellation/budget tests;
это не должно превращаться в формат нашей геометрии или собственный renderer.

## Сборки и установка

Полный normal dist: `S:\Programming\Dreamsleeve\dist` (Client/Server/UI/ESP/config examples).
Diagnostic установлен в `F:\MO2 - Skyrim - VanillaLike\mods\Dreamsleeve`;
server обновлён в `S:\Dreamsleeve`, не запущен. Перед копированием процессы игры
и сервера отсутствовали. SHA256 всех9существующих TOML-конфигов до/после совпали.
`Plugin/client.toml` не изменялся этой частью и не включается в commit.

Diagnostic DLL SHA256: `fdabc2bf47e2b8366dd7f777d6ab556014ae5bbcb1bbc37edcb93e5ea40a80d2`.
Normal DLL SHA256: `2fe93540d0ad64a6f858bdd4b85f2ca530e71f9f697f21d1b49312e824eb30fd`.
Server.Core SHA256: `9bbd54dd8d0a177cd88ca89bb8b61c520ffe0a15dbd7e092200166035da256e1`.
Diagnostic DLL zip: `build/native-nif/camera-v24/Dreamsleeve-diagnostic-camera-v24.zip`.
Backup установленных бинарников: `build/native-nif/camera-v24/installed-backup`.
Настройка xmake возвращена в diagnostics=n; диагностические outputs отдельные.

В игре остаётся проверить два клиентаv24: свободный поворот камеры третьего лица
при неподвижном персонаже, first/third person, возвращение за спину/вперёд на
расстоянии >512units, ближнюю зону, cache reentry и обновление оружия. SE/AE/VR
статическая проверка не является новым игровым QA.
