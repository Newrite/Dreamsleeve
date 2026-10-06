# Native NIF: план исполнения, 07.10.2026

Рабочая ветка `codex/phantom-native-nif` создана от `codex/phantom-replication`
(`3b80b8e`). Проверенный источник поведения — `codex/phantom-local-se`
(`23ae14d`). Пользовательское изменение `Plugin/client.toml` сохраняется.
Прежний план neutral реализации сохранён в истории Git.

## Следующий этап: непрерывная замена и стоимость обновлений

Основание: новые игровые записи `1791317521446`–`1791319299286`, анализ
`benchmarks/phantom-native-recordings-2026-10-07.md`. Пользователь подтвердил
визуальный replay SE; обнаружен разрыв свежих поз при смене поколения.

1. [ ] Исследовать новую запись экипировки и завершение native updates в
   CommonLib/IDA SE/AE/VR. Отделить structural changes, мелкую динамику лица,
   привязку/видимость и временные эффекты; добавить измеримые причины rebuild.
2. [ ] Исправить правила обновления модели и покрыть policy regression tests.
3. [ ] Обеспечить ограниченный переход двух поколений с живыми позами старого
   asset до готовности нового у получателя. Объединять следующие изменения;
   context/disconnect/AOI отменяют переход и освобождают ресурсы.
4. [ ] Измерить бинарные патчи полных NIF на записях. Сохранять native Load и
   непрозрачность asset на сервере; решение о внедрении принять по измерению.
5. [ ] Внедрить lossless byte-plane pose packing, синхронный protocol bump,
   generated code, lifecycle/fragmentation/real-server tests.
6. [ ] Проверить ordinary/diagnostic builds, обновить измерения/runtime docs,
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


## Следующие оптимизации, обсуждены, но не внедрены

- Audience-driven live poses: существующий AOI/Selected определяет наличие
  реального получателя. При нуле получателей останавливать отправку/кодирование
  живых поз, при появлении — свежий полный снимок. Модель и её поколение остаются
  отдельно; отдельный spatial index или actor не нужен.
- Направление камеры: movement rotation сейчас пересылается и интерполируется,
  но Fireflies применяет только position, а Phantoms использует native transforms.
  GroundMark heading читается отдельно. Поэтому можно переопределить нынешнее
  поле как camera orientation с явным переименованием/сменой контракта и не
  включать чужой camera angle в downstream. Сейчас поле всё ещё actor rotation.
  Нужен корректный accessor фактической камеры для first/third person и VR.
- Дешёвый сектор: dot(viewForward, targetPosition-observerPosition), широкий
  угол и гистерезис. Сначала приоритет/снижение частоты за камерой; кеш модели
  не выбрасывать. Один actor yaw не годится для свободной камеры третьего лица.
  Это будущая политика поверх текущих AOI, а не реализованный frustum culling.
- Death phantom: независимый ограниченный локальный ring buffer поз и нужных
  поколений NIF. При смерти заморозить запись и загрузить связанный с death mark
  clip через общий проверяемый model/pose путь. Не зависеть от наличия live
  audience; удалять модель из ring только после исчезновения ссылок кадров.
  Число моделей, длительность, память, TTL и права доступа предстоит определить.
