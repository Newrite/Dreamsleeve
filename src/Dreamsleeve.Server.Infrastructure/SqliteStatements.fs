namespace Dreamsleeve.Server.Infrastructure

open Microsoft.Data.Sqlite
open SqlHydra.Query

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

    /// Commits when action succeeds; an error or an exception rolls back.
    let transaction (context: QueryContext) action =
        use transaction = context.Connection.BeginTransaction()
        context.Transaction <- Some transaction
        let result = action ()
        match result with
        | Ok _ -> transaction.Commit()
        | Error _ -> ()
        result
