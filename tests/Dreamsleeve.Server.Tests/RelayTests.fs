module Dreamsleeve.Server.Tests.RelayTests

open System
open Expecto
open Dreamsleeve.Network.Testing

let tests = testList "Network relay calibration" [
    testCase "five MiB paced datagrams do not overflow a 64 KiB queue" <| fun _ ->
        let rate = 5.0 * 1048576.0
        let queue = LinkSchedule(rate, 65536.0)
        let period = 1400.0 / rate
        for i in 0..20000 do
            let at = 123.0 + double i * period
            let result = queue.Admit(at, 1372) // 28 bytes of IPv4/UDP overhead.
            Expect.isTrue result.HasValue "No false burst from time quantization."
            Expect.isLessThan (abs (result.Value - at - period)) 0.00000001 "Exact FIFO serialization time."

    testCase "FIFO counts headers rejects overflow and drains with elapsed time" <| fun _ ->
        let queue = LinkSchedule(1024.0, 4096.0)
        for i in 1..4 do Expect.equal (queue.Admit(0.0, 996).Value) (double i) "Wire bytes set departure."
        Expect.isFalse (queue.Admit(0.0, 996).HasValue) "Fifth datagram does not fit."
        Expect.isFalse (queue.Admit(0.5, 996).HasValue) "Partial drain is insufficient."
        Expect.equal (queue.Admit(1.0, 996).Value) 5.0 "Rejected datagram did not reserve bytes."
        Expect.equal (queue.Admit(10.0, 996).Value) 11.0 "Idle time cannot create negative debt."

    testCase "directions have independent bandwidth and disabled shaping has no debt" <| fun _ ->
        let upload, download = LinkSchedule(1024.0, 1024.0), LinkSchedule(1024.0, 1024.0)
        Expect.equal (upload.Admit(0.0, 996).Value) 1.0 "Upload."
        Expect.equal (download.Admit(0.0, 996).Value) 1.0 "Download does not consume upload queue."
        let unshaped = LinkSchedule(0.0, 1.0)
        Expect.equal (unshaped.Admit(0.0, 65507).Value) 0.0 "No artificial shaping at rate zero."

    testCase "invalid timing options fail validation before starting sockets" <| fun _ ->
        Expect.isTrue (RelayOptions(8778, 100.0, 10.0, 200, 5.0, 64.0).Valid) "Supported scenario."
        Expect.isFalse (RelayOptions(8778, Double.NaN, 0.0, 0, 5.0, 64.0).Valid) "NaN cannot poison the priority queue."
        Expect.isFalse (RelayOptions(8778, 100.0, 0.0, 0, 5.0, 0.0).Valid) "Positive queue required."
        Expect.isFalse (RelayOptions(0, 100.0, 0.0, 0, 5.0, 64.0).Valid) "Real server port required."
]
