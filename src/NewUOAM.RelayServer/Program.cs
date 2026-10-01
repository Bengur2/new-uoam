using System.Net;
using NewUOAM.RelayServer;

int port = 27980;
IPAddress bindAddress = IPAddress.Any; // 0.0.0.0 - works whether this runs locally, on a LAN box, or a public VPS
string? adminPassword = null;
string roomsFilePath = Path.Combine(AppContext.BaseDirectory, "rooms.json");

for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out int p)) port = p;
    else if (args[i] == "--bind" && IPAddress.TryParse(args[i + 1], out var b)) bindAddress = b;
    else if (args[i] == "--admin-password") adminPassword = args[i + 1];
    else if (args[i] == "--rooms-file") roomsFilePath = args[i + 1];
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var server = new RelayServer(bindAddress, port, adminPassword, roomsFilePath);
await server.RunAsync(cts.Token);
