using System.Net.Sockets;
using System.Text.Json;
using Dreamsleeve.Network.Testing;

if (args.Length != 2) { Console.Error.WriteLine("Usage: relay config.json state-directory"); return 1; }
try
{
    if (args[0] == "--calibrate") return Calibration.Run(args[1]);
    var options = JsonSerializer.Deserialize<RelayOptions>(File.ReadAllText(args[0]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (options is null || !options.Valid) { Console.Error.WriteLine("Invalid relay options"); return 1; }
    Directory.CreateDirectory(args[1]);
    using var relay = new DatagramRelay(options);
    relay.Start();
    var ready = Path.Combine(args[1], "relay-ready.json");
    File.WriteAllText(ready + ".tmp", JsonSerializer.Serialize(new { port = relay.Port }));
    File.Move(ready + ".tmp", ready);
    await Task.WhenAny(Task.Run(Console.ReadLine), relay.Completion);
    relay.Dispose();
    File.WriteAllText(Path.Combine(args[1], "network.json"), JsonSerializer.Serialize(relay.Report, new JsonSerializerOptions { WriteIndented = true }));
    return relay.Healthy ? 0 : 1;
}
catch (Exception error) when (error is IOException or SocketException or JsonException or UnauthorizedAccessException)
{ Console.Error.WriteLine(error.Message); return 1; }
