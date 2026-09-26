# Справочник ENet Peer

> Исторические заметки о native ENet API. Текущие обёртки находятся в
> [DreamNet](../../src/Dreamsleeve.Client.Core/DreamNet/DreamNetPeer.ixx).
> DreamNetClient владеет одним peer, а прикладная сессия будет отдельным слоем.
> Примеры ниже не являются планом добавления каждого ENet метода в обёртку.

Да, тут очень важно разделить **public API ENet**, которым стоит пользоваться в приложении, и **internal API/внутренние структуры**, которые лучше не трогать из своей обертки.

## Самое главное правило

Для `DreamNetPeer` я бы считал **публичными и нормальными для обертки** только вот эти функции:

* `enet_peer_send`
* `enet_peer_receive`
* `enet_peer_ping`
* `enet_peer_ping_interval`
* `enet_peer_timeout`
* `enet_peer_reset`
* `enet_peer_disconnect`
* `enet_peer_disconnect_now`
* `enet_peer_disconnect_later`
* `enet_peer_throttle_configure` ([GitHub][1])

А вот это я бы относил к **внутреннему ENet-механизму** и не тащил в ваш public wrapper:

* `enet_peer_dispatch_incoming_reliable_commands`
* `enet_peer_dispatch_incoming_unreliable_commands`
* `enet_peer_on_disconnect`
* `enet_peer_on_connect`
* `enet_peer_reset_queues`
* `enet_peer_setup_outgoing_command`
* `enet_peer_has_outgoing_commands`
* `enet_peer_queue_acknowledgement`
* `enet_peer_queue_incoming_command`
* `enet_peer_queue_outgoing_command`
* `enet_peer_throttle`
  Причина простая: эти функции работают уже на уровне внутренних `ENetProtocol`, `ENetIncomingCommand`, `ENetOutgoingCommand`, внутренних очередей и ack-команд — то есть это не тот слой, который обычно должен видеть прикладной код. Это видно и по самим сигнатурам, и по тому, что рядом в `enet.h` живут внутренние структуры команд/каналов/протокола. ([GitHub][1])

---

## Что такое `ENetPeer` по смыслу

`ENetPeer` — это **дескриптор удаленной стороны**, через который host:

* отправляет пакеты,
* получает пакеты,
* следит за состоянием соединения,
* считает RTT, packet loss, throttle, timeouts.

В `enet.h` он прямо описан как peer, “к которому данные могут быть отправлены или от которого могут быть получены”, и ENet отдельно предупреждает: поля структуры **не нужно менять вручную**, кроме специально оговоренных случаев. Из полезных исключений — `peer->data`, которое прямо описано как application private data и может свободно изменяться приложением. ([GitHub][1])

Очень важно: `ENetPeer` — это **не owner**, а handle. У `ENetHost` внутри есть массив `peers`, и peer’ы живут в рамках host’а. Поэтому C++-обертку над ним надо проектировать как **non-owning wrapper над `ENetPeer*`**, а не как RAII-owner. ([GitHub][1])

---

## Какие поля peer’а реально важны

Я бы мысленно делил их на четыре группы.

### 1. Идентичность и связь

* `host`
* `address`
* `state`
* `connectID`
* `incomingPeerID`, `outgoingPeerID`
* `incomingSessionID`, `outgoingSessionID`
* `data`

Это то, что нужно для:

* понимания, с кем связан peer,
* в каком он состоянии,
* какой у него адрес,
* и как к нему прикрепить application data. ([GitHub][1])

### 2. Каналы

* `channels`
* `channelCount`

Для обертки это важно в основном через `channelCount`.
Сам `ENetChannel` я бы наружу **не вытаскивал**, потому что это уже внутренняя структура секвенсирования и очередей. Для приложения обычно достаточно channel ID как `uint8_t`. ([GitHub][1])

### 3. Метрики качества соединения

* `roundTripTime`
* `roundTripTimeVariance`
* `packetLoss`
* `packetLossVariance`
* `mtu`
* `windowSize`
* `packetsSent`
* `packetsLost`
* `incomingDataTotal`
* `outgoingDataTotal`

Это очень полезно для диагностики, телеметрии и отладки transport-поведения. Документация в заголовке прямо комментирует, например, `roundTripTime` как среднее RTT в миллисекундах, а `packetLoss` — как среднюю потерю reliable-пакетов. ([GitHub][1])

### 4. Параметры адаптации и таймаутов

* `packetThrottle`
* `packetThrottleLimit`
* `packetThrottleAcceleration`
* `packetThrottleDeceleration`
* `packetThrottleInterval`
* `pingInterval`
* `timeoutLimit`
* `timeoutMinimum`
* `timeoutMaximum`

Это уже operational knobs, которыми управляют `enet_peer_throttle_configure`, `enet_peer_ping_interval` и `enet_peer_timeout`. ([GitHub][1])

---

## Состояния peer’а

ENet перечисляет состояния peer’а явно:

* `DISCONNECTED`
* `CONNECTING`
* `ACKNOWLEDGING_CONNECT`
* `CONNECTION_PENDING`
* `CONNECTION_SUCCEEDED`
* `CONNECTED`
* `DISCONNECT_LATER`
* `DISCONNECTING`
* `ACKNOWLEDGING_DISCONNECT`
* `ZOMBIE` ([GitHub][1])

Для обертки я бы:

* сохранил доступ к сырому enum,
* и сверху добавил helper’ы:

    * `IsConnected()`
    * `IsConnecting()`
    * `IsDisconnected()`
    * `IsDisconnecting()`

Потому что промежуточные состояния реально существуют и иногда важны для диагностики, а не только “connected / not connected”.

---

# Как пользоваться основными peer API

## `enet_peer_send`

Это главный send API.

Он:

* принимает `ENetPeer*`,
* `channelID`,
* `ENetPacket*`,
* ставит пакет в очередь отправки.

Важно понимать: **фактическая отправка происходит не в момент вызова `enet_peer_send`, а во время работы host/service/flush**. Это подтверждается и обертками в других экосистемах: например, Rust-обертка прямо пишет, что actual sending happens during `Host::service`. ([GitHub][1])

Практически это означает:

* `Send()` в вашей обертке — это **queue/send intent**, а не “байты уже ушли в сеть”;
* после send нужен `enet_host_service(...)` или `enet_host_flush(...)` на уровне host.

### Что я бы делал в обертке

`DreamNetPeer::Send(...)`:

* либо принимает `DreamNetPacket&&`,
* либо принимает `std::span<const std::byte>` + flags и внутри создает packet.

Но packet-модель лучше продумать заранее, потому что peer send на нее естественно опирается.

---

## `enet_peer_receive`

Это low-level receive API на уровне peer.

Он пытается вернуть входящий `ENetPacket*` и канал через `channelID*`.
Но в обычном коде ENet чаще работают через **event loop** (`enet_host_service` + `ENetEvent`), а не через прямой peer receive. Официальный tutorial ENet вообще строится вокруг event-driven модели, где connect/disconnect/receive приходят как события. ([GitHub][1])

### Практический совет

В вашей low-level обертке я бы:

* либо вообще не делал `Peer::Receive()` как основной API,
* либо оставил его как thin advanced method.

Основной receive path для проекта я бы строил через `DreamNetEvent`.

---

## `enet_peer_disconnect`

Это **graceful disconnect**.

По tutorial ENet:

* disconnect request отправляется другой стороне,
* ENet ждет acknowledgement,
* при успехе потом приходит `ENET_EVENT_TYPE_DISCONNECT`,
* если ack не приходит вовремя, peer eventually forcefully disconnects. ([enet.bespin.org][2])

Это и должен быть ваш “обычный disconnect”.

---

## `enet_peer_disconnect_now`

Это **жесткий немедленный disconnect**.

По описаниям ENet-оберток поверх peer:

* disconnect event не гарантируется,
* удаленная сторона может вообще не получить уведомление,
* peer сбрасывается сразу. ([enet.bespin.org][3])

Это не everyday API. Это аварийный или force-close путь.

---

## `enet_peer_disconnect_later`

Это disconnect **после отправки уже queued outgoing packets**.

Это полезно, если хочешь:

* не принимать новые отправки,
* но дать очереди допроиграться,
* и потом закрыть peer.

Такой вариант особенно хорош для корректного shutdown без обрыва последних пакетов. Это прямо так и описывают и ENet-обертки, и сторонние binding docs. ([Docs.rs][4])

---

## `enet_peer_ping`

Это ручной ping.
ENet и так пингует peers автоматически, но этот вызов позволяет:

* чаще проверять liveness,
* быстрее обновить RTT.
  Сторонние peer docs на ENet это прямо отмечают: ENet automatically pings all connected peers at regular intervals, но можно вызвать ping вручную. ([enet.bespin.org][3])

---

## `enet_peer_ping_interval`

Позволяет настроить интервал ping в миллисекундах.
По описанию peer wrappers:

* ping нужен и для liveness,
* и для того, чтобы throttle разумно адаптировался даже в периоды низкого трафика;
* default interval — `500 ms`, что совпадает с константой `ENET_PEER_PING_INTERVAL = 500` в заголовке ENet. ([GitHub][1])

---

## `enet_peer_timeout`

Это очень важный API.

Он задает timeout-параметры peer’а для reliable traffic.
Сторонние peer docs, основанные на ENet, довольно хорошо формулируют семантику:

* `timeout` — фактор, который масштабирует лимит на основе среднего RTT;
* когда лимит достигается, timeout удваивается;
* disconnect происходит, если лимит дорастает до `timeout_min`;
* `timeout_max` — жесткий верхний предел ожидания ack. ([Godot Engine documentation][5])

Практически:

* это один из самых полезных tuning knobs для реального продукта;
* но я бы в первой версии обертки просто давал тонкий passthrough, не пытаясь сразу строить “умную timeout policy abstraction”.

---

## `enet_peer_throttle_configure`

Это настройка packet throttle для unreliable-трафика.

Смысл такой:

* unreliable packets ENet может дропать в зависимости от состояния канала;
* throttle — это вероятность не дропнуть unreliable packet и действительно отправить его;
* параметры acceleration/deceleration/interval задают адаптацию.
  Именно так это описывают peer docs поверх ENet. ([Godot Engine documentation][5])

Практически:

* это low-level network tuning;
* не главный everyday API;
* но полезно иметь в peer wrapper как advanced method.

---

## `enet_peer_reset`

Это **forceful local reset** peer’а.

Он не про “корректно завершить соединение”, а про “сбросить peer state прямо сейчас”.
Сторонние docs на ENet peer прямо говорят, что foreign host не уведомляется и удаленная сторона timeout’нется сама позже. ([Godot Engine documentation][5])

Я бы оставил этот метод в обертке, но:

* назвал прямо `Reset()`,
* задокументировал как low-level escape hatch,
* не использовал как обычный disconnect.

---

# Что из твоего списка не надо тащить в обертку

Вот эти штуки я бы **вообще не экспортировал в ваш public low-level API**:

* `ENetAcknowledgement`
* `ENetIncomingCommand`
* `ENetOutgoingCommand`
* `ENetChannel`
* `ENetProtocol`
* `ENetPeerFlag`
* и функции `queue_*`, `dispatch_*`, `setup_*`, `reset_queues`, `has_outgoing_commands`, `throttle`

Почему:

* это уже transport internals ENet;
* они очень завязаны на внутренние очереди, sequence numbers, fragmentation, ack logic;
* прикладной код от них только пострадает;
* любая попытка “красиво завернуть всё” здесь даст лишнюю магию и сильную связность с внутренностями ENet. ([GitHub][1])

## Практическое правило

Если функция принимает/возвращает:

* `ENetIncomingCommand*`
* `ENetOutgoingCommand*`
* `ENetProtocol*`
* `ENetChannel*`

то это почти наверняка **не ваш public wrapper surface**.

---

# Как я бы систематизировал `DreamNetPeer`

Я бы делил методы peer wrapper на 5 групп.

## 1. Identity / state

* `IsValid()`
* `State()`
* `IsConnected()`
* `IsConnecting()`
* `IsDisconnected()`
* `Address()`
* `ChannelCount()`

## 2. Telemetry

* `RoundTripTime()`
* `RoundTripTimeVariance()`
* `PacketLoss()`
* `PacketLossVariance()`
* `Mtu()`
* `WindowSize()`
* `PacketsSent()`
* `PacketsLost()`
* `IncomingDataTotal()`
* `OutgoingDataTotal()`

## 3. Send / receive

* `Send(...)`
* maybe `TryReceive(...)` как advanced API
* `Ping()`
* `Flush()` тут не нужен, flush — это host-level responsibility

## 4. Disconnect / reset

* `Disconnect(...)`
* `DisconnectNow(...)`
* `DisconnectLater(...)`
* `Reset()`

## 5. Tuning

* `SetPingInterval(...)`
* `SetTimeout(...)`
* `ConfigureThrottle(...)`

И плюс:

* `Native()`
* maybe `RawUserData() / SetRawUserData()` если хочешь поддержать `peer->data`

---

# Что говорят best practices по использованию ENetPeer

Самые важные практические выводы такие:

### 1. Основной receive loop лучше строить через события, а не через direct peer receive

ENet tutorial и обзорные материалы подают ENet как event-driven модель: connect/disconnect/receive обрабатываются через event loop. ([enet.bespin.org][2])

### 2. `disconnect`, `disconnect_now`, `disconnect_later` — это три разных операции

Не надо схлопывать их в один метод. Семантика у них реально разная. ([Docs.rs][4])

### 3. `Reset()` — это low-level force reset, не обычное закрытие

Его стоит держать как escape hatch, а не как стандартный путь shutdown. ([rokojori.com][6])

### 4. Peer wrapper лучше делать non-owning

Потому что peer lifecycle принадлежит host’у. Это следует из устройства `ENetHost` и `ENetPeer` в `enet.h`. ([GitHub][1])

### 5. Не тащить внутрь public wrapper internal command/channel/protocol machinery

Это только усложнит API и сильнее привяжет вас к внутренностям ENet. ([GitHub][1])

---

# Мой итоговый совет

Для `DreamNetPeer` тебе реально нужно завернуть только **public peer surface**:

* send
* receive
* ping
* ping interval
* timeout
* disconnect
* disconnect now
* disconnect later
* reset
* throttle configure
* state/address/metrics/data/native

А всё, что связано с:

* `ENetIncomingCommand`
* `ENetOutgoingCommand`
* `ENetChannel`
* `ENetProtocol`
* `queue_*`
* `dispatch_*`

лучше оставить **полностью за кадром**.

Если хочешь, следующим сообщением я могу уже набросать тебе **конкретный интерфейс `DreamNetPeer`**, без реализации, но в хорошем разбиении по группам методов.

[1]: https://raw.githubusercontent.com/lsalzman/enet/master/include/enet/enet.h "raw.githubusercontent.com"
[2]: https://enet.bespin.org/Tutorial.html?utm_source=chatgpt.com "Tutorial - ENet"
[3]: https://enet.bespin.org/group__peer.html?utm_source=chatgpt.com "ENet peer functions"
[4]: https://docs.rs/enet/latest/enet/struct.Peer.html?utm_source=chatgpt.com "Peer in enet - Rust"
[5]: https://docs.godotengine.org/en/4.4/classes/class_enetpacketpeer.html?utm_source=chatgpt.com "ENetPacketPeer — Godot Engine (4.4) documentation in ..."
[6]: https://rokojori.com/en/labs/godot/docs/4.3/enetpacketpeer-class?utm_source=chatgpt.com "ENetPacketPeer | Godot Docs 4.3 | ROKOJORI Labs"
