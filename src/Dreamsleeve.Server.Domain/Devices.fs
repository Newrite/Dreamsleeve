namespace Dreamsleeve.Server.Domain

open System

/// A player's computer as one server sees it: the client hashes the board UUID
/// (SMBIOS) together with this server's address into 64 lowercase hex
/// characters (docs/AuthenticationRu.md, «Устройства»). Another server gets an
/// unrelated value and the UUID never leaves the computer. Patched clients can
/// send anything: a device ban stops casual new accounts, not a determined player.
type DeviceId = private DeviceId of string

[<RequireQualifiedAccess>]
module DeviceId =
    [<Literal>]
    let Length = 64

    let create (text: string) : Result<DeviceId, DomainError> =
        if isNull text then
            Error(DomainError.InvalidText("DeviceId", TextError.InvalidFormat))
        elif text.Length <> Length then
            Error(DomainError.InvalidText("DeviceId", TextError.InvalidFormat))
        elif text |> Seq.forall (fun c -> Char.IsAsciiDigit c || (c >= 'a' && c <= 'f')) then
            Ok(DeviceId text)
        else
            Error(DomainError.InvalidText("DeviceId", TextError.InvalidFormat))

    let value (DeviceId text) = text

    /// Enough of the hash to tell a player's devices apart on a page.
    let short (DeviceId text) = text.Substring(0, 12)

/// A device a player signed in or registered from, for the panel. Kept like
/// the sign-in addresses: SignInHistoryDays after the last sign-in from it.
type SignInDevice = {
    Device: DeviceId
    FirstSeen: DateTimeOffset
    LastSeen: DateTimeOffset
    SignIns: int64
}
