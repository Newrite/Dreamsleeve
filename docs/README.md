# Документация Dreamsleeve

Начать с [CurrentStateRu.md](CurrentStateRu.md): решения из обсуждения «Языки для ENet»
и состояние исходников на 26 сентября 2026 года. Документ отделяет принятые
контракты от предложений для будущих слоёв.

| Документ | Статус и назначение |
|---|---|
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
[Agent](../src/Dreamsleeve.Agent/README.ru.md).

Исторические SQL/protobuf-примеры удалены из рабочего дерева; архивный коммит `40059c5`.
Просмотр: `git show 40059c5:docs/ProtoExamples/README.md` или
`git show 40059c5:docs/SqlExamples/README_ru.md`.
