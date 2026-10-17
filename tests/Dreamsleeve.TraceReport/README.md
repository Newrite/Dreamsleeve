# Offline trace reports

`dotnet run --project tests/Dreamsleeve.TraceReport -c Release -- input.nettrace output.json`
разбирает EventPipe GC/аллокации. Проект не подключён к серверу или нагрузчику.

UDP ETW-файлы Windows можно разобрать через `--udp input.etl output.json`.
Результат включает `EventsLost`, события TCPIP/AFD, расшифрованные причины потерь,
агрегаты по endpoint/контексту процесса/адресу/размеру и ограниченные примеры.
Нужно сопоставлять их с портами и PID конкретного теста: PID заголовка ETW может
быть контекстом отправителя, а не владельцем принимающего сокета.


`--alloc-stacks input.nettrace output.json` разбирает GCAllocationTick через
TraceEvent/ETLX с управляемыми стеками. Отчёт содержит EventsLost и число событий
без стека. Estimated bytes относят интервал сэмплирования к вызвавшей событие
аллокации, это не точный счётчик байтов типа. Общий объём проверяется отдельно
через GC.GetTotalAllocatedBytes. Конвертер пишет производный .etlx рядом с входом;
трассы и производные файлы не включаются в dist.
