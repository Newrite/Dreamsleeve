# Тестовый UDP relay на .NET

Этот процесс отвечает только за пакеты имитатора: Stopwatch, независимый FIFO
каждого направления, задержку/jitter и tail drop. Python в `phantom-smoke/latency.py`
только запускает процессы и собирает результаты. Production ENet, кодеки и сервер
запускаются в исходном виде; relay не читает содержимое моделей/поз.

```powershell
dotnet build tests/Dreamsleeve.Network.Relay -c Release
dotnet tests/Dreamsleeve.Network.Relay/bin/Release/net10.0/Dreamsleeve.Network.Relay.dll --calibrate build/relay-calibration
dotnet run --project tests/Dreamsleeve.Server.Tests -- --filter-test-list "Network relay calibration"
```

`--calibrate` создаёт независимый paced UDP source + echo (без ENet/ModelFlow),
прогревает путь, затем отправляет поток 5 MiB/s на протяжении двух секунд. Успех:
полная доставка без дубликатов, измеренная скорость в пределах ±2%, отсутствие
ошибок/исчерпания ресурсов и неприемлемых пауз. В отчёте есть реальная скорость,
RTT и lateness отправителя/relay. Это калибровка на данном хосте, не гарантия для
другой нагрузки или ОС. Несопоставимые условия требуют отдельного прогона.

Обычный запуск: `dotnet ...Relay.dll config.json output-directory`; готовность
публикуется атомарно в `relay-ready.json`. Любая строка stdin завершает процесс
с записью `network.json`. JSON config: ServerPort, RttMs, JitterMs, LossEvery,
LinkMiBps (0 отключает shaping), QueueKiB. В скорость входят IPv4+UDP заголовки
28 bytes, но не Ethernet. LossEvery=0 отключает заданную потерю.

Ограничения измерителя задаются отдельно: PendingBytes (по bucket ArrayPool,
64 MiB по умолчанию), PendingDatagrams (65536), MaxPeers (8). Исчерпание любого
из них не считается потерей канала: прогон завершается неуспешно. Это тестовая
ёмкость, не policy production и не оценка всей resident memory процесса.

Dispatch lateness не обнаруживает ожидание во входном kernel socket buffer.
Поэтому отдельно записаны service-gap histogram/max. Для shaped канала пауза
дольше времени полного опустошения настроенной очереди делает прогон невалидным.
Даже при пройденной калибровке это userspace relay, а не kernel network emulator;
для окончательных выводов об интернет-каналах нужны независимые реальные прогоны.

Для других задач уже используются другие инструменты: Expecto/doctest проверяют
контракты, серверные нагрузочные процессы используют Stopwatch/MeterListener/GC,
`benchmark_enet.py --trace-server` подключает dotnet-trace. BenchmarkDotNet подходит
для отдельных CPU/allocations microbenchmarks; он не заменяет сетевой lifecycle
и многопроцессную нагрузку. В этой части BenchmarkDotNet не добавлялся.
