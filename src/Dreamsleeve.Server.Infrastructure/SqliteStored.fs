namespace Dreamsleeve.Server.Infrastructure

open System
open System.Data.Common
open System.IO

/// SQLite's getters can coerce REAL/TEXT values before domain validation. Check
/// the representation of a known projection first; domain constructors still
/// own enum, name and other value rules. Specifications belong to each query.
[<RequireQualifiedAccess>]
module internal SqliteStored =
    type Column =
        | Integer
        | PositiveInteger
        | Int32
        | UnixMilliseconds
        | Text
        | Blob
        | Number
        | Nullable of Column

    let private earliest = DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
    let private latest = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()

    let rec private accepts column (value: obj) =
        match column with
        | Column.Nullable required ->
            match value with :? DBNull -> true | _ -> accepts required value
        | Column.Integer -> match value with :? int64 -> true | _ -> false
        | Column.PositiveInteger -> match value with :? int64 as number -> number > 0L | _ -> false
        | Column.Int32 ->
            match value with
            | :? int64 as number -> number >= int64 Int32.MinValue && number <= int64 Int32.MaxValue
            | _ -> false
        | Column.UnixMilliseconds ->
            match value with
            | :? int64 as number -> number >= earliest && number <= latest
            | _ -> false
        | Column.Text -> match value with :? string -> true | _ -> false
        | Column.Blob -> match value with :? (byte array) -> true | _ -> false
        // A REAL-affinity field may legitimately retain a SQLite INTEGER.
        | Column.Number -> match value with :? double | :? int64 -> true | _ -> false

    let validate (reader: DbDataReader) first (columns: Column array) =
        let mutable failure = None
        let mutable index = 0
        while index < columns.Length && failure.IsNone do
            let ordinal = first + index
            if not (accepts columns[index] (reader.GetValue ordinal)) then
                failure <- Some(InvalidDataException($"Stored column '{reader.GetName ordinal}' has an invalid SQLite representation."))
            index <- index + 1
        match failure with
        | Some error -> Error error
        | None -> Ok ()
