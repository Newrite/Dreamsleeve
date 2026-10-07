# Дополнение07.10.2026: native NIF production

NiStream/Clone/нормализация из разделов прототипа снова используются в
production. Последующий раздел neutral ниже — **исторический аудит удалённой
реализации**, его factories/readback/upload больше не являются текущим путём.

В этой сессии повторно проверены select_instance → server_health → survey
для всех трёх IDB. Base0x140000000. SE/AE input_path и MD5 совпадают с таблицей;
VR IDB E:\Reverse\SkyrimVR.exe.i64 сообщает input_path
D:\Programs\IDA Pro\data\SkyrimVR.exe.1415\SkyrimVR.exe; hash текущая
survey не возвращает, предыдущий MD5 ниже — историческое свидетельство.
Повторная disasm Save/Load во всех трёх базах подтверждает RDX/R8 arguments,
запись DWORD длины, virtual calls +0x20/+0x08 и освобождение memory stream.
Остальной layout переносится из документированного аудита прототипа;
нового игрового подтверждения SE/AE/VR для production этой ветки нет.

Вызов после Main::Update сохраняется из полной клиентской архитектуры.
SKSE Hooks_NetImmerse.cpp (локальный skse64_2_00_20) посылает
NiNodeUpdateEvent **после** ActorProcessManager::UpdateEquipment.
CommonLib SKSE::NiNodeUpdateEvent.reference — заимствованный TESObjectREFR*;
callback сравнивает игрока и отмечает atomic запрос аудита, указатель не хранит.
TESEquipEvent.actor — NiPointer, событие лишь ускоряет последующий аудит.
Событие не гарантирует завершение: вызванная функция может поставить задачу
в очередь. Hook завершённой ветви добавлен ниже; hooks на намерение не нужны.


## Материалы аксессуаров: исправление07.10.2026 после сетевого теста

`Auxiliary` сохраняет skinned BSEffect geometry как часть силуэта. Ранее следующий
lighting-only guard отвергал её и останавливал публикацию всей модели. Теперь
после Clone/Pair, до фильтрации клона, такая поверхность получает **новую нативную**
BSLightingShaderProperty. Оригинальный shader только читается; source geometry,
skin и buffers не меняются. Engine clone может разделять или пропускать effect
property — оба случая закрываются этой заменой. Остальные shared required
lighting properties по-прежнему запрещены. Затем общий Ghostify удаляет внешние
текстурные зависимости и сохраняет обычный native lighting NIF. Renderer не менялся.

| Native no-arg factory | SE1.5.97 ID / RVA | AE1.6.1170 ID / RVA | VR1.4.15 RVA |
|---|---|---|---|
| BSLightingShaderProperty | 99847 /12C4B10 |106492 /14AC610 |1302ED0 |
| ctor, свидетельство внутри factory |12C50F0 |14ACC20 |13034B0 |
| allocated bytes |160 |160 |178 |

Значения RVA/layout в таблице hex. Проверка текущих IDB: последовательные
list_instances/select_instance → server_health → minimal survey → disasm;
SE дополнительно decompile ctor. Input paths/imagebase/hashes совпадают с
описанными выше. Factory принимает **ноль аргументов**, возвращает engine object
pointer через RAX; alloc/ctor принадлежат движку. Ctor ставит default lighting
material через BSShaderProperty::SetMaterial. Вызов только на уже установленном
main-loop thread. Результат сразу удерживается NiPointer, затем shaderProperty
клона; ручных sizeof allocations/free нет. Hooks использует VariantID с **VR RVA**,
а не третий Address Library ID. CommonLib CreateMaterial (scrap allocator) здесь
не используется. Операция не добавляет нового vtable callsite.

Отдельно проверен accessor материала BSEffect: vtable из CommonLib
SE ID304580/RVA185E3D0, AE ID254763/VA141AB76D8, VR RVA18FED40.
Slot32 getter (hex): SE RVA12D6400, AE14BE9C0, VR1315370; соседний
slot31 setter:12D6410 /14BE9D0 /1315380. Во всех трёх disasm getter:
`mov rax,[rcx+78h]; movss xmm0,[rax+54h]; ret`. Подтверждены pointer
BSShaderProperty.material +78 и float BSEffectShaderMaterial.baseColor.alpha
+54. Setter получает this вRCX, float вXMM1 и пишет тот же DWORD после
ограничения снизу нулём. Код capture использует inline CommonLib accessor/field,
не прямой virtual call; численные offsets в Game не добавлены.

Это статическая проверка SE/AE/VR. В присланном логе отказавший mesh называется
`shades`, но класс shader прежний лог не записывал и source NIF не приложен.
Поэтому связь именно этого аксессуара с BSEffect — пока гипотеза. Новая ошибка
содержит RTTI/skin, успешная конверсия — имя поверхности. Игровая проверка
конкретного аксессуара и AE/VR остаётся открытой.

## Завершение изменения3D: повторный аудит07.10.2026

SE/AE/VR instances выбраны последовательно через list_instances/select_instance,
server_health и minimal survey. Imagebase0x140000000. SE MD5cc3a69467b61093053bb766dd503b229,
AE MD59f5eb140eb54eb8d3ae613f0f395cb13. VR input_path отличается от IDB path,
как указано выше; hash survey недоступен. Address mappings сверены с исходными
таблицами: SE decimalID/hexRVA, AE decimalID/hexVA, VR CSV decimalID/hexRVA.

| Операция | SE1.5.97 ID/RVA | AE1.6.1170 ID/RVA | VR1.4.15 RVA |
|---|---|---|---|
| AIProcess::Update3DModel_Impl |38404 /650DF0|39395 /6E3B70|65A140 (CSV ID38404)|
| Вызов Clear3DFlags после реальной работы |6511F0 (+400)|6E3F72 (+402)|65A540 (+400)|
| Clear3DFlags |38868 /67E3F0|39909 /711B00|687870 (VariantID RVA)|
| Чтение flags в начале update |67E430|711B40|6878B0|

Update имеет ABI void(AIProcess* RCX, Actor* RDX). Ветка task-pool вызывает
постановку задачи (SE5C37D0/AE655960/VR5CBD70) и уходит в эпилог без completion
callsite. Поэтому hook возврата Update и SKSE NiNode event были бы преждевременны.
В завершённой ветви сначала выполняются equipment/face/tree update, затем
world update и shadow update, затем вызов Clear3DFlags. Проверено disasm caller;
SE pseudocode сверён с инструкциями. AE/VR decompiler отказал, вывод основан на
disasm, а не на предположительной сигнатуре Hex-Rays.

Clear имеет ABI void(AIProcess* RCX), читает nullable pointer [RCX+8], обнуляет
**один byte** [middleHigh+311], не выделяет память, return value не используется.
Одинаковые инструкции подтверждены во всех3IDB. Hooks использует точный
callsite и проверяет E8/target перед patch; если другой мод его изменил, hook
не ставится и остаётся bounded audit. Исходный executable не патчился в IDA.

Thunk вызывает оригинал и только atomic RequestAudit. Он не читает actor и
не удерживает engine pointer; допустим также engine task thread. Уведомления
любых actor объединяются, main-loop проверяет только игрока не чаще4Гц.
В тишине остаётся1Гц fallback для сторонних правок. Сериализация/clone в callback
не выполняются. Владение native объектами не меняется; allocator не требуется.
Это completion vanilla3D pipeline, не универсальная точка завершения SMP,
RaceMenu morph queue или GPU-only edits. Новая DLL и этот hook ещё требуют
игрового подтверждения; статический AE/VR аудит не считается игровым тестом.

RaceMenu/skee64 локальный IActorUpdateManager v2 FlushCallback проверен по
ActorUpdateManager.cpp: Flush ставит morph/overlay задачи в очередь, затем
уведомляет наблюдателей. Это не completion barrier. BodyMorph UpdateModelWeight
также может defer task. Привязка к flush как к завершённой геометрии отклонена.
Страховочный bounded audit видит partition replacement и стабилизировавшиеся
изменения dynamicData (BSSpinLockGuard); пользовательские GPU-only изменения
не считаются покрытыми. Непрерывная мимика и phase сторонних SMP/VR IK требуют
игровой проверки; новая network scene не наследует статус visual-tested прототипа.

Все собственные numeric addresses/offsets/ABI adapters находятся в Hooks.
Game использует CommonLib runtime-accessors, нативные virtual operations,
NiPointer и ObjectRefHandle. Файл игры не патчился для исследования.

---

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

## Live feature: ABI-аудит neutral capture/render, 06.10.2026

### Граница с историей прежнего прототипа

Разделы выше описывают **older prototype history** ветки
`codex/phantom-local-se`: локальный NiStream replay и его собственные
игровые/автоматические проверки. Они не подтверждают загрузку сетевой
модели или игровой запуск нового live feature. В частности, описанные
выше LoadBuffer/SaveBuffer/Clone и исправление bone-array cookie не
являются операциями нового renderer.

Этот раздел относится к `codex/phantom-replication`, native commit
`69e8487` после Core commit `3ca0808`. Статический audit этого commit
выполнен чтением исходников и трёх IDA-баз без source edits и xmake.
Последующие согласованные исправления Hooks и Scene описаны ниже;
xmake для этих исправлений аудитор не запускал.
Parent сообщил об успешном DLL build session 70409 (91.5 s), после
исправления MSVC LNK2001: пять внутренних storage variables Graphics
стали обычными definitions именованного module вместо inline globals.
Это свидетельство сборки от parent, а не результат отдельной сборки
аудитора. Новый игровой replay пока не подтверждён.

Capture работает на main thread после полного Main::Update и читает
живую third-person модель, включая режим первой камеры. Сеть получает
только neutral Asset/Snapshot. Scene принимает только ValidatedAsset;
CheckSnapshot принадлежит Core. Remote mesh создаётся новыми native
объектами и D3D-ресурсами; peer bytes не передаются NiStream, engine
LoadBuffer или файловому texture loader.

### Factory ABI и фактические allocations

Все адреса далее — RVA, base `140000000`. Их использование остаётся в
Hooks; таблицы приводятся как свидетельства, не как новые Game relocations.

| Объект | SE ID / RVA | AE ID / RVA | VR RVA | Native allocation SE/AE → VR |
|---|---|---|---|---|
| NiNode no-arg factory | 68935 / `C57980` | 70286 / `D1CE60` | `C9CBF0` | `128` → `150` |
| BSTriShape no-arg factory | 69284 / `C67490` | 70655 / `D2D730` | `CAD6D0` | `160` → `1A0` |
| BSLightingShaderProperty no-arg factory | 99847 / `12C4B10` | 106492 / `14AC610` | `1302ED0` | `160` → `178` |
| NiAlphaProperty no-arg factory | 69311 / `C67CD0` | 70684 / `D2E020` | `CADF10` | `38` → `38` |
| NiSourceTexture create-unrendered helper, **one argument** | 69335 / `C68D20` | 70717 / `D2F140` | `CAEF60` | `58` → `58` |
| NiSourceTexture ctor, caller-allocated this | 69338 / `C68DE0` | 70720 / `D2F200` | `CAF020` | helper above owns allocation |
| BSDynamicTriShape no-arg factory (capture ABI evidence; remote Scene does not use it) | 69562 / `C72180` | 70946 / `D38A20` | `CB8530` | `180` → `1C0` |

No-arg callbacks имеют ABI `RE::NiObject* (*)()`. IDA показывает настоящий
engine allocation и последующий ctor; ручной sizeof allocation для
NiNode/BSTriShape/lighting/alpha не используется. NiNode начинает с пустого
children array и growBy=1. BSTriShape начинает с нулевых u16 vertex/triangle
counts; native type=3, у BSDynamicTriShape type=4.

Registry initializers подтверждены отдельно: SE `C7AA50` и `1294E00`, AE
`D41880` и `147F1E0`, VR `CC12A0` и `12CD830`. Зарегистрированные имена:
`NiNode`, `BSTriShape`, `BSLightingShaderProperty`, `NiAlphaProperty`.
Map/entry layout совпадает с таблицей старого аудита.

**P1 на commit 69e8487, исправлено в Hooks:** обязательный registry lookup `NiSourceTexture`
не имеет подтверждённой vanilla регистрации. В SE просмотрены все callers
registration entry `C5B040`: статические initializers его не добавляют;
caller `D36D20` только создаёт alias из Loaders.ini для уже существующего
loader. Строка NiSourceTexture имеет xrefs из RTTI static ctor и старого
unregister. AE/VR initializers выше также его не добавляют. Наличие RTTI
или старого unregister не является доказательством существования factory.
По исходникам commit это приводит к отказу Install
`graphics.registry-class:2` на обычном runtime. Мод мог добавить entry,
но live feature не должен зависеть от этого.

Реализовано в Hooks::PhantomTexture: FactoryKind::Texture возвращает
no-arg thunk, вызывающий настоящий create-unrendered helper с локальной
пустой RE::BSFixedString. Остальные четыре factories используют registry;
Ops API и gate SE 1.5.97 / AE 1.6.1170 / VR 1.4.15 сохранены. Helper ABI:
`RE::NiSourceTexture* (*)(const RE::BSFixedString*)`.
Нельзя cast helper прямо к no-arg Factory. Все три helpers выделяют `58`,
вызывают настоящий ctor, копируют FormatPrefs/name и не вызывают renderer
texture loader. Ни внешнее имя файла, ни peer data здесь не нужны.

Ctor добавляет объект в native texture linked list, обнуляет resourceStream
(+40), но **не записывает rendererTexture (+48)**. PhantomTexture явно
устанавливает `result->rendererTexture = nullptr` до передачи Graphics.
Пары ctor/list removal проверены: SE `C68FE0/C69080`, AE inline insertion в
`D2F200` / removal `D2F350`, VR `CAF220/CAF2C0`. Create helper нельзя заменить
zeroed sizeof object: этим будут пропущены NiRefObject/texture-list invariants.

### Geometry/dynamic destruction и renderer wrappers

| Путь освобождения | SE RVA | AE RVA | VR RVA |
|---|---|---|---|
| BSTriShape non-deleting dtor | `C675F0` | `D2D890` | `CAD830` |
| BSDynamicTriShape deleting dtor | `C72C60` | `D395B0` | `CB9010` |
| NiSourceTexture deleting dtor | `C68F40` | `D2F3F0` | `CAF180` |
| TriShape renderer-wrapper release | `D6C320` | `E46810` | `DBE0D0` |
| Texture renderer-wrapper release | `D6EC70` | `E49990` | `DC0AE0` |

BSTriShape dtor освобождает rendererData через renderer virtual slot +28
и зануляет pointer. BSDynamicTriShape сначала освобождает dynamicData
родным парным allocator path, затем dynamic renderer wrapper через renderer
slot +58 и зануляет его; следующий BSTriShape dtor уже не освобождает тот же
wrapper повторно. Native deleting dtor освобождает object allocation
`180` в SE/AE и `1C0` в VR. Capture только копирует live dynamicData; не
освобождает, не чинит и не клонирует источник.

Dynamic runtime fields: SE/AE data +160, lock +168, dataSize +170,
frameCount +174; VR соответственно +1A0/+1A8/+1B0/+1B4. Native factories
обнуляют эти fields. Копирование идёт под try-lock, без spin на physics job;
layout owner/count BSSpinLock подтверждён CommonLib BSAtomic. Dynamic path
читает текущие bounded positions, а не cached GPU readback. Посторонний
асинхронный post-skin layout без синхронизации не считается поддержанным.

TriShape renderer wrapper имеет общий размер **30**: VB +0, IB +8,
VertexDesc +10, refCount DWORD +18, CPU vertex/index shadows +20/+28.
Release выполняет atomic decrement +18; при переходе 1→0 вызывает COM
Release для обоих buffers, engine free для обоих shadows и sized free(30)
для wrapper. SE shadow free `F7C40` идёт в MemoryManager::Deallocate с
alignmentRequired=false (`C02560`), соответствующий RE::free.

Texture renderer wrapper имеет общий размер **28**: resource +0, UAV +8,
SRV +10, height/width +18/+1A, mips/format +1C/+1D, refCount DWORD +20.
Release выполняет atomic decrement +20, COM Release SRV/resource/UAV и
sized free(28). Это обычные renderer structs, не Ni objects: здесь общий
sizeof CommonLib допустим, в отличие от cross-runtime NiNode/BSTriShape.
NiSourceTexture dtor освобождает rendererTexture через renderer slot +E0,
сбрасывает resourceStream owner и удаляет texture из native linked list;
сам object allocation равен 58 во всех трёх runtime.

### SetMaterial: bool ABI, copy и shared ownership

| Операция | SE ID / RVA | AE ID / RVA | VR RVA |
|---|---|---|---|
| BSShaderProperty::SetMaterial | 98897 / `1291D40` | 105544 / `147BFF0` | `12CA650` |
| Material manager acquire/copy | `130BB80` | `14F7790` | `134EDC0` |
| Material manager release | `130BE10` | `14F7A40` | `134F050` |

Callback signature:
`void (*)(RE::BSShaderProperty*, RE::BSShaderMaterial*, bool unique)`.
Native return value не используется. Третий аргумент читается как bool
в `r8b`: test instructions по VA `141291D56`, `14147C006`, `1412CA666`.
Property material pointer находится +78 во всех трёх runtime.
SetMaterial сохраняет старый pointer, получает новый через manager,
публикует его в +78, затем освобождает старый через manager release.
Четвёртый manager argument вычисляется внутри функции из property flags;
четвёртого caller argument для SetMaterial не требуется.

На unique path manager вызывает virtual Create (+8), virtual Copy (+10),
затем InterlockedIncrement material.refCount (+8). Доказательства SE:
Create/Copy по VA `14130BCFA/14130BD14`, refcount `14130BD94`; AE
`1414F791E/1414F7938/1414F7959`; VR
`14134EF3A/14134EF54/14134EFD4`.
Входной temporary остаётся caller-owned и может быть уничтожен после
уникальной копии. При unique=false manager вправе сохранить исходный
pointer; для caller-owned temporary этот режим непригоден.

Default lighting material Create в SE `12D0000` выделяет A0; deleting
virtual dtor `451090` освобождает A0. CopyMembers `12CEF40` копирует texture
owners +48/+58/+60/+68/+78 через NiPointer refcounts, а не переносит сырой
borrowed pointer. Поэтому локальные diffuse/normal owners могут выйти из
scope: уникальная material copy продолжает владеть текстурами. Material
manager release уменьшает refcount, удаляет map entry на нуле и вызывает
virtual deleting dtor; вручную удалять property-owned material нельзя.
OwnEmit flag сохраняется, поскольку lighting ctor отдельно выделяет C
для emissiveColor, а lighting dtor освобождает его по этому flag.

### D3D/caches, memory и fixed-commit review

New Scene owns fresh VB/IB, CPU shadows и сгенерированные RGBA masks/normal
textures. Renderer wrappers получают refCount=1 и владеют COM references
через Detach; все error paths до передачи wrappers используют RAII.
Поза не изменяет geometry/material/resources живого игрока.
Source cache использует owning NiPointer/COM owners; engine-owned source
buffers/textures разделяются по refcount, не передаются Scene и не удаляются
ручным free. Renderer forwarder/context и lock берутся через CommonLib
runtime accessors, без дополнительных адресов в Game.

Readback планирует staging/result/copy peaks и cache row capacities до
CreateBuffer/CreateTexture2D/reserve/resize через обязательный
`reserveReadbacks(totalPlannedBytes)`. Callback принадлежит Exchange budget.
Evictions публикуют уменьшенный total; ClearReadbacks освобождает containers
и owners, уведомляет 0; Shutdown затем освобождает alpha shader и сбрасывает
Ops/factories до RE teardown. Физический D3D driver overhead и command-queue
retention не являются точными requested bytes этой модели.

Default Limits.geometry теперь 512: pool caps geometry*2=1024 buffers и geometry=512 masks.
Completed entries остаются на Open retries и обновляют touched TTL (10 s);
это предотвращает повторное начало всех reads на моделях с >16 streams.
Audit RefreshMesh инвалидирует streams один раз на transaction и не
переиздаёт staging copy на каждом Busy retry. Changed dynamic positions
копируются каждый Sample. BC7 alpha извлекается typed GPU SRV compute
shader; DDS/path load и DirectXTex dependency не используются.
Compute shader/class instances, SRV0 и все восемь CS UAV slots сохраняются и
восстанавливаются; UAV counters сохраняются. Read Map использует
DO_NOT_WAIT, upload WRITE_DISCARD использует flags=0.

Scene RequiredBytes/Requirements не выделяют native resources; MemoryBytes
включает requested CPU/GPU/native/scratch bytes и удерживаемый immutable
ValidatedAsset::MemoryBytes() в cpuBytes. Scene сохраняет shared ownership
старого Asset во время replacement, тогда как Exchange descriptor reservation
уже может относиться к новому меньшему Asset. Поэтому каждая current/candidate
Scene независимо резервирует свой Asset; консервативное повторное начисление
для общего Asset намеренно. Snapshot storage остаётся в Core.
RequiredBytes и Admit используют один Requirements; MemoryBytes равен этому
Total до Clear. Clear освобождает asset ownership и сбрасывает MemoryBytes
в ноль. Apply использует общий FrameBudget до visible mutation;
ApplyCost — work units, не wall-clock deadline. Около 100k skinned vertices
в одном piece стоят 900k units плюс 16*boneRecords и 2*nodes; dynamic добавляет
2*vertices. Ранее успешный frame сохраняется при отказе до mutation, но Busy
после upload failure может означать Ready=false.

Read-only review 69e8487 подтверждает parent fixes: Busy Open повторяется на
следующем sampling tick; ForgetCleared удаляет slots с MemoryBytes=0 и
обновляет SceneMemory; current с Ready=false больше не active; combat
represented вычисляется после Hide/ForgetCleared. Пять non-inline module
storage definitions Graphics исправлены parent. Выявленный NiSourceTexture
lookup исправлен отдельным Hooks thunk, описанным выше. После завершения
native compile 40201 (reviewfix log: build ok, 86.672 s) изменён только Scene
reservation для удерживаемого Asset. Hooks/Scene fixes проверены
clang-format и git diff --check; новый DLL build и runtime проверяет parent.

Ограничения текущего neutral contract сообщаются явно: legacy NiTriShape/strips,
несовместимые nonshared skin partitions, скиненные effect materials и
неизвестные несинхронизированные dynamic layouts возвращают failure. Для
alpha material с clamp mode != repeat нужен новый neutral sampler/clamp field;
до его появления выдаётся UnsupportedGeometry для этой детали. Capture
изолирует её: Open помечает omission, Sample скрывает только её slot,
диагностика показывает неполную репрезентацию. Остальная сцена продолжает
работать; правила восстановления описаны в PhantomDiagnosticsRu.md.
Runtime factory preflight в первом Main::Update, реальный BC7 hair/face/body fixture,
SMP update ordering, переход cell/world, disconnect и quit всё ещё требуют
игрового теста новой feature на каждом runtime.

Уточнение packed positions от 06.10.2026: SE `C66F80` через resource manager
slot 2 вызывает renderer loader `D6B8D0`. Он сохраняет descriptor без изменения,
читает vertexCount*(low nibble*4) байт в CPU shadow +20 и передаёт те же байты
GPU. SSE stream-100 rigid meshes могут иметь FP32 позиции без VF_FULLPREC:
проверенный Warhammer_Mesh descriptor `1B00000650407`, stride28, UV16/normal20/
tangent24. Нельзя определять FP16 только по отсутствию bit54. Общий byte decoder
определяет layout по footprint до следующего атрибута, сохраняет FP16 для
8-byte streams и dynamic override. Добавление новых offsets/хуков не требуется.
Декодирование всех 3223 позиций локального mesh проверено вне игры; GPU capture
этой версии и AE/VR gameplay всё ещё не проверены.

Уточнение capture scheduling от 06.10.2026: неизменные пропуски geometry
не требуют повторного Open по таймеру. Восстановление pending read сначала
проверяет готовность ресурса; смена источника проверяется cheap stamp, включая
SRV diffuse. Периодический deep audit распределён по одному mesh за sample,
не чаще одного раза в пять секунд для каждого mesh. RefreshMesh обновляет
только buffers; shared alpha удерживается активной моделью и сравнивается
по identity перед сравнением pixels. Это убирает повторные GPU alpha reads
и полный hash общих масок на каждом audit. In-place alpha updates с прежним
SRV не отслеживаются автоматически и требуют explicit invalidation.

## Чтение направления камеры, protocol24 (2026-10-07)

Game/World читает NiCamera.world.rotate через CommonLib, первый столбец — forward.
Поиск ограничен 32 объектами под PlayerCamera.cameraRoot, только на игровом потоке;
указатели за кадр не сохраняются, новые engine calls/hooks/allocators не добавлены.
Ось сверена с NiCamera WindowPointToRay и helper построения луча,
включая movss/addss в машинных инструкциях всех трёх образов:

| Runtime | Address Library ID / RVA | Основание |
|---|---|---|
| SE 1.5.97 | 69263 / C65760; helper C65860 | нулевые проекционные слагаемые оставляют world[0][0], [1][0], [2][0] |
| AE 1.6.1170 | 70630 / D2B650 | та же формула встроена в функцию |
| VR 1.4.15 | 69263 / CAB2C0 (CommonLib CSV); helper CAB3B0 | луч читает +7C/+88/+94; disasm подтверждает float32 поля |

Imagebase 140000000, IDB/input сверены через server_health. В SE таблице ID→RVA,
в AE ID→VA, VR CSV ID→RVA. +7C — общий NiAVObject.world в установленной CommonLib;
численные offsets используются только как доказательство, не в Game-коде.
Чтение поля не вызывает WindowPointToRay и не зависит от её ABI. Ни один адрес
новой операцией не релокируется. Это статическая проверка направления; реальный
VR head tracking, свободное третье лицо и моды камеры требуют игрового теста.

## Повторная проверка готового снимка и потока, 07.10.2026

SE1.5.97: IDA `E:\Reverse\SkyrimSE.exe.i64`, input `SkyrimSE.exe`,
imagebase0x140000000. Повторно разобран AIProcess::Update3DModel_Impl
VA0x140650DF0 (RVA0x650DF0); подтверждены ветвь task queue0x1405C37D0,
обновление equipment/FaceGen, world0x141291F30, shadow0x1412B99F0 и
Clear3DFlags через callsite0x1406511F0. Это уже существующий completion hook.
Он сообщает о законченной vanilla работе, но не передаёт immutable serialized
asset. Его callback по-прежнему только atomic audit request.

CommonLib NiObject::Clone — wrapper REL::RelocationID(68835,70187), без
контракта безопасного чтения живого дерева из другого потока. Собственный
Prepare дополнительно читает live skin/optimized bone transforms, создаёт
NiNode и shader property. Поэтому новый model worker получает уже отделённый
ValidatedAsset, а Clone/Save/Load не перенесены туда на основании одного
наличия NiPointer. Новые адреса/ABI/хуки в этой части не добавлялись.
AE/VR сохраняют прежний статический статус; новых игровых проверок нет.

Diagnostic build пишет `[Phantom stages]`: topology, clone, normalize/ghost,
NiStream Save; отдельно ValidateAsset и bind/probe/initial pose; при восстановлении
NiStream Load и scene preparation. Время включает работу именованного блока
на CPU и возможные ожидания внутри него, не является измерением GPU.


## Повторный аудит стоимости NiStream, SE 1.5.97 — 07.10.2026

IDA input `E:/Reverse/SkyrimSE.exe`, IDB `E:/Reverse/SkyrimSE.exe.i64`,
imagebase `0x140000000`, MD5 `cc3a69467b61093053bb766dd503b229`.
Проверены health, minimal survey, decompile и инструкции. Таблица offsets SE:
первая колонка — десятичный Address Library ID, вторая — hex RVA (не VA).
Новые наблюдения ниже относятся только к этому образу; AE/VR в этой итерации
не переаудировались, новый runtime hook не установлен.

| Операция | SE ID / RVA | Проверенное поведение |
|---|---|---|
| SaveBuffer | 68979 / C59F10 | RCX=NiStream*, RDX=char**, R8=uint32*. Стековый NiMemStream; вызывает vslot04, передаёт владение output через releaseBuffer. |
| Save(NiBinaryStream*) | 68981 / C59FE0 | this RCX, output RDX; сохраняет output по+2A0, вызывает vslot10, очищает+2A0. |
| SaveStream | 69021 / C5C040 | bool(AL), this RCX. vslot11 RegisterObjects, заголовок/таблицы, каждый object->vslot1B SaveBinary(object RCX, NiStream RDX), таблица размеров и top objects, освобождение registry/map. |
| NiMemStream empty ctor | 101030 / 13120B0 | Начальный capacity1024; pointer+20, uint32 position+28/size+2C/capacity+30, флаг+34. |
| NiMemStream write | 101038 / 1312280 | RCX=stream, RDX=bytes, R8D=count; uint32 result EAX. При недостатке capacity выделяет max(2*capacity,position+count), копирует прежние size bytes, освобождает прежний буфер, затем копирует вход. Подтверждено disasm, включая ширины полей. |

SaveStream действительно повторяет RegisterObjects после нашего отдельного
AuditPhantom. Поэтому старые35–50мс `nistream_save_ms` из двухклиентского теста
нельзя целиком называть временем native Save: туда входили оба прохода,
проверка загрузчиков, служебные операции, копия результата и уничтожение stream.
Новые таймеры разделяют эти стадии. Рост NiMemStream доказывает наличие копий,
но не их долю в задержке на конкретной модели. Резервирование output и повторное
использование регистрации — кандидаты, пока не внедрённые оптимизации; потребуют
проверки корректного SaveStream/ABI по всем поддержанным runtime и игровых измерений.

Повторно проверен Update3DModel_Impl SE38404/RVA650DF0. Очередная ветвь
IsTaskPoolRequired только ставит задачу; в завершённой ветви Clear3DFlags
вызывается поRVA6511F0 (RCX=AIProcess) **после** FaceGen/экипировки, обновления
NiAVObject и shadow scene. Существующий hook уже отмечает ревизию там, а capture
позже выполняется после Main::Update. Функция обновляет нативное дерево,
не выдаёт готовый переносимый NIF. Клонирование прямо внутри callback не уберёт
стоимость сериализации и удлинит этот callback. Доказательства безопасности
переноса NiStream/нативных объектов на произвольный worker не получено; detached
bytes, сжатие и файловые операции остаются у существующих workers.


## Оптимизация native Save — 08.10.2026

Повторно проверены через IDA MCP: SE `E:/Reverse/SkyrimSE.exe.i64`,
AE `E:/Reverse/SkyrimSE1170.exe.unpacked.exe.i64`, VR `E:/Reverse/SkyrimVR.exe.i64`.
Для каждой базы выполнены health и minimal survey; imagebase везде `0x140000000`.
Input VR в IDB: `D:/Programs/IDA Pro/data/SkyrimVR.exe.1415/SkyrimVR.exe`.
Порты заново определены через list_instances (в этой сессии SE13339/AE13337/VR13338).
В таблице offsets AE первая колонка — ID, вторая — **VA** (`70334 140D1F950`);
RVA ниже получены вычитанием imagebase. Таблица SE использует RVA.

| Операция/slot | SE RVA | AE RVA | VR RVA |
|---|---|---|---|
| Save(NiBinaryStream*),04 | C59FE0 (ID68981) | D1F950 (ID70334) | C9F590 |
| SaveStream,10 | C5C040 | D21C70 | CA15F0 |
| RegisterObjects,11 | C5C200 | D21FC0 | CA17B0 |
| RegisterSaveObject,09 | C5A960 | D203A0 | C9FF10 |
| NiNode::RegisterStreamables,1A | C57750 | D1CC50 | C9C9C0 |
| NiAVObject::RegisterStreamables,1A | C56930 | D1BD40 | C9B9F0 |
| NiObjectNET::RegisterStreamables,1A | C60390 | D25DB0 | CA5940 |
| NiObject::RegisterStreamables,1A | C526B0 | D17DF0 | C97770 |
| SaveHeader,0E | C5B7C0 | D214D0 | CA0D70 |
| SaveObjectSizeTable,16 | C5C700 | D224C0 | CA1CB0 |

Основание: vtable entries, decompile, disasm и вызывающая цепочка. В Save
RCX=NiStream*, RDX=заимствованный NiBinaryStream*, return bool в AL;
`oStr` по+2A0 устанавливается на время синхронного SaveStream и затем очищается.
SaveStream сам вызывает RegisterObjects, SaveBinary и backpatch таблицы размеров.
RegisterSaveObject находит уже зарегистрированный pointer в map+2B0 и возвращает
AL=0. NiObject/NET/AVObject/NiNode передают этот результат до обхода children;
нормализованный корень production — обычный NiNode. Поэтому AuditPhantom и Save
теперь используют **один NiStream**: проверка загрузчиков не удалена, второй
вызов регистрации прекращается на корне. Между audit и Save дерево не меняется.
Map, strings, objects и их refcounts освобождает прежний native destructor/Save.

Вместо SaveBuffer с временным NiMemStream вызывается штатный CommonLib `Save1`
(slot04). Узкий адаптер `PhantomOutput` остаётся в Hooks.ixx; адреса новых функций
в production не добавлены, global hooks/vtables не патчатся. Движок пишет через
WriteFn на+18 (RCX=stream, RDX=source, R8D=uint32 bytes, R9=component sizes,
пятый аргумент=count), затем сам прибавляет возвращённый EAX к uint32 position+08.
Адаптер не делает этого повторно. tell=slot03; seek=slot02 принимает int32 relative
смещение. В конце SaveObjectSizeTable курсор остаётся внутри файла: размер output
равен high-water size, а не tell(). SaveHeader передаёт set_endian_swap(false)
для текущего little-endian профиля. Неподдержанный режим/чтение/выход за размер
делают output ошибочным; даже если SaveStream вернёт true, partial asset не отдаётся.
NiMemStream get_info VR135ACE0 подтверждает pointer, три поля текущего размера,
два поля позиции; адаптер заполняет их, не раскрывая незаписанную capacity.

`NifOutput` в Client.Core владеет только байтами: ограниченная запись, seek/backpatch,
move результата в Asset. Буфер резервируется по предыдущей успешной длине с6,25%
запаса для малых изменений; это подсказка, не новый предел. Холодный рост удваивает
capacity с минимумом1024B, ограниченным существующим raw assetBytes. Финальной
копии NiMemStream→vector больше нет. Capacity учитывается существующим MemoryBytes.
Сам буфер не переиспользуется между assets: предыдущий снимок может ещё принадлежать
worker/очереди; между захватами хранится только scalar длины на игровом потоке.
Нативные сцены/NiStream не перенесены на worker.

Статус: статически проверено SE1.5.97/AE1.6.1170/VR1.4.15, комментарии сохранены
в IDB без изменения executable. Offline output tests не заменяют игровой Save/Load
и замер frame-time. Сравнение записанного NIF проверяет байты/границы/backpatch,
но не выполняет engine SaveBinary. Новое игровое ускорение пока **не измерено**.
