#r "nuget: Microsoft.Data.Sqlite, 10.0.12"
#r "nuget: Migrondi.Core, 1.3.0"
#load "../src/Dreamsleeve.Server.Infrastructure/SqliteDatabase.fs"

open System
open System.Diagnostics
open System.IO
open Microsoft.Data.Sqlite
open Dreamsleeve.Server.Infrastructure

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let database = Path.Combine(root, "build", "sqlhydra", "schema.db")
let config = { DatabasePath = database; BusyTimeoutSeconds = 5 }

// Only this generator's disposable database is replaced. Runtime data lives elsewhere.
for suffix in [""; "-wal"; "-shm"] do
    File.Delete(database + suffix)

match SqliteDatabase.initialize config (Path.Combine(root, "db", "migrations")) with
| Ok () -> printfn "SQLite schema ready for SqlHydra: %s" database
| Error error -> eprintfn "%s" error; exit 1

SqliteConnection.ClearAllPools()
let start = ProcessStartInfo("dotnet", UseShellExecute = false)
start.WorkingDirectory <- Path.Combine(root, "src", "Dreamsleeve.Server.Infrastructure")
for argument in ["tool"; "run"; "sqlhydra"; "--"; "sqlite"] do
    start.ArgumentList.Add argument

let generate () =
    use generator = Process.Start start
    generator.WaitForExit()
    generator.ExitCode

exit (generate ())
