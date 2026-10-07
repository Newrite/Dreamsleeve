# Native NIF output — 08.10.2026

Production сохраняет сериализацию и рендер Skyrim. Изменены только расходы
выходного потока и повторная регистрация объектов:

- AuditPhantom регистрирует дерево/проверяет loaders в том же NiStream, который
  вызывает Save. Второй RegisterObjects останавливается на уже зарегистрированном
  обычном NiNode root. Не нужны patch/hook движка или обход графа на worker.
- Save1 пишет через NiBinaryStream в ограниченный vector. Сохранены signed relative
  seek, high-water размер, little-endian режим, предел raw asset и отказ partial output.
- Move отдаёт vector в production Asset; копия NiMemStream→vector удалена.
  Следующий capture резервирует предыдущую длину +6,25% вместо повторного роста с1KiB.
  Сохраняется только число, а не разделяемый изменяемый буфер.

ABI и цепочка регистрации проверены в SE1.5.97, AE1.6.1170 и VR1.4.15;
подробности и адреса — [PhantomRuntimeRu](../PhantomRuntimeRu.md#оптимизация-native-save--08102026).
Новый игровой захват/визуал и frame-time пока не проверены. Дельты ещё не внедрены.

## Проверяемая граница измерения

C++ executable `Dreamsleeve.NifOutput.Benchmark` использует production NifOutput.
Baseline — тестовая реализация подтверждённого алгоритма NiMemStream: capacity1024,
удвоение или размер записи, memcpy накопленных байтов, затем полная копия в vector.
Обе ветки используют CRT allocator; baseline **не запускает Skyrim allocator,
NiStream/RegisterObjects/SaveBinary**, поэтому результат нельзя выдавать за игровое
ускорение полного capture/Save и вычитать из старых26,1/52,8мс.

Вход27740607B из реальной записи idle. Первые4096B пишутся полями по4B, далее64KiB.
Это воспроизводимая модель операций output, **не trace фактических вызовов SaveBinary**.
3прогрева +30измерений каждого варианта, порядок чередуется; чтение/валидация NIF и
побайтовое сравнение находятся вне таймера. Память результатов не переиспользуется.

| Output | Median ms | P95 ms | Allocations | Дополнительные memcpy B |
|---|---:|---:|---:|---:|
| Doubling + итоговая копия | 19,088 | 20,627 | 14 | 62977471 |
| Новый первый capture | 13,072 | 16,170 | 13 | 35236864 |
| Новый, размер предыдущего capture известен | 5,565 | 5,844 | 1 | 0 |

Дополнительные memcpy не включают неизбежную запись source→output. Количество
аллокаций/copies — наблюдение за сменой data pointer и размером перед расширением;
в baseline также учитывается последняя копия. Цифры относятся только к этому набору
операций и машине. На первом варианте реализации был хуже cold output из-за роста
vector в1,5раза; измерение выявило это, финальная политика удваивает capacity.
Размеры27 проверенных моделей27580326–27739471B; запас не является новым asset limit.

## Проверки и воспроизведение

Тесты покрывают запись/перезапись/продление, gap zeroing, отрицательные seek,
точную границу бюджета и overflow, sticky failure, перенос владения без копии,
небольшой рост модели и backpatch с сохранением хвоста файла.
27 имеющихся NIF (747235108B суммарно) проходят запись с холодным/предсказанным
размером, побайтовое сравнение и production Nif::Inspect. Исходные записи не менялись.
Это проверяет output, но не сериализацию живых NiObject в игре.

Команды:

```powershell
xmake build Dreamsleeve.NifOutput.Benchmark
& build/diagnostics/windows/x64/releasedbg/Dreamsleeve.NifOutput.Benchmark.exe <appearance.nif>
$env:DREAMSLEEVE_NATIVE_NIF = '<appearance.nif>'
& build/diagnostics/windows/x64/releasedbg/Dreamsleeve.Client.Tests.exe '--test-case=Native NIF recorded bytes survive*'
```

Benchmark исключён из default target и dist. Машиночитаемый результат рядом вJSON;
локальные логи/IDA evidence/fixtures — `build/nif-output`. После игрового теста сравнить
`native_export_ms`, полный capture/frame-time, `native_audit`, `native_save` и вложенный
`native_reserve`; событие `native_output` показывает подсказку/размер/capacity.
