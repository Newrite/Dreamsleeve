# Native NIF: план исполнения, 07.10.2026

Рабочая ветка `codex/phantom-native-nif` создана от `codex/phantom-replication`
(`3b80b8e`). Проверенный источник поведения — `codex/phantom-local-se`
(`23ae14d`). Пользовательское изменение `Plugin/client.toml` сохраняется.
Прежний план neutral реализации сохранён в истории Git.

## Исправления после двухклиентской игры07.10.2026

- [x] Сопоставить оригинальные логи, server cache и архив друга без изменений входов.
- [x] Восстановить причину отсутствия публикации; добавить native skinned effect
  conversion и точный shader diagnostic. SE/AE/VR factory проверена статически.
- [x] Убрать зависимость transfer ACK от pose tick, расширить окно в рамках
  существующего контракта, проверить полные окна и cancellation через ENet.
- [x] Переиспользовать movement source-time mapping, воспроизвести скачок
  регрессионным тестом; исправить дрейф серверной сетки10Гц.
- [x] Сравнить один и тот же native asset до/после, проверить512-player steady.
- [x] Завершить diagnostic build/tests/install и зафиксировать итоговые hashes.
- [ ] Игровая проверка новой DLL обоими игроками: аксессуар shades, взаимная
  видимость, магия↔вархаммер, плавность. Требует запуска Skyrim пользователями.

Подробные данные: [отчёт](benchmarks/phantom-two-player-2026-10-07.md).

## Завершённый этап7a29738: непрерывная замена и стоимость обновлений

Основание: новые игровые записи `1791317521446`–`1791319299286`, анализ
`benchmarks/phantom-native-recordings-2026-10-07.md`. Пользователь подтвердил
визуальный replay SE; обнаружен разрыв свежих поз при смене поколения.

1. [x] Исследовать новую запись экипировки и завершение native updates в
   CommonLib/IDA SE/AE/VR. Отделить structural changes, мелкую динамику лица,
   привязку/видимость и временные эффекты; добавить измеримые причины rebuild.
2. [x] Исправить правила обновления модели и покрыть policy regression tests.
3. [x] Обеспечить ограниченный переход двух поколений с живыми позами старого
   asset до готовности нового у получателя. Объединять следующие изменения;
   context/disconnect/AOI отменяют переход и освобождают ресурсы.
4. [x] Измерить бинарные патчи полных NIF на записях. Сохранять native Load и
   непрозрачность asset на сервере; решение о внедрении принять по измерению.
5. [x] Внедрить lossless byte-plane pose packing, синхронный protocol bump,
   generated code, lifecycle/fragmentation/real-server tests.
6. [x] Проверить ordinary/diagnostic builds, обновить измерения/runtime docs,
   собрать dist и установить diagnostic при закрытой игре с backup/config preserve.

Состояния перехода принадлежат существующим владельцам: Exchange — локальные
поколения и remote данные; Source/Scene — native bindings; PhantomAgent —
подписки/готовность получателей. Не вводить вторую систему model readiness в UI.

| Решение | Подсистемы |
|---|---|
| Оставить | AOI/Presence, ENet owner и unreliable-fragment policy, chunks/ACK, content hash/cache/storage, session epoch, cancellation, privacy/UI |
| Адаптировать | manifest, detached worker budgets, независимые компактные позы, interpolation, lifecycle, диагностика и сборка |
| Заменить | neutral asset на ограниченный контейнер native NIF с каналами; reconstruction на NiStream Load; вершины на transforms нативной сцены |
| Удалить вместе с заменой | neutral vertex/skin/material/mask schema, CPU skinning, D3D readback/upload, специальные factories, старые лимиты/тесты |

## Доменные границы

Публикация — immutable содержание в поколении модели и контексте источника.
Подготовка следующего поколения не отзывает пригодное текущее. Успешное
завершение заменяет текущее только при совпадении session epoch/request/generation;
смена пространства или отзыв публикации прекращают оба состояния.
Источник отсутствующего 3D временно ожидает. Трансформ, видимость и привязка
оружия не меняют внешность; состав дерева и реальная геометрическая деформация
помечают ревизию для объединённого обновления.

Server.Domain задаёт публикацию/доступность и переходы; PhantomAgent владеет
подписками и текущей/подготавливаемой публикацией. Client.Core владеет detached
asset, кодеком, передачами и историей поз. Game владеет native source bindings
и сценой только на игровом потоке. Hooks содержит ABI/адреса и сообщает
завершённые изменения; Host/UI читают состояние через существующие границы.
NIF проверяется до native loader; сервер его не распаковывает. Проверка
структуры asset не заменяет проверки актуальности у владельца сессии.

## Последовательность и критерии

1. [x] Аудит документов, прототипа, входных архивов; типы/инварианты.
2. [x] Native clone/normalization, ограниченный NIF, общий codec/load/replay.
3. [x] Компактные каналы, живые bounds, revisions внешности, runtime hooks.
4. [x] Сервер/транспорт/storage, protocol bump и удаление neutral формата.
5. [x] Настройки, production diagnostics, окончательная очистка.
6. [x] Штатный/diagnostic build, tests, real server, 512/group25 benchmark,
   документация и полный dist с сохранёнными пользовательскими конфигами.

После каждой законченной части — самопроверка или одно ограниченное ревью.
Сборка/статический ABI audit не являются игровым подтверждением SE/AE/VR.
Исходные архивы не изменяются; результаты/fixtures идут в ignored build.

### Наблюдения аудита

- Реальный prototype NIF содержит внешние texture paths, NiPointLight и effect
  controllers. Сам успешный local roundtrip не даёт переносимого сетевого asset.
- Старый server Publish вызывает clearSource до готовности replacement;
  переход должен сохранять текущую публикацию до успешного settle.


## Самопроверка

Удалены отдельные geometry/material/mask codecs, CPU skinning и D3D adapters.
Native capture и replay используют одну Scene; server review проверил сохранение
пригодной публикации до commit следующего поколения. При аудите clone исправлена
проверка совместного владения shader до удаления auxiliary geometry.
Dodge/GhostTrail.cpp сверён: native lighting material, уникальный SetMaterial,
engine allocator для emissiveColor, additive blend и ZBufferWrite соответствуют
прототипу. Удаление авторских texture dependencies до Save требует игрового QA.

Игровой QA не закрыт: Windows10 UI adapter возвращает out-of-range HWND для MO2.
Статический ABI аудит и codec/UDP тесты не заменяют визуальную проверку.

Финальная самопроверка: неизвестные non-node классы не считаются auxiliary
автоматически; обязательная геометрия должна иметь clone pair. Capture и Scene
получают одну таблицу native операций. Ошибка native Load блокирует повтор той
же view/generation, а не вызывает повторную тяжёлую загрузку каждый кадр.

Ограниченное независимое native ревью выявило и закрыло два пропуска: source
topology проверяется до Clone (включая не попавшие в clone неизвестные классы);
потеря skinInstance при Clone теперь отказ, а не успешный unskinned asset.
Дополнительных конкретных lifetime/repeated Load дефектов ревью не выявило;
это статическая проверка, не игровой результат.

- [ ] Игровая приёмка новой DLL SE/AE/VR и сравнение внешности между модпаками.
  Автоматические проверки и полный dist готовы; Windows10 UI adapter блокирует
  автоматизированный игровой прогон. Подробности в PhantomReleaseValidationRu.md.

## Улучшения после игровых записей07.10.2026

- [x] Проанализировать7новых записей; разделить pose drift, FaceGen deformation
  и настоящую смену состава (Warhammer / magic GlowMesh).
- [x] Заменить quantized hash на порог относительно принятого asset и coalescing.
- [x] Проверить completion callsites SE/AE/VR, поставить узкий deferred audit hook.
- [x] Внедрить lossless byte planes в production pose3; wire23 одновременно.
- [x] Два ограниченных поколения и атомарная пара поз до Displayed/Settled;
  retry, устаревшие view/sequence, withdrawal и память. Ограниченное server review.
- [x] Diagnostic replay всех 7 записей, full builds/tests, одинаковый 512/group25 run.
- [x] Обновить dist и диагностическую установку при закрытой игре, сохранить configs.
- [ ] Игровая проверка именно новой DLL/hook/перехода двумя игроками.

Copy/XOR дельта NIF пока только офлайн измерение с побайтовой реконструкцией.
Её нельзя считать внедрённой сетевой функцией. Текущий production всё ещё
передаёт полный compressed NIF; сортировка/новый renderer не добавлялись.


## Оставшиеся оптимизации

- Production NIF delta: определить точную идентичность compressed результата,
  доступность базы, ограничение цепочек, бюджет реконструкции и fallback full asset.
- Death phantom: независимый ограниченный локальный ring buffer поз и нужных
  поколений NIF. При смерти заморозить запись и загрузить связанный с death mark
  clip через общий model/pose путь. Политика памяти/TTL/прав — будущая функция.

## Следующая законченная часть: камера и demand (2026-10-07)

Внедрены protocol24 CameraDirection, сектор в PhantomPolicy/PhantomAgent,
надёжный PoseDemand и остановка live sample/encode/send без зрителей.
Модели публикуются независимо, диагностика продолжает записывать.
Добавлены тесты сектора/near zone/hysteresis, движения камеры через реальный UDP,
контекста и перестановки bootstrap между Control/Models, late worker результата.
Ограниченное ревью обнаружило потерю initial demand; исправлено хранением команды
будущего контекста в Exchange. NIF-дельты — следующая отдельная часть: не выдавать
офлайн recipe за готовый wire-контракт. Death phantoms в объём не входят.

## Исследование перед NIF-дельтами (07.10.2026)

По новой просьбе пользователя следующий этап NIF-дельт отложен до проверки
интерполяции и задержек. [Исследование и численные результаты](PhantomOptimizationResearchRu.md).
Аудит3e3b29f выявил общую model/pose FIFO одного peer в TransportOwner и
последовательное обслуживание тяжёлых assets и срочных poses одним Client.Core Worker.
Новая offline проверка7записей показывает потери движения при20→10Гц, которые
одна замена NLERP наSLERP не устраняет. Production этой частью не менялся.

- [x] Первичные источники, аудит кода, held-out сравнение2107кадров и timing simulation.
- [x] Повторный разбор512-player queue metrics;42focused C++ testsPASS.
- [x] Изоляция poses от model handoff/worker с тестами одного и нескольких источников.
- [x] Монотонный playhead, адаптивный buffer, ограниченная экстраполяция и loss tests.
- [x] Раздельные native timings и синтетическая проверка таймера10/20Гц.
- [ ] Игровые frame-time traces и визуальная оценка политики10/20Гц.
- [ ] После этих проверок — production NIF delta с определённой базой/target identity.

## Исправление очередей и алгоритмов после исследования

- [x] Отделить Poses от reliable Models в TransportOwner; заменить устаревший
  полный slot без голодания источника и без расхода reliable reserve.
- [x] Разделить detached model и pose работу клиента; сохранить G1 poses при
  подготовке G2; epoch/revision и prepared assets принадлежат Exchange.
- [x] Общий адаптивный playout для movement/pose, root-only extrapolation.
- [x] Повторно проверить native completion SE и добавить раздельные stage timings.
- [x] Закончить повторные suites/ENet/replays/512 benchmark и dist этой части.
  [Результаты и оставшиеся ограничения](benchmarks/phantom-algorithms-2026-10-07.md).
- [ ] Пользовательский игровой QA новой сборки: плавность/оружие/камера и frame times.
- [ ] NIF byte deltas: отдельный последующий этап после stage measurements.


## Приоритетные исправления доставки — 08.10.2026

- [x] Ограничить burst и flight по прикладным ACK; исключить голодание bulk-передачи.
- [x] Связать capture с авторитетным пространством movement и инвалидировать старый token.
- [x] Продлевать мост поколения по реальному download progress; late Offer и load grace.
- [x] Проверить через настоящий сервер bounded link, потерю фрагментов, кеш и lifecycle.
- [ ] Дальнейшая настройка скорости: clean RTT100 стал медленнее; потери ещё дороги.
- [ ] Игровая проверка переходов/долгой замены модели после этих изменений.

[Измерения и ограничения](benchmarks/phantom-delivery-2026-10-08.md).


## Скорость передачи после повторной проверки стенда — 08.10.2026

- [x] Удалить cap 32 KiB: сохранять earned credit между обслуживаниями,
  ограничивая накопление согласованным flight; проверить 5 MiB/s при 10/16 мс.
- [x] Точные монотонные часы server runtime и relay; явно фиксировать лимит
  smoke-сервера и IPv4/UDP overhead.
- [x] Быстрый старт и оценка excess queued bytes в ModelFlow; не подавлять
  минимальное окно из-за абсолютного порога задержки; duplicate-ACK guard сохранён.
- [x] Будить существующий ENet owner по chunk deadline вместо полного ожидания
  10 мс при готовой передаче. Второго owner или сетевого потока нет.
- [ ] Production NIF delta: Zstd prefix + LDM дал точную реконструкцию и
  190568 bytes вместо 13194472 на одной реальной паре. До сетевой интеграции нужны
  явная база/target identity, bounded worker reconstruction, проверка canonical
  compression между C++/сервером, pinning базы и full fallback.

[Проверки и ограничения](benchmarks/phantom-pacing-2026-10-08.md).
