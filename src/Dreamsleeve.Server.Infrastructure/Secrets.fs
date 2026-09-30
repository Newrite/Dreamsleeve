namespace Dreamsleeve.Server.Infrastructure

open System
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Identity
open Microsoft.Extensions.Options

/// Random bearer secrets and password hashing shared by the account service and
/// the admin service. Raw secrets never reach storage or logs: SQLite and memory
/// keep only SHA-256 hashes.
[<RequireQualifiedAccess>]
module Secrets =
    /// 32 random bytes in base64url without padding: 43 characters.
    let newToken () =
        Convert.ToBase64String(RandomNumberGenerator.GetBytes 32).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let hash (secret: string) =
        Encoding.ASCII.GetBytes secret |> SHA256.HashData |> Convert.ToHexString

    /// The shape of newToken; anything else is refused before hashing or storage.
    let validToken (value: string) =
        not (isNull value) && value.Length = 43 && value |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_')

    /// 12..128 UTF-8 bytes, neither trimmed nor normalized; one rule for players and administrators.
    let validPassword (password: string) =
        if isNull password || password.Length > 128 then false
        else
            let bytes = Encoding.UTF8.GetByteCount password
            bytes >= 12 && bytes <= 128

    /// PBKDF2 iterations of every password: the OWASP minimum and a bound on sign-in cost.
    [<Literal>]
    let MinPasswordIterations = 210000

    [<Literal>]
    let MaxPasswordIterations = 2000000

    let hasher iterations =
        PasswordHasher<obj>(Options.Create(PasswordHasherOptions(IterationCount = iterations)))
