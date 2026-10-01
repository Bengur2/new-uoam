using System.Collections.Concurrent;
using System.Net;
using NewUOAM.Positioning;
using NewUOAM.Positioning.Relay;
using NewUOAM.RelayServer;
// Loopback test of private chat ("-c name>text") and Panic! mode against a real RelayServer.
// Args: none = in-process server on loopback; "host:port roomPassword" = against a deployed
// server (the room must exist).

string host = "127.0.0.1";
int port = 27990;
string roomPass = "pw-test";
CancellationTokenSource? serverCts = null;
Task? serverTask = null;
if (args.Length >= 2)
{
    host = args[0][..args[0].LastIndexOf(':')];
    port = int.Parse(args[0][(args[0].LastIndexOf(':') + 1)..]);
    roomPass = args[1];
}
else
{
    serverCts = new CancellationTokenSource();
    string roomsFile = Path.Combine(Path.GetTempPath(), $"uoam-rooms-{Guid.NewGuid():N}.json");
    serverTask = new RelayServer(IPAddress.Loopback, port, "admin", roomsFile).RunAsync(serverCts.Token);
    await Task.Delay(300);
    var created = await RelayAdminClient.CreateRoomAsync(host, port, "admin", "Test", roomPass);
    var other = await RelayAdminClient.CreateRoomAsync(host, port, "admin", "Other", "pw-other");
    Console.WriteLine($"rooms created: {created.Success} {other.Success}");
}

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

var logs = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
async Task<RelayMultiplayerClient> Connect(string name, string pass)
{
    var q = logs.GetOrAdd(name, _ => new ConcurrentQueue<string>());
    var c = new RelayMultiplayerClient(host, port, pass, name, "FF8800");
    c.ChatMessageReceived += (_, m) => q.Enqueue($"chat|{m.Name}|{m.Message}|{(m.Private ? "P" : "-")}|{m.To}");
    // panic|SET|name|notify  or  panic|NONE|notify|actor|previous
    c.PanicChanged += (_, p) => q.Enqueue(p.Panicker is { } who
        ? $"panic|SET|{who}|{(p.Notify ? 1 : 0)}"
        : $"panic|NONE|{(p.Notify ? 1 : 0)}|{p.Actor}|{p.Previous}");
    c.ReportLocalPosition(PositionUpdate.Now(1000, 1000, 0, 0, name));
    var r = await c.StartAsync();
    Check(r == RelayConnectResult.Connected, $"{name} connected ({r})");
    return c;
}
List<string> Drain(string name) { var list = new List<string>(); while (logs[name].TryDequeue(out var s)) list.Add(s); return list; }

var a = await Connect("Alice", roomPass);
var b = await Connect("Bob", roomPass);
var c = await Connect("Carol", roomPass);
var x = await Connect("Xavier", "pw-other");
await Task.Delay(400);
foreach (var n in new[] { "Alice", "Bob", "Carol", "Xavier" }) Drain(n);

// --- private chat
a.SendChatMessage("tajne příliš | ahoj", "Bob");
await Task.Delay(300);
var la = Drain("Alice"); var lb = Drain("Bob"); var lc = Drain("Carol"); var lx = Drain("Xavier");
Check(la.Contains("chat|Alice|tajne příliš | ahoj|P|Bob"), $"sender sees own private echo: {string.Join(";", la)}");
Check(lb.Contains("chat|Alice|tajne příliš | ahoj|P|"), $"Bob gets private with P flag: {string.Join(";", lb)}");
Check(!lc.Any(s => s.StartsWith("chat")), "Carol does NOT get the private message");
Check(!lx.Any(s => s.StartsWith("chat")), "other room does NOT get it");

a.SendChatMessage("verejne");
await Task.Delay(300);
Check(Drain("Bob").Contains("chat|Alice|verejne|-|") && Drain("Carol").Contains("chat|Alice|verejne|-|"), "public message reaches Bob and Carol, not private");
Drain("Alice");

a.SendChatMessage("nikomu", "Nobody");
await Task.Delay(300);
Check(!Drain("Bob").Any(s => s.StartsWith("chat")) && !Drain("Carol").Any(s => s.StartsWith("chat")), "private to unknown name is dropped, not broadcast");
Drain("Alice");

// --- panic: one per room, anyone can turn it off
a.SetPanic(true);
await Task.Delay(300);
Check(Drain("Alice").Contains("panic|SET|Alice|1"), "Alice gets her own panic (notify)");
Check(Drain("Bob").Contains("panic|SET|Alice|1"), "Bob gets Alice's panic (notify)");
Check(Drain("Carol").Contains("panic|SET|Alice|1"), "Carol gets Alice's panic (notify)");
Check(!Drain("Xavier").Any(s => s.StartsWith("panic|SET|Alice")), "other room doesn't see it");

a.SetPanic(true); // already hers
await Task.Delay(300);
Check(Drain("Alice").Contains("panic|SET|Alice|1") && !Drain("Bob").Contains("panic|SET|Alice|1"), "repeated -panic answers only the sender (sweep resyncs are notify=0)");

var d = await Connect("Dave", roomPass);
await Task.Delay(400);
Check(Drain("Dave").Contains("panic|SET|Alice|0"), "newcomer Dave learns Alice panics (notify=0)");

b.SetPanic(true); // takes over - only one panic per room
await Task.Delay(300);
Check(Drain("Alice").Contains("panic|SET|Bob|1") && Drain("Dave").Contains("panic|SET|Bob|1"), "Bob's -panic replaces Alice's for everyone");
Drain("Bob"); Drain("Carol");

c.SetPanic(false); // Carol turns off Bob's panic
await Task.Delay(300);
Check(Drain("Bob").Contains("panic|NONE|1|Carol|Bob"), "anyone can turn it off: Bob sees Carol ended his panic");
Check(Drain("Alice").Contains("panic|NONE|1|Carol|Bob"), "Alice sees it too");
Drain("Carol"); Drain("Dave");

b.SetPanic(false); // nothing to turn off
await Task.Delay(300);
Check(Drain("Bob").Contains("panic|NONE|1|Bob|") && !Drain("Alice").Any(s => s.StartsWith("panic|NONE|1")), "-unpanic with no panic answers only the sender");

d.SetPanic(true);
await Task.Delay(300);
foreach (var n in new[] { "Alice", "Bob", "Carol", "Dave" }) Drain(n);
await d.StopAsync(); d.Dispose();
await Task.Delay(400);
Check(Drain("Alice").Contains("panic|NONE|0|Dave|Dave"), "a panicking player who leaves takes the panic along (quiet)");

await Task.Delay(3500); // one sweep
Check(Drain("Carol").Any(s => s == "panic|NONE|0||"), "sweep re-sends the room's state, 'none' included");

foreach (var cl in new[] { a, b, c, x }) { await cl.StopAsync(); cl.Dispose(); }
if (serverCts is not null) { serverCts.Cancel(); try { await serverTask!; } catch { } }
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
