# Зависание после загрузки — 08.10.2026

Процесс SkyrimSE 22928, запущен 22:08:04. Сняты контексты и затем настоящие
стеки через Windows DbgHelp StackWalk64; потоки возобновлялись в finally.
Первый raw scan не был стеком вызовов; выводы основаны на двух unwind-снимках.

Главный поток 31724: NtWaitForSingleObject → WaitForSingleObjectEx → Skyrim
5765FF → WorkerSpinLockFix+55FD0 → Skyrim5B35DD → HDT MainHooks Update →
Dreamsleeve Hooks::MainUpdate::Update+35 → DynamicSettings → игровой цикл.
Dreamsleeve здесь ожидает возврата UpdateOriginal; Logic::OnFrame ещё не вызван.
IDA SE подтверждает бесконечное ожидание события в функции5765D0.

Рабочий поток34640: FittingRoom+CAA70 → SKSE+1812F → Skyrim5B44E1 →
Skyrim640E67 → task worker. В другом снимке: SKSE+1818F → FittingRoom+21CD9
→ FittingRoom+CAAE5 → SKSE+1812F. Он продолжает расходовать CPU.

Исследована установленная DLL из мода Fitting Room - ESO Style Transmog.
Её callback CA950 вызывает PlayerCharacter::IsBlocking (SE608AF0) через
обёртку326C20; затем проверяет дополнительный флаг актёра. В ветви повторения
CAAC2 явно загружает адрес самого CA950, CAAE0 передаёт его постановщику задач.
Та же ветвь используется для ожидания временной задержки. Очередь не опустошается,
главный поток ждёт её, а обновление состояния персонажа не продвигается.
Это установленный механизм текущего зависания. Первоначальная причина, почему
проверяемое состояние удерживается на этой загрузке, не установлена.

В снятых стеках нет PhantomClone/NiStream/захвата модели. Это не абсолютное
доказательство отсутствия взаимодействия модов; следующий изолирующий тест —
та же загрузка с отключённым только Fitting Room, сохранив остальные версии.
Ни DLL игры, ни память процесса, ни конфиги/список модов не изменялись.
Локальные артефакты: build/phantom-hang/{threads.json,unwound.json,second-sample.txt}.
