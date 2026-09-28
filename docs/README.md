# Документация Dreamsleeve

Начать с [CurrentStateRu.md](CurrentStateRu.md): решения из обсуждения «Языки для ENet»
и состояние исходников на 28 сентября 2026 года. Документ отделяет принятые
контракты от предложений для будущих слоёв.

| Документ | Статус и назначение |
|---|---|
| [План разделения SessionRegistry](SessionArchitecturePlanRu.md) | Исходный план на основе `f4eef57`; ниже зафиксированы реализованные имена и изменения политики |
| [ProductSpecRu.MD](ProductSpecRu.MD) | Текущий MVP и более широкое продуктовое видение |
| [DomainSpecRu.MD](DomainSpecRu.MD) | Имена, пространство, показания, чат и будущие социальные правила |
| [TechnicalHandbookRu.MD](TechnicalHandbookRu.MD) | Архитектура и справочные примеры стека |
| [Прикладной протокол](../Protocol/README.ru.md) | Реализованные контракты сессии/чата и C++/F# codec |
| [MSVC и protobuf в модулях](MsvcProtobufModulesRu.md) | Повторная проверка C1001 на VS 18.10.2 / cl 19.51.36260 |
| [ProtobufHandbookRu.MD](ProtobufHandbookRu.MD) | Справочник работы с protobuf |
| [SqlHandbookRu.md](SqlHandbookRu.md) | Справочник предложенного SQLite-стека, не работающая persistence-подсистема |
| [FSAgentReadme.MD](FSAgentReadme.MD) | Указатель на поддерживаемую документацию агентов |
| [Answers/PeerInfo.MD](Answers/PeerInfo.MD) | Справочные заметки об ENet Peer |
| [Тесты](../tests/README.md) | Состав, происхождение и запуск |

Контракты реализованных модулей находятся рядом с кодом:
[Client State](../src/Dreamsleeve.Client.Core/State/README.ru.md),
[Server Domain](../src/Dreamsleeve.Server.Domain/README.ru.md),
[Agent](../src/Dreamsleeve.Agent/README.ru.md),
[Server.Core](../src/Dreamsleeve.Server.Core/README.ru.md).

Исторические SQL/protobuf-примеры удалены из рабочего дерева; архивный коммит `40059c5`.
Просмотр: `git show 40059c5:docs/ProtoExamples/README.md` или
`git show 40059c5:docs/SqlExamples/README_ru.md`.

- [Измерения после разделения сессий](benchmarks/session-routing-2026-09-27.md): воспроизводимое сравнение и ограничения выводов.
- [Нагрузочные прогоны через настоящий ENet](benchmarks/enet-2026-09-27.md): 100/500/1000 клиентов, CPU, память, задержки и полная проверка доставки.

- [Авторизация, SQLite, логирование и NuGet-стек](AuthenticationRu.md).

- [Телеметрия игрока и PlayerDetails](PlayerTelemetryPlanRu.md): реализованные данные игрока, владельцы и границы дальнейшей игровой интеграции.

- [Движение через ENet и интерполяция](benchmarks/movement-2026-09-27.md): 100/500/1000 клиентов, AOI, перегрузка и стоимость C++-потребителя.

- [Пространственный индекс и периодическая репликация v6](SpatialReplicationRu.md).

- [Повторная матрица движения v5](benchmarks/movement-v5-2026-09-27.md): эффект индекса и batching, ограничения плотной рассылки.

- [Диагностика движения: таймер, UDP, ENet и GC](benchmarks/movement-diagnostics-2026-09-27.md): частоты, буферные эксперименты и EventPipe-профили.

- [План первой волны оптимизации движения](MovementOptimizationPlanRu.md): конкретные hot paths, Task/ValueTask, struct/DU, reuse и порядок проверок; первая волна реализована; результаты повторных бенчмарков ниже.

- [Первая волна оптимизации: повторные ENet-бенчмарки](benchmarks/movement-opt-wave1-2026-09-27.md): группы по 25 приблизились к 10 Гц, плотная рассылка регрессировала; матрица и контрольные повторы.

- [Расследование регрессии движения](benchmarks/movement-regression-2026-09-27.md): разделены расходы MTU-разбиения и ограничения общего клиентского сокета; исправленные defaults и контрольные прогоны.

- [1000 клиентов при 20 Гц: независимые нагрузчики](benchmarks/movement-workers-2026-09-27.md): сравнение 1/4/10 процессов при одинаковом числе сокетов, проверки UDP-буферов и ограничения интерпретации.



- [План рефакторинга каналов, движения и владельца ENet](EnetMovementRefactorPlanRu.md): три канала, периодические позиции без подтверждений, reliable-границы и последовательная проверка транспорта.

- [Movement v6: каналы и production ENet owner](benchmarks/movement-v6-owner-2026-09-27.md): 1000 клиентов, 20 Гц, AOI по 25/100, сравнение inline/owner и отдельный прогрев видимости.

- [SKSE-клиент](SkseClientRu.md): модули DLL, сообщения SKSE, потоки, хук Main::Update, PrismaUI host, телеметрия, светлячки, конфигурация и dist.

- [Смерть и actor values](DeathAndActorValuesRu.md): исследованные события/хуки, покрытие и выбранный sampling.
