# Актуальные изменения07.10.2026

Текущая карта владельцев и инвариантов — [PhantomsRu.md](PhantomsRu.md).
Из старой реализации переиспользованы Exchange/Worker/Streaming, AOI, request
correlation, epoch и ACK pacing, UI/privacy и lane delivery policy.
Удалены PhantomAsset/Graphics/Mesh/VertexStream/Recovery, Masks и neutral
geometry/skin/material schema. Нативные Capture/Scene больше не обновляют GPU
буферы самостоятельно. Server больше не имеет geometry cap или поля manifest.

Исправлено: pending Publish сохраняет Ready/Latest; replacement Offer не
уничтожает текущую сцену; watermark поколения/sequence живёт независимо от истекающего Latest;
one publisher с ready+pending учитывается один раз в MaxSources.

В23 Exchange владеет текущим/удерживаемым поколениями и разрешением следующей
публикации; Game держит нативные Source/Scene в соответствии с этим решением.
Worker сохраняет immutable PreparedAsset; Streaming владеет transfers и квитанцией
последнего server commit, чтобы rollback не переотправлял старый manifest.
Server actor владеет Ready, high-watermark manifest, подтверждениями views и
ограниченным переходом. Обход подтверждений использует существующий Audiences,
без сканирования всех игроков на каждого источника и без второго spatial index.
Готовность UI по-прежнему выводится из Exchange/Scene, отдельного lifecycle нет.
Preparation failure до Publish восстанавливает предыдущую публикацию; ошибка
нового native Apply/Attach не уничтожает пригодную текущую сцену. Terminal
upload rejection не блокирует будущие изменения: старый bridge продолжает
самостоятельные позы, сервер удерживает оба sequence floors. PublicationPhase
остаётся в Exchange; transport receipt не является отдельной готовностью UI.
Game выделяет монотонные поколения независимо от отката Source.

Проблема вне игрового объёма: Sandbox изолированного процесса не даёт тесту Credential Manager
сохранить временный credential; требуется обычный разрешённый запуск теста.
Managed FS3511 в старом task-тесте Presence исправлен минимальным переносом
рекурсивного helper в обычную функцию; поведение теста сохранено.

Записи ниже — история codex/phantom-replication. P-06 geometry cap и P-07
neutral factories больше не описывают актуальную архитектуру.

---

# Фантомы: наблюдения о существующем коде

Журнал сессии полной реализации, ветка `codex/phantom-replication`.
План: [PhantomImplementationPlanRu.md](PhantomImplementationPlanRu.md).
Здесь фиксируются конкретные наблюдения, включая старые участки проекта;
документ не означает, что все они входят в объём текущей фичи.

Для новой записи указывать: место/исходный commit, доказательство, семантику,
связь с задачей, действие и статус. Исправленные записи сохранять с hash
и релевантной проверкой. Непроверенные предположения отмечать как гипотезы.

## P-01. Описание версии протокола отстаёт от кодеков

- База: `7772d17`, `Protocol/README.ru.md`,
  `src/Dreamsleeve.Server.Core/Protocol/ProtocolCodec.fs`.
- Наблюдение: заголовок README указывает 15, `ProtocolCodec.Version` — 20.
  Документ ветки эксперимента уже содержит историю до 20.
- Связь: прямая; новый wire contract должен опираться на фактическую версию.
- Действие: синхронизировать описание с обеими константами; после добавления
  контракта фантомов поднять версию на обеих сторонах без legacy-путей.
- Статус: исправлено в реализации; оба codec используют 21, README описывает
  пять lanes и контракт фантомов. Старых wire-путей нет.

## P-02. Reliability передаётся через bool, а bulk/pose lanes ещё отсутствуют

- База: `7772d17`,
  `src/Dreamsleeve.Server.Infrastructure.Interop/OutgoingPackets.cs`,
  `src/Dreamsleeve.Server.Infrastructure/EnetTransport.fs`,
  `src/Dreamsleeve.Server.Infrastructure/TransportOwner.fs`, `Protocol/network.proto`.
- Наблюдение: `TrySend(..., byte channel, bool reliable)` выбирает только
  Reliable/default; адаптер отличает Realtime от остальных lanes. Это
  корректно для существующих трёх каналов, но недостаточно для отдельного
  reliable model stream и большого unreliable fragmented pose packet.
- Связь: прямая; новые lanes должны иметь единую delivery policy и budgets.
- Действие: расширить существующую типизированную транспортную границу и
  централизовать lane/flags policy. Не копировать `lane <> Realtime` в новых
  местах и не создавать второй ENet owner ради streaming. Пример generics
  вводить только если найден общий алгоритм, а не для замены одного bool.
- Проверка: реальные fragment/loss/wrap тесты, native leases и headroom
  control/chat; старый путь остаётся корректным.
- Статус: реализованы типизированная delivery policy, Models/Poses и реальная
  проверка ENet fragment loss/FFFF rollover. Reliable epoch marker пустой;
  payload позы остаётся unreliable. Это ограничение расширения, не старый баг.

## P-03. Пространственное представление не учитывало личный игнор

- База: `7772d17`, `Game/Fireflies.ixx`: проверялись self и guildmates-only,
  тогда как чат и метки использовали `Names::Hides`.
- Связь: прямая; фантом и светлячок должны отвечать на тот же вопрос видимости.
- Действие: `Session::HidesPlayerRepresentation` объединяет self/ignore/guild
  и используется обоими представлениями. Отдельная политика имён не создана.
- Проверка: сценарий Host session с ignored/self/другим игроком; обычная
  presence остаётся в online list. Статус: исправлено.

## P-04. Ограничения и races новой передачи, найденные ревью

- Проверка Client.Core: request correlation при новом context с той же
  generation, retry прерванного download, девятый cache miss, off/on во время
  Prepare, RAM shrink и общий upload/download admission limit.
- Действие: positive RequestId на обеих сторонах, explicit transfer states,
  publication revision token, bounded miss queue и timeout cache probe,
  общий счётчик передач и invalidation локального источника после Prepare
  failure. Ready remote теперь получается из закрытого content variant.
- Проверка: отдельные regression tests, полный native suite. Статус:
  исправлено; повторная проверка фиксированных commit завершена, результаты
  сохранены в [проверке выпуска](PhantomReleaseValidationRu.md).

## P-05. Полные Presence observations при выключенных фантомах

- Измерение первоначального AFTER на dense128 показало лишнюю сборку views,
  хотя `Phantoms.Enabled=false`. В плотной AOI это до 16384 записей за тик.
- Действие: тип `PhantomObservationMode` разделяет Disabled/Membership/Full;
  выключенная фича получает только membership для bootstrap. Нет второго
  spatial index или отдельной копии правил AOI.
- Статус: исправлено в `06f7420`; server suite 601/601. Первоначальные
  измерения сохранены, отдельный повтор фиксирует результат оптимизации.

## P-06. Учёт памяти, RAM-cache metadata и ACK pacing

- Fixed-commit review `3ca0808` воспроизвёл under-reservation декодированных
  поз, обход raw/count проверки на RAM hit по одному hash и timeout окна
  при восьми передачах с 64 KiB/s.
- Действие: единый `SnapshotWorkingBytes` и `BufferedPoseCount`, debit всех
  одновременно удерживаемых histories/scratch; `Descriptor.SameContent`
  проверяет manifest на RAM reuse; ACK выдаёт доступный целый prefix chunks.
- Дополнительно Scene учитывает удерживаемый старый Asset; geometry cap 512
  допускает измеренный аватар с 267 mesh, а превышение cap отклоняется.
- Статус: исправлено в `0c67ccf`, native 364/364. Исторический review report
  сохраняет исходные ошибки; final scoped review не выявил блокеров.

## P-07. Native factory и lifecycle сцены

- IDA audit SE/AE/VR выявил неподтверждённую stream registration текстуры.
  Hooks использует проверенный typed `NiSourceTexture` helper; Ops не знают
  адресов. Source cache count caps выведены из geometry limit.
- Clear/Busy могут оставить слот с разрушенной или непозированной сценой.
  `ForgetCleared` снимает такой слот и его reservation; combat не обходится
  без проверки timeout. Это нужно для восстановления и корректного fallback.
- Статус: исправлено; статические runtime доказательства записаны отдельно
  в `PhantomRuntimeRu.md`, а новый сетевой renderer ещё требует игрового теста.

## P-08. Старый View после departure связывался с новым source session

- Fixed-commit review `06f7420` воспроизвёл подписки 1→0→1 после запоздалого
  View и reconnect того же PlayerId вне AOI, без новой разрешённой view.
- Действие: Presence fact несёт source connection epoch; PhantomAgent
  проверяет текущую membership и сохраняет authority floor при detach.
  Второй AOI policy или дополнительная история не появились.
- Статус: исправлено в `d1b4dea`; regression проверяет отсутствие offer,
  pose и download IO до свежего View. Server 602/602 и fresh native UDP 1/1
  прошли; повторное ревью зафиксированного commit без блокеров.

## Замечания вне текущего объёма

На Windows тесты Playwright завершались успешно, но процесс очистки
двух Vite webServer оставался запущен. Это воспроизвелось и до изменения
default памяти; освобождение строго идентифицированных дочерних процессов
на портах 5178/5179 позволяло runner завершиться с кодом 0. Статус:
отдельная задача для harness shutdown в `Client.UI/playwright.config.ts`,
не ошибка игровой функции. Чужие dev servers на 5180/5181 не останавливались.

При повторной Release-компиляции серверных тестов 06.10.2026 обнаружен FS3511
в существующем `PresenceAgentTests.fs:206`: рекурсивная локальная функция в
`task` включает динамический state machine, а warnings-as-errors останавливает
тестовую сборку. С `-p:NoWarn=3511` весь набор 602/602 проходит. Отдельная задача:
вынести функцию из resumable expression; production-сборка этого файла не содержит.
