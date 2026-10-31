# Очереди и алгоритмы воспроизведения, 07.10.2026

Native asset2/pose3/protocol24; NIF-дельты не внедрялись. Новый TransportPacket.Schedule
не сериализуется. Все исходные игровые записи и пользовательские конфиги сохранены.

## Изменения

- Reliable models и latest full poses разделены в TransportOwner. Источник сохраняет
  очередь обслуживания при замене payload; после8 poses обслуживается готовая model
  очередь. Non-growing replacement разрешён даже при занятом reliable reserve.
  Graceful Close ждёт все принятые очереди. Deadline pose по-прежнему200 мс.
- Worker: отдельный model thread для detached compression/hash/receive validation/cache;
  pose thread занимается кодеками поз. Exchange владеет immutable prepared generations.
  Подготовка G2 больше не останавливает ни remote decode, ни самостоятельные poses G1.
  Старое завершение после session reset или RestartCapture не устанавливает публикацию.
- Общий PlayoutClock у movement и phantom Playback: адаптивный запас2 интервала +
  EMA джиттера, плавная скорость, непрерывность при вставке. История/контекст принадлежат
  прежним владельцам. Root-only extrapolation сохраняет относительную позу костей,
  оружия и bounds вместо независимого продолжения движения каждого канала.
- Diagnostic build отдельно измеряет clone/normalize/Save/asset validation/Load/scene
  preparation; логирует целевую задержку и скорость production timeline.

## Воспроизводимая симуляция production таймера

Тест `Production playout reduces missing right endpoints under jitter and lost snapshots`
использует настоящий C++ PlayoutClock:3000 независимых снимков, PRNG seed42,
равномерный jitter±20 мс,6% потерь полных снимков, render tick16667 мкс.
Первые10 секунд прогрева исключены; fixed baseline100 мс привязан к первому пакету.
Это синтетический тест clock, не ENet и не визуальная ошибка на исходных анимациях.

| Частота | Кадров | Нет правого endpoint, fixed | Новый clock |
|---|---:|---:|---:|
|10Гц|17395|2897 (16,65%)|44 (0,253%)|
|20Гц|8399|153 (1,82%)|9 (0,107%)|

Снижение underrun покупается задержкой. При10Гц цель обычно около200 мс плюс
измеренный jitter; минимум100 мс не означает фиксированную задержку100 мс.
Гистерезис скорости и стартовая адаптация допускают некоторое отличие от цели.
Смена постоянной задержки проверена отдельными тестами; clock не перепрыгивает
при вставке нового пакета. Root prediction не восстанавливает потерянную анимацию:
детали боя при10Гц по-прежнему хуже исходного20Гц даже при идеальной сети.

## Граница native capture

Повторно проверенная SE completion ветвь Update3DModel завершает vanilla equipment,
face, world/shadow updates перед существующим Clear3DFlags hook. Готовая сцена
остаётся живой и изменяемой. CommonLib Clone не гарантирует thread-safe чтения;
Prepare обращается к live optimized bones/skin и native factories. Поэтому native
Clone/Save/Load остаются на game thread; новый поток получает отделённые bytes.
Сторонние morph/SMP очереди этой точкой полностью не покрываются.

Снижение пиков frame time при native Save/Load ещё не доказано. Нужен игровой
повтор с новыми stage timings. Одно суммарное captureMs нельзя выдавать за цену
NiStream, GPU или клонирования. AE/VR — прежняя статическая проверка; эта часть
не добавляет новых ABI и не является игровым тестом этих runtime.

## Проверки и измерения

Обычный C++ suite:394/394,17479 assertions; диагностический:407/407,17887 assertions.
Серверный suite после scheduler review:624/624; UI phantom tests4/4 и production build.
Настоящий UDP smoke прошёл для обоих clients:742923cold bytes,32-chunk window,
3446-byte fragmented pose, loss discard, FFFF rollover остаётся unreliable,
warm download0chunks. Число assertions polling-теста зависит от времени исполнения.
Все7исходных записей/2107кадров/27assets снова прошли production replay.
Это codec replay, не игровое выполнение NiStream/renderer.

Новый regression использует arrivals199,301,399,501,...ms и runtime ticks1ms:
повторная выборка по общей100ms сетке теряет промежуточные poses. Новый scheduler
отделяет refresh100ms от dispatch≤25ms. Source token bucket2 ограничивает среднюю
частоту; одна identity оплачивается один раз при частичном fanout. Shared send
credit сохраняет MaxPoseFanoutPerTick/ReplicationIntervalMs, отдельный inspection
ceiling ограничивает каждый turn. Сканирование теперь может происходить чаще;
это не обещание прежнего aggregate CPU. Последняя поза не образует backlog.

Промежуточные512прогоны выявили просадку: baseline9,195Hz, варианты очередей7,908 /
6,520 /7,866Hz. Они сохранены вbuild/native-nif/algorithms-v24/steady512* и не
выдаются за ускорение. Ввариантеfinal actor fanout402024, rx+stale400851: основная
потеря до transport. Native failures/drops0 не подтверждают гипотезу квот.
Счётчик Offers (включая warmup) исходного steady сценария составляет34–77тыс.: позиции через4units при
скорости до200units/s чувствительны к разновременности movement snapshots.
Добавлен --static-positions: та же частота/число movement packets, фиксированные
координаты для отделения queue стоимости от nearest selection churn.
A/B использует один актуальный генератор; baseline server построен из git archive
edd5672 с исходными db migrations. Архив не содержит.git, поэтому MSBuild может
показать хеш внешней рабочей ветки в assembly version; идентичность baseline
подтверждается SOURCE.txt, git archive и хешами его отдельных DLL, не этой строкой.
Первый запуск baseline отказал до нагрузки из-за пропущенного каталога миграций;
повторstatic-baseline-v2 сохраняет исправленный standalone build.
Полные агрегаты и SHA256 исходных results.json:
[phantom-algorithms-2026-10-07.json](phantom-algorithms-2026-10-07.json).
Все прогоны:512издателей,8workers, группы≤25, maximum4,10Гц,30s,
actor values4Гц; общий hash модели предварительно в кеше, модельных bytes вload0.
Пара static — один генератор с --static-positions; historical moving baseline
взят из предыдущего camera-v24 прогона с теми же параметрами. Это по одному
прогону каждой пары, не доверительный интервал и не гарантия под другой ОС/сетью.

| Сценарий | Получено,Гц на активную подписку | CPU,ядра | Tick p99,мс | Queue age p99,мс | Private peak,MiB |
|---|---:|---:|---:|---:|---:|
| Static baseline edd5672 |7,417|1,417|12,159|87,908|126,13|
| Static новый |9,595|1,552|9,678|77,120|134,12|
| Moving historical baseline |9,195|1,396|12,424|109,665|123,87|
| Moving новый |9,283|1,534|10,335|81,926|136,78|

Новый moving run:58stale-view пакетов; static0. Независимые ENet lanes не имеют
общего wire ordering; эти полные пакеты отбрасываются клиентом. Native failures0.
Новый pose-age histogram p99:80,292мс(static),82,755мс(moving), max130,147 /
145,469мс. У baseline его не было: нельзя сравнивать pose histogram с общей
семплируемой очередью как одну метрику. Runtime tick wait p99 вырос30,275→33,892мс
наstatic и27,596→34,950мс наmoving. Ускорение доставки не устранило все хвосты.

| Сценарий | Pose upload,MiB/s | Pose download,MiB/s | Весь IPv4+UDP,оценка MiB/s |
|---|---:|---:|---:|
| Static baseline |19,290|57,298|117,457|
| Static новый |19,290|74,150|135,446|
| Moving baseline |19,270|70,130|132,240|
| Moving новый |19,272|71,586|134,245|

Последний столбец = сумма ENet sent/received bytes восьми workers +28байт на
каждую UDP datagram, оба направления вместе. Он включает movement, actor values,
control и ENet overhead; это не только pose bandwidth и не packet capture Ethernet.
Рост полезной частоты увеличивает передаваемые bytes; уменьшения размера NIF/pose
этой частью нет. Модель13 288 628compressed /27 740 619raw bytes в этих load
прогонах не пересылалась. Cold transfer отдельно проверен вreal UDP smoke.

Вbenchmark server process с подключёнными метриками allocation rate остаётся
высоким: static560,7→574,8MiB/s, moving603,7→615,4MiB/s. СуммарныйGC pause за
~30s: static2043,7→2256,3мс, moving2641,5→3184,0мс. Это сумма пауз, не одна
остановка и не доля времени игрового кадра. Нужен отдельный allocation/GC profile
production сервера без влияния benchmark listeners. Нельзя объявить отсутствие
статтеров по среднемуCPU или памяти136,8MiB. Это оставшаяся оптимизация.

Новый измеренный replay2107кадров: полный client protobuf packet mean3939,06B,
p954026B,p994073B,max4100B. Это38,47KiB/s при10Гц или76,93KiB/s при20Гц доENet.
Encode mean0,1102ms,p990,2042ms;decode mean0,0166ms,p990,0330ms на этом хосте.
Это codec над detached snapshots, не native capture/Apply. Размер серверного
recipient envelope зависит от IDs/view revision и учтён вbenchmark download.
Helper теперь передаёт каталог измерений, тест требует успешного открытияCSV:
раньше путь к.csv воспринимался как каталог и метрики могли тихо не сохраниться;
само чтение/roundtrip301кадров каждого архива выполнялось и тогда.

## Сборка, установка и оставшаяся проверка

Полный ordinary dist: `S:\Programming\Dreamsleeve\dist`, Client/Server, UI,
Plugin ESP и примеры. Вordinary DLL нет diagnostics marker; diagnostic ZIP лежит
в`build/native-nif/algorithms-v24/Dreamsleeve-diagnostic-algorithms-v24.zip`.
Диагностическая DLL/PDB и UI установлены в
`F:\MO2 - Skyrim - VanillaLike\mods\Dreamsleeve`, сервер —`S:\Dreamsleeve`.
Перед копированием проверены процессы; резервная копия —
`build/native-nif/algorithms-v24/installed-backup`.11TOML сохранены поSHA256,
включаяPlugin/client.toml иMO2 Output_SKSE override. Оригинальные записи неизменны.
Хеши артефактов/установки —`build/native-nif/algorithms-v24/artifacts.json`.
Dist проверен на отсутствие archives/IDB/credentials/игровых NIF/diagnostic payload.

SE completion повторно проверен статически; новых ABI/хуков нет. AE/VR — прежняя
статическая проверка. Новая DLL ещё требует игрового QA: лицо/глаза, оружие↔ножны,
магия, смена камеры, наружныеCELL, model replacement и1/4/8видимых сцен. Прежний
положительный пользовательский replay не подменяет эту проверку. Сначала читать
раздельные `[Phantom stages]`; отдельно измерить game-frame p95/p99/max.
Не переносить Clone/Save/Load наworker без подтверждённого threading/lifetime
контракта. NIF byte deltas и death clips этой частью не внедрены.


Уведомления модели имеют приоритет только на голове существующей per-peer FIFO:
Complete не перескакивает через свои Chunk. Для Offer/Remove очередь хранит счётчик
ещё не переданных уведомлений recipient/source; соответствующий latest pose ждёт
успешного ENet admission. Остальные источники продолжают отправляться даже при
blocked model. Это зависимость принятых команд очереди, не копия готовности сцены
или подписки. Порядок между ENet lanes на самой сети всё равно не гарантируется;
клиентские проверки view/generation и независимые полные snapshots сохраняются.
