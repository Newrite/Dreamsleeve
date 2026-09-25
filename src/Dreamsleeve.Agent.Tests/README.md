# Regression tests / Регрессионные тесты

Requires the .NET 10 SDK or a newer compatible SDK. No test framework or third-party
test packages are required; the executable exits with a nonzero code on failure.
The runner contains 28 regression scenarios.

Требуется .NET SDK 10 или более новый совместимый SDK. Тесты работают без отдельного
тестового фреймворка; при ошибке процесс возвращает ненулевой код завершения.
В наборе 28 регрессионных сценариев.

Run from the package root / Запуск из корня пакета:

```sh
dotnet run --project tests/Dreamsleeve.Agent.Tests -c Release
```

The suite uses `TaskCompletionSource` gates to establish ordering, plus `WaitAsync`
guards to detect hangs. Only actual timeout tests use short timers. Assertions cover
public behavior: bounded backpressure, request/reply outcomes, eviction and queue
accounting, abort/fault cleanup, graceful drain, terminal notifications, and state
wrapper policies. The idle-abort regression repeats the handler handshake with
bounded and unbounded synchronous-continuation channels, because the public API
does not expose when the reader parks. Concurrent `Complete`/`Abort` calls also
verify one consistent terminal outcome. These are executable regression checks, not a stress test or a
formal proof of every possible thread interleaving.

Порядок действий задаётся через `TaskCompletionSource`; `WaitAsync` ограничивает
ожидание при возможном зависании. Короткие таймеры используются только в тестах
самих тайм-аутов. Проверяется публичное поведение: backpressure, ответы на запросы,
вытеснение и счётчики очереди, очистка после остановки, корректный graceful shutdown,
уведомления о завершении и политики stateful-обёрток. Это регрессионные проверки,
а не нагрузочный тест и не доказательство всех возможных межпоточных чередований.
Проверка остановки простаивающего агента повторяет handshake для bounded и
unbounded каналов с синхронными продолжениями: публичный API не раскрывает момент
перехода reader в ожидание. Дополнительно проверяется согласованность результата
при одновременных вызовах `Complete` и `Abort`.
