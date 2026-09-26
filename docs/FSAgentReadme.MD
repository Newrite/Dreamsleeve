# Dreamsleeve.Agent

Поддерживаемая документация находится рядом с исходником:

- [Русский README](../src/Dreamsleeve.Agent/README.ru.md).
- [English README](../src/Dreamsleeve.Agent/README.md).
- [Интерактивное руководство RU/EN](../src/Dreamsleeve.Agent/docs/index.html).
- [Примеры](../src/Dreamsleeve.Agent/examples/Program.fs).
- [Тесты и запуск](../tests/README.md).

F#/.NET 10+, Channels и Task. Agent сериализует сообщения, StatefulAgent —
переходы неизменяемого состояния, MutableStatefulAgent — доступ к изменяемому
состоянию. Выделенный поток на агент не создаётся. Возвращаемые снимки не должны
давать внешнему коду изменять внутренние коллекции.

Complete прекращает приём и дренирует очередь; Abort запрашивает кооперативную
отмену. Request/reply, потери в bounded mailbox и завершение проверяются набором
Dreamsleeve.Agent в общем Expecto-проекте. Библиотека агентов не зависит от Expecto.
