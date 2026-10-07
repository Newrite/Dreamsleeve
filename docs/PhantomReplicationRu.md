# Репликация фантомов

Актуальная реализация08.10.2026: [PhantomsRu.md](PhantomsRu.md).
Нативный NIF передаётся потоковым HTTP через существующий сервер авторизации;
ENet Models передаёт допуск/публикации/отмену, независимые компактные позы —
unreliable sequenced. Контракт25/asset2 описан в [Protocol](../Protocol/README.ru.md).

Исходные решения и измерения прототипа остаются в
[PhantomMeasurementsRu.md](PhantomMeasurementsRu.md) и
[PhantomPrototypeRu.md](PhantomPrototypeRu.md). Они не являются измерениями
новой сетевой сцены. Альтернативный neutral renderer удалён из production.

Владение, бюджеты, проверка отмены и измерения: [HTTP migration](benchmarks/phantom-http-2026-10-08.md).
