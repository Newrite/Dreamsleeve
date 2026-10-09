# Локальная проверка HTTP после игровой сессии 08.10.2026

Самопроизвольное падение скорости последующих загрузок не воспроизведено.
Это не доказательство отсутствия ошибки на WAN: RTT90ms, потеря TCP-пакетов,
фильтры антивируса и маршрут друга этим loopback-тестом не эмулировались.

## Что выполнялось

Настоящие PhantomHttp, PhantomStorage (файловый путь, RamBytes=0) и Kestrel.
Авторизация заменена локальной выдачей одноразовой capability; серверный actor,
ENet, Game/NiStream не участвуют. Два получателя: .NET HttpClient и production
C++ Phantom.Http/WinHTTP. Последние три передачи используют **один** экземпляр
C++ Http с общей WinHTTP session и сохранёнными бюджетами между передачами.
Один серверный HTTP owner/global budget живёт всю последовательность.

Файл автора 13313497 B, hash1bd4e136d761ab04de6731853e9a122cf0b0223c1304820740ff7a7855c015e9.
Это detached compressed asset; исходные записи не изменены. Лимиты сервера5MiB/s
global и peer. Лимит native клиента5MiB/s, кроме специально медленного512KiB/s.
.NET медленный клиент читает с расписанием по cumulative bytes/Stopwatch,
а pause-case ждёт3с после headers. Это контролируемый медленный потребитель,
**не симулятор TCP RTT или потерь**.

Время измеряется Stopwatch в .NET и steady_clock в C++, Python не участвует
в передаче или измерении. Сбор результатов Python — только чтение готовых JSON.

## Результаты

| Получатель/сценарий | Полное получение тела, с | Серверный pump, с | WriteAsync, с |
|---|---:|---:|---:|
| .NET fast1 | 2,589 | 2,552 | 0,005 |
| .NET fast2 | 2,539 | 2,538 | 0,002 |
| .NET 512KiB/s | 25,394 | 15,257 | 12,735 |
| .NET fast после slow | 2,541 | 2,541 | 0,002 |
| .NET пауза3с | 4,218 | 4,218 | 1,684 |
| .NET fast после паузы | 2,539 | 2,539 | 0,002 |
| WinHTTP fast1 | 2,595 | 2,554 | 0,006 |
| WinHTTP 512KiB/s | 25,381 | 15,257 | 12,737 |
| WinHTTP fast после slow | 2,555 | 2,538 | 0,002 |

Все9 тел проверены: SHA-256 у .NET, полное byte equality у C++.
Чтение файлов суммарно25–38мс/модель. Остаток pump после вычитания disk/write
около2,50–2,51с; он включает бюджеты и прочие накладные расходы,
**не является прямым замером исключительно Task.Delay**.
Теоретическое время13,313497MB при5MiB/s —2,539с.

Медленный получатель воспроизводит длительность около25с без неисправности
серверного actor: Kestrel ждёт освобождения буферов ответа. Он может закончить
pump раньше получения тела клиентом, поскольку данные остаются в сетевых буферах.
Накопления долга, замедляющего следующую быструю передачу, здесь не наблюдается.
Сходство времени с игровой сессией не доказывает тот же первопричинный механизм.

## Исследование документации

* [Kestrel MaxResponseBufferSize](https://github.com/dotnet/aspnetcore/blob/main/src/Servers/Kestrel/Core/src/KestrelServerLimits.cs):
  при заполнении output buffer асинхронная запись ожидает освобождения места.
  Такой backpressure согласуется с локальным замером, а не означает занятость actor.
* [Task.Delay](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay?view=net-10.0):
  короткие задержки зависят от разрешения системных часов. Однако в данных прогонах
  сервер достигает заданных5MiB/s; объяснение игровых25с таймером не подтверждено.
* [Microsoft TCP performance](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/overview-of-tcpip-performance):
  потери и receive window на соединениях с задержкой ограничивают throughput;
  сравнивать нужно одинаковые конечные машины/маршрут. Для независимой проверки
  Microsoft предлагает NTttcp/ctsTraffic. Эти инструменты между игроками здесь не запускались.
* Найденная [статья WinHTTP TcpAutotuning](https://learn.microsoft.com/en-us/troubleshoot/windows-server/networking/receive-window-auto-tuning-for-http)
  явно относится к Vista. Её утверждения о значениях по умолчанию нельзя переносить
  на Windows10 только из-за свежей даты страницы. Реестр по ней не менялся.

Read-only netsh на локальной машине сообщает autotuning=normal. Это не проверка
машины друга и не измерение окна конкретного игрового TCP-соединения.

## Воспроизведение

Из корня репозитория (пути модель/output/exe лучше абсолютные):

```powershell
xmake build Dreamsleeve.Http.Benchmark
dotnet build tests/Dreamsleeve.Phantom.HttpProbe -c Release
dotnet tests/Dreamsleeve.Phantom.HttpProbe/bin/Release/net10.0/Dreamsleeve.Phantom.HttpProbe.dll $assetPath $outputDirectory $nativeProbeExe
```

`$nativeProbeExe` — build/windows/x64/releasedbg/Dreamsleeve.Http.Benchmark.exe.
Без третьего аргумента выполняются только .NET cases. Для повторения только native
установить DREAMSLEEVE_HTTP_PROBE_NATIVE_ONLY=1 в окружении запуска.
Сервер слушает только127.0.0.1 на автоматически выделенном порту; credentials
и capability не печатаются. Кеш остаётся в выбранном outputDirectory.
Probe исключён из default xmake build и dist; исходные assets не входят в Git.

Вспомогательные логи и результаты: build/session-20261008/http-final и
http-persistent-native. В раннем подготовительном запуске был неверный путь
к native exe; его незавершённые результаты не использованы как успешный native тест.

## Вывод и границы

Следующий доказательный шаг — сравнение обычного HTTP и нашего клиента по
одному маршруту друг→этот сервер с одинаковым файлом, плюс TCP-counters при
замедлении. Локально проблема очередей/накопления бюджета не подтверждена;
полностью оправдывать клиент для WAN или обвинять провайдера пока нельзя.
Production-код, протокол, DLL, dist и пользовательские конфиги не изменены.
