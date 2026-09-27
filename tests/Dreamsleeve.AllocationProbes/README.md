# Allocation probes

Небольшие синхронные пробы для [плана оптимизации](../../docs/MovementOptimizationPlanRu.md).
Используют настоящий Domain/Agent и локальные варианты представления данных;
production код не меняют. Дополнительных NuGet-пакетов нет.

```powershell
dotnet run --project tests/Dreamsleeve.AllocationProbes -c Release -- allocation-probes.json
```

Каждая операция прогревается, затем измеряется три раза. В JSON — все повторы,
`GC.GetAllocatedBytesForCurrentThread`, exploratory timings и Unsafe.SizeOf
для текущих типов и локальных struct-аналогов. Fixtures создаются до измерения,
результаты удерживаются через GC.KeepAlive, чтобы они не были устранены JIT.
GC.Collect вызывается только между повторами в этом диагностическом процессе.

Операции строго синхронные; переносить thread-local метод на async delivery нельзя.
Сравнение snapshot reuse-map моделирует новую запись с неизменной immutable картой,
а не проверяет ещё не реализованную инвалидацию кеша. Модель batch сохраняет
общую PlayerLocation ссылку; массив результата всегда отдельный, переиспользуется
только рабочий ResizeArray. Нулевая аллокация HashSet после прогрева не означает
нулевую удерживаемую память. Измерение admission notification — отдельный TCS,
а не полный mailbox dequeue.

Пробы не заменяют BenchmarkDotNet для точных сравнений времени или сетевую
матрицу для throughput/latency. Сохранённый запуск:
[allocation-probes-2026-09-27.json](../../docs/benchmarks/allocation-probes-2026-09-27.json).

Ошибки fixture/вывода печатаются в stderr с exit code 1; они не выходят из entry point необработанными.
