namespace Dreamsleeve.Server.Domain

open System
open System.Net
open System.Net.Sockets

/// Client IP addresses as the server stores and compares them: 16 bytes, IPv4
/// mapped into IPv6 (::ffff:a.b.c.d), so one layout and one byte order cover
/// both families. Only the admin panel ever shows them.
[<RequireQualifiedAccess>]
module ClientAddress =
    [<Literal>]
    let Length = 16

    let bytes (address: IPAddress) =
        let address =
            if address.AddressFamily = AddressFamily.InterNetwork then
                address.MapToIPv6()
            else
                address

        address.GetAddressBytes()

    let private mapped (bytes: byte array) =
        bytes.Length = Length
        && Array.forall ((=) 0uy) bytes[0..9]
        && bytes[10] = 0xFFuy
        && bytes[11] = 0xFFuy

    /// The stored form back; IPv4 stays IPv4.
    let ofBytes (bytes: byte array) =
        if isNull bytes || bytes.Length <> Length then
            None
        else
            let address = IPAddress(bytes)
            Some(if mapped bytes then address.MapToIPv4() else address)

    /// What a person reads: IPv4 without the mapping, IPv6 without a scope.
    let text (address: IPAddress) =
        match ofBytes (bytes address) with
        | Some plain -> plain.ToString()
        | None -> address.ToString()

    /// Whether the stored form is an IPv4 address.
    let isIpv4 (address: IPAddress) = mapped (bytes address)

/// An IP network: every address whose first Prefix bits equal Network's. Kept
/// in the 16-byte form of ClientAddress, so an IPv4 /24 has prefix 96 + 24.
type AddressRange = private {
    network: byte array
    prefix: int
}

[<RequireQualifiedAccess>]
module AddressRange =
    /// The widest range a ban may take: an IPv4 /8 or an IPv6 /24. Wider is a
    /// typo that would lock out a continent.
    [<Literal>]
    let MinIpv4Prefix = 8

    [<Literal>]
    let MinIpv6Prefix = 24

    let private masked (bytes: byte array) prefix =
        bytes
        |> Array.mapi (fun index value ->
            let bits = prefix - index * 8
            if bits >= 8 then value
            elif bits <= 0 then 0uy
            else value &&& byte (0xFF <<< (8 - bits)))

    let private create (bytes: byte array) prefix = {
        network = masked bytes prefix
        prefix = prefix
    }

    let private invalid () = Error(DomainError.InvalidText("AddressRange", TextError.InvalidFormat))

    let private parsePrefix longest (prefix: string option) =
        match prefix with
        | None -> Some longest
        | Some value ->
            match Int32.TryParse value with
            | true, bits when value |> Seq.forall Char.IsAsciiDigit -> Some bits
            | true, _ | false, _ -> None

    /// "203.0.113.7", "203.0.113.0/24" or "2001:db8::/48"; the host bits are
    /// cleared. A single address is the whole host: /32 or /128.
    let parse (text: string) : Result<AddressRange, DomainError> =
        let text = if isNull text then "" else text.Trim()

        let address, prefix =
            match text.IndexOf '/' with
            | -1 -> text, None
            | slash -> text.Substring(0, slash), Some(text.Substring(slash + 1))

        match IPAddress.TryParse address with
        | false, _ -> invalid ()
        | true, ip ->
            let ipv4 = ClientAddress.isIpv4 ip
            let widest, longest = if ipv4 then MinIpv4Prefix, 32 else MinIpv6Prefix, 128

            let bits = parsePrefix longest prefix

            match bits with
            | None -> invalid ()
            | Some bits when bits < widest || bits > longest ->
                Error(DomainError.InvalidLimit("AddressRange", bits))
            | Some bits ->
                Ok(create (ClientAddress.bytes ip) (if ipv4 then 96 + bits else bits))

    /// The range a ban of one address suggests: the address itself for IPv4, its
    /// /64 for IPv6, which a single home or phone usually holds whole.
    let around (address: IPAddress) =
        let bytes = ClientAddress.bytes address

        if ClientAddress.isIpv4 address then
            create bytes 128
        else
            create bytes 64

    let contains (range: AddressRange) (address: IPAddress) =
        masked (ClientAddress.bytes address) range.prefix = range.network

    /// Canonical CIDR: "203.0.113.0/24", "2001:db8::/48".
    let key (range: AddressRange) =
        match ClientAddress.ofBytes range.network with
        | Some address when address.AddressFamily = AddressFamily.InterNetwork -> $"{address}/{range.prefix - 96}"
        | Some address -> $"{address}/{range.prefix}"
        | None -> "?"

    /// The stored form: the network bytes and the 16-byte prefix.
    let network (range: AddressRange) = Array.copy range.network

    let prefix (range: AddressRange) = range.prefix

    /// The first and last address of the range in the stored form; storage
    /// finds the addresses between them by byte order.
    let bounds (range: AddressRange) =
        let last =
            range.network
            |> Array.mapi (fun index value ->
                let bits = range.prefix - index * 8
                if bits >= 8 then value
                elif bits <= 0 then 0xFFuy
                else value ||| byte (0xFF >>> bits))

        Array.copy range.network, last

    /// A stored range read back; refused unless it is exactly what parse makes.
    let ofStored (network: byte array) (prefix: int) =
        if isNull network || network.Length <> ClientAddress.Length || prefix < 0 || prefix > 128 then
            None
        else
            let range = create network prefix

            if range.network = network then
                Some range
            else
                None

/// A ban of an IP range from the admin panel: no sign-in, registration or game
/// connection from it while it holds. Reason and term follow the rules of a
/// player's sanction (SanctionReason, SanctionTerm).
type AddressBan = {
    Id: int64
    Range: AddressRange
    Reason: SanctionReason
    /// Absent once that administrator account is gone.
    IssuedBy: AdminId voption
    IssuedAt: DateTimeOffset
    /// Absent: until lifted.
    Expires: DateTimeOffset voption
}

[<RequireQualifiedAccess>]
module AddressBan =
    let activeAt (now: DateTimeOffset) (ban: AddressBan) =
        match ban.Expires with
        | ValueNone -> true
        | ValueSome expires -> now < expires

    /// The first ban in force at now that covers address.
    let find now (address: IPAddress) (bans: AddressBan seq) =
        bans
        |> Seq.tryFind (fun ban -> activeAt now ban && AddressRange.contains ban.Range address)
        |> ValueOption.ofOption

/// An address a player signed in or registered from, for the panel. Kept for
/// a configured number of days after the last sign-in from it.
type SignInAddress = {
    Address: IPAddress
    FirstSeen: DateTimeOffset
    LastSeen: DateTimeOffset
    SignIns: int64
}
