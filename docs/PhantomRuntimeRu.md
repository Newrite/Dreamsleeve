# Фантом: проверка SE, AE и VR

Проверено 06.10.2026 для локального стенда ветки `codex/phantom-local-se`.
Адреса, ABI-адаптеры и установка хука находятся только в `Hooks.ixx`.
Рабочий игровой replay подтверждён пользователем на SE 1.5.97. AE 1.6.1170
и VR 1.4.15 проверены статически в IDA и включены в стенде; игровой тест
этих двух runtime ещё нужен. Остальные версии локальный стенд отклоняет.

## Входные образы и метод

| Runtime | IDA database | MD5 входного образа |
|---|---|---|
| SE 1.5.97 | `E:\Reverse\SkyrimSE.exe.i64` | `cc3a69467b61093053bb766dd503b229` |
| AE 1.6.1170 | `E:\Reverse\SkyrimSE1170.exe.unpacked.exe.i64` | `9f5eb140eb54eb8d3ae613f0f395cb13` |
| VR 1.4.15 | `E:\Reverse\SkyrimVR.exe.i64` | `b9b0ac2ad6599d79c143dbb16e2b305d` |

Base образов — `0x140000000`. SE IDs сопоставлены с
`E:\Reverse\offsets-1.5.97.0.txt`, AE — с
`E:\Reverse\offsets-1-6-1170-0.txt`. Функции затем проверены в каждом
образе по vtable, xrefs, вызываемым функциям, инструкциям и полям объектов.
Перенос по похожему смещению без проверки не использовался.

IDA MCP на момент проверки: SE — 13337, AE — 13339, VR — 13338. Это
не постоянное соответствие: перед работой проверять `server_health` и имя
открытой базы. Сохранены комментарии во всех трёх базах; безымянные функции
получили имена `Dreamsleeve_Phantom_*`, полезные существующие имена сохранены.
Уточнены типы LoadBuffer, SaveBuffer и NiMemStream buffer ctor. Байты игры
не изменялись. IDB и дизассемблированные образы не входят в репозиторий/dist.

## Используемые операции

В таблице указаны **RVA**, а SE/AE ID — отдельным числом. Для VR третий
аргумент `REL::VariantID` является RVA, а не Address Library ID.

| Операция | SE ID / RVA | AE ID / RVA | VR RVA |
|---|---|---|---|
| NiStream ctor | 68971 / `C59690` | 70324 / `D1EF80` | `C9EC40` |
| NiStream non-deleting dtor | 68972 / `C598F0` | 70325 / `D1F1E0` | `C9EEA0` |
| NiStream LoadBuffer, slot 02 | 68978 / `C59EC0` | 70331 / `D1F830` | `C9F470` |
| NiStream SaveBuffer, slot 05 | 68979 / `C59F10` | 70332 / `D1F880` | `C9F4C0` |
| NiAlphaProperty factory | 69311 / `C67CD0` | 70684 / `D2E020` | `CADF10` |
| NiStream loader registry global | 523904 / `3012408` | 410484 / `3272D80` | `316AC08` |
| PlayerCharacter::Update | 39375 / `69E580` | 40447 / `732660` | `6BEC10` |

PlayerCharacter::Update принимает `(PlayerCharacter*, float delta)`.
В SE/AE это slot **AD**, в VR — **AF**. VR slot AD указывает на другую
функцию. Перед заменой проверяется адрес исходного метода; внешний хук
можно цепочить, неожиданный адрес внутри `.text` отключает стенд.
Снимок снимается после исходного Update.

NiStream имеет одинаковые virtual slots на этих runtime. LoadBuffer
принимает `(NiStream*, char*, uint32 size)` и заимствует входные байты.
SaveBuffer принимает `(NiStream*, char*&, uint32&)`: инструкция записывает
DWORD длины, хотя декларация CommonLib содержит uint64. Адаптер использует
проверенный uint32. `releaseBuffer` передаёт владение выходным буфером;
он освобождается через `RE::free`. Non-deleting dtor не освобождает сам
NiStream; после него вызывается соответствующий allocator free.

## Связанные функции: свидетельства ABI и bounds

Они исследованы для проверки пути; отдельные новые хуки на них не ставятся.

| Операция | SE RVA | AE RVA | VR RVA |
|---|---|---|---|
| NiStream LoadStream, slot 0F | `C5BA80` | `D21790` | `CA1030` |
| NiStream RegisterObjects, slot 11 | `C5C200` | `D21FC0` | `CA17B0` |
| NiStream LoadObject, slot 14 | `C5C470` | `D22240` | `CA1A20` |
| NiMemStream buffer ctor | `1312040` | `14FE330` | `135ABA0` |
| NiMemStream empty ctor | `13120B0` | `14FE3A0` | `135AC10` |
| NiMemStream dtor | `1312120` | `14FE410` | `135AC80` |
| NiMemStream releaseBuffer | `13121D0` | `14FE4C0` | `135AD30` |
| BSFlattenedBoneTree Clone copy | `C69220` | `D2F630` | `CAF460` |
| NiNode UpdateWorldBound | `C588E0` | `D1DF20` | `C9DC10` |
| BSGeometry UpdateWorldBound | `C715C0` | `D37DC0` | `CB78C0` |

NiMemStream: buffer +20, position +28, size +2C, capacity +30,
external-buffer flag +34, released flag +35. Размеры позиции/длины — uint32.
В пустом конструкторе начальная capacity — 1024; destructor освобождает
buffer только когда оба флага ложны. releaseBuffer выставляет released.
Изменённый тип конструктора восстанавливает в Hex-Rays передачу RDX/R8
из LoadBuffer: одноаргументный старый pseudocode был следствием неверного
типа вызываемой функции, а не отсутствия аргументов в машинном коде.

## Размеры и поля

| Объект/поле | SE / AE | VR | Основание |
|---|---|---|---|
| NiStream allocation | `620` | `620` | ctor/dtor, последние поля и путь файла |
| NiStream objects / topObjects | `218` / `258` | `218` / `258` | ctor и регистрация |
| NiStream lastError / lastErrorMessage | `410` / `414` | `410` / `414` | ctor, Save/Load |
| NiTLargeArray freeIndex / size | `14` / `18` | `14` / `18` | ctor/dtor массивов NiStream |
| NiAlphaProperty allocation | `38` | `38` | no-arg factory |
| NiAVObject local / world / worldBound | `48` / `7C` / `E4` | те же | трансформы и UpdateWorldBound |
| NiAVObject flags / fade | `F4` / `100` | `10C` / `118` | runtime layout и bound updates |
| NiNode children | `110` | `138` | Node UpdateWorldBound, CommonLib accessor |
| NiNode allocation | `128` | `150` | CommonLib runtime allocation, engine layout |
| BSGeometry modelBound / runtimeData | `110` / `120` | `138` / `160` | Geometry UpdateWorldBound, accessor |
| BSGeometry skinInstance | `130` | `170` | bound update и skin lookup |
| BSFlattenedBoneTree allocation | `168` | `190` | CreateClone allocation / deleting dtor |
| BoneTree numBones / entries | `128` / `130` | `150` / `158` | clone copy и destructor |
| BoneEntry size | `80` | `80` | clone copy stride |

BoneEntry: local +0, world +34, parent +68, node +70, name +78. На всех
трёх runtime Clone выделяет `numBones * 0x80 + 8`, сохраняет число в
array cookie, а entries указывает на allocation +8. Destructor освобождает
entries напрямую. `Normalize` переносит только массив клона в allocation
без cookie и сохраняет владение строками; живое дерево не исправляет.
Используется `GetRuntimeData()`, чтобы VR получал свои numBones/entries.

NiStream loader registry — указатель на string → no-argument factory map.
Map: buckets +8, table +10, count +18; entry: next +0, name +8, factory +10.
Соседний global +8 относится к другой таблице NiStream. Preflight читает
настоящий реестр текущей игры, поэтому не требует угадывать набор фабрик
и проверяет также классы, зарегистрированные модами.

`sizeof` cross-runtime обёртки CommonLib не равен allocation движка.
Стенд использует runtime-аксессоры для children, geometry data, flags,
fade и BoneTree. Ни actor, ни контейнер сцены не выделяется по sizeof
фиксированной cross-runtime обёртки. NiStream — проверенное исключение
с общим размером 620. В metadata сохраняются фактические observedOffsets.

## VR culling

В VR после worldBound расположены world AABB center +F4 и halfExtents +100.
NiNode UpdateWorldBound копирует/объединяет коробки детей; BSGeometry
UpdateWorldBound обновляет их из модели/скина. Записи только worldBound
для ручного replay недостаточно: коробка останется в исходном месте.

После применения поз и пересчёта контейнерных сфер стенд обновляет
`GetVROcclusionBox()` каждого узла: центр сферы и радиус по каждой оси.
Это консервативная коробка, не точный tight AABB; она сохраняет culling
и не отсекает сферу фантома. Отдельный native-тест проверяет её границы
и обновление при движении/нулевом радиусе. В SE/AE accessor возвращает null.

## Что ещё проверять в игре

Для AE/VR: загрузка DLL, Save/Load и полный preflight фабрик; лицо/волосы,
движение, оружие рука ↔ ножны, смена камеры, исчезновение из видимости и
возврат, загрузка сохранения, переход ячейки и завершение игры. В VR
дополнительно оба глаза, руки и влияние используемого VR body/IK-мода.
Статическая проверка не доказывает время обновления такого мода относительно
PlayerCharacter::Update. Это локальные фантомы без сетевого обмена.

## Автоматические проверки этой ветки

06.10.2026: единая DLL собрана с включёнными SE/AE/VR; native-набор —
357/357, 7619 assertions, включая шесть настоящих SE-архивов. Vitest —
95/95; Python — 3/3 (fixture форматов/кодеков); Edge Playwright — 1/1
для панели, выбора режима и освобождения chat focus. Playwright завершился
с кодом 0 после остановки двух принадлежащих тесту зависших Vite-серверов
на стадии cleanup; сама проверка UI прошла до этой остановки.

clang-format для новых модулей/изменённого Hooks и Prettier для изменённых
UI-файлов прошли. Общий UI format:check имеет прежние замечания в
`src/views/GuildsPanel.tsx` и `tests/guilds.test.ts`; они этой веткой не менялись.
Сборка production UI проверяет отсутствие development fixtures/storage.
Эти проверки не заменяют игровое тестирование AE/VR.
