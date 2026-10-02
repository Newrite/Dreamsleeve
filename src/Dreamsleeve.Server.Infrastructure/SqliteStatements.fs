namespace Dreamsleeve.Server.Infrastructure

open Microsoft.Data.Sqlite
open SqlHydra.Query
open Dreamsleeve.Server.Domain

/// Raw statements of the stores, where SqlHydra's typed queries do not fit:
/// every value is a parameter, and a statement joins the context's transaction.
module internal SqliteStatements =
    let command (context: QueryContext) sql (parameters: (string * obj) list) =
        let command = context.Connection.CreateCommand()
        context.Transaction |> Option.iter (fun transaction -> command.Transaction <- transaction)
        command.CommandText <- sql
        for name, value in parameters do command.Parameters.Add(SqliteParameter(name, value)) |> ignore
        command

    let execute context sql parameters =
        use statement = command context sql parameters
        statement.ExecuteNonQuery()

    let scalar context sql parameters =
        use statement = command context sql parameters
        statement.ExecuteScalar()

    /// profiles.name_color as stored.
    let nameColor (value: int64) =
        if value < 0L || value > 0xFFFFFFL then Error(DomainError.InvalidColor "name_color")
        else NameColor.create (uint32 value)

    /// Commits when action succeeds; an error or an exception rolls back.
    let transaction (context: QueryContext) action =
        use transaction = context.Connection.BeginTransaction()
        context.Transaction <- Some transaction
        let result = action ()
        match result with
        | Ok _ -> transaction.Commit()
        | Error _ -> ()
        result
