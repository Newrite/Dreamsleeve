# ENet owner regression harness

This historical experiment project now tests the production `TransportOwner`.
There is no separate diagnostic worker implementation. Run it with:

```powershell
dotnet run --project tests/Dreamsleeve.EnetWorkerExperiment -c Release -- --summary
```

To prepare protocol-identical owner/inline binaries, without concurrent builds:

```powershell
python Scripts/build_enet_worker_experiment.py
```

The script builds `build/enet-worker-server` using production ownership, then
`build/enet-inline-server` using the same adapter serviced directly by runtime.
It restores source bytes in `finally` and rebuilds owner outputs even if the
baseline build fails. Use `--server-benchmark` in `Scripts/benchmark_enet_workers.py` to
select the corresponding `Dreamsleeve.Server.NetworkBenchmarks.dll`.

Queue and work budgets now come from `Server.Worker` configuration. The historical
`DREAMSLEEVE_ENET_WORKER_BUDGET` and report variables are not production settings.
Historical reports remain unchanged; production-owner results require new runs.
