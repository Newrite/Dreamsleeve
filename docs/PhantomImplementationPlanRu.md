# Native NIF: план исполнения, 07.10.2026

Рабочая ветка `codex/phantom-native-nif` создана от `codex/phantom-replication`
(`3b80b8e`). Проверенный источник поведения — `codex/phantom-local-se`
(`23ae14d`). Пользовательское изменение `Plugin/client.toml` сохраняется.
Прежний план neutral реализации сохранён в истории Git.

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
