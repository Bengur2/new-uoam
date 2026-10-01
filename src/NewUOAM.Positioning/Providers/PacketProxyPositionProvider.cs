using System.Net;
using System.Net.Sockets;
using NewUOAM.MapData.Maps;
using NewUOAM.Positioning.PacketProxy;

namespace NewUOAM.Positioning.Providers;

/// <summary>
/// Variant B1: a local TCP relay sitting between client.exe and the real game server. Point the
/// client's server address at this proxy's <see cref="LoginPort"/> instead of the real server;
/// the proxy connects onward to <see cref="RealServerHost"/>:<see cref="RealServerPort"/> and
/// relays every byte in both directions completely unmodified, with ONE exception: the login
/// server's 0x8C (Play Server Ack) redirect packet has its embedded game-server IP/port rewritten
/// to point back at this proxy's own second listener, so the client's follow-up connection (the
/// "game server" hop, made after character login) also routes through us - that's where the
/// position packets actually live.
///
/// Position extraction (<see cref="PositionExtractor"/>, fed by <see cref="PacketStreamWalker"/>)
/// is a purely observational side-channel on the game-hop relay: it never gates or modifies what
/// gets forwarded, so a parsing mistake can only cost us position updates, never corrupt the
/// player's actual connection.
/// </summary>
public sealed class PacketProxyPositionProvider : IPositionProvider
{
    public string RealServerHost { get; }
    public int RealServerPort { get; }
    public int LoginPort { get; }

    public string Name => "Packet proxy (B1)";
    public string Description => $"Relays client.exe <-> {RealServerHost}:{RealServerPort} through 127.0.0.1:{LoginPort}, reading (not modifying) position packets.";
    public PositionProviderStatus Status { get; private set; } = PositionProviderStatus.Stopped;

    public event EventHandler<PositionUpdate>? PositionChanged;
    public event EventHandler<string>? StatusMessage;

    private TcpListener? _loginListener;
    private TcpListener? _gameListener;
    private CancellationTokenSource? _cts;
    private readonly List<Task> _connectionTasks = new();

    // Set by the login-hop when it sees the client's 0xA0 (Select Server) and consumed once the
    // matching 0x8C shows up - not thread-safe against multiple concurrent login attempts, which
    // is an acceptable limitation for one personal character at a time.
    private volatile string? _pendingRealGameHost;
    private volatile int _pendingRealGamePort;

    public PacketProxyPositionProvider(string realServerHost, int realServerPort, int loginPort = 2593)
    {
        RealServerHost = realServerHost;
        RealServerPort = realServerPort;
        LoginPort = loginPort;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Status == PositionProviderStatus.Running) return Task.CompletedTask;
        Status = PositionProviderStatus.Starting;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;

        try
        {
            _loginListener = new TcpListener(IPAddress.Loopback, LoginPort);
            _loginListener.Start();

            _gameListener = new TcpListener(IPAddress.Loopback, 0); // OS-assigned port
            _gameListener.Start();
            int gamePort = ((IPEndPoint)_gameListener.LocalEndpoint).Port;

            _connectionTasks.Add(Task.Run(() => AcceptLoop(_loginListener, gamePort, isGameHop: false, ct), ct));
            _connectionTasks.Add(Task.Run(() => AcceptLoop(_gameListener, gamePort, isGameHop: true, ct), ct));

            Status = PositionProviderStatus.Running;
            StatusMessage?.Invoke(this, $"Proxy naslouchá na 127.0.0.1:{LoginPort} (login) a 127.0.0.1:{gamePort} (game); přepošlu na {RealServerHost}:{RealServerPort}.");
        }
        catch (Exception ex)
        {
            Status = PositionProviderStatus.Error;
            StatusMessage?.Invoke(this, $"Nepodařilo se spustit proxy: {ex.Message}");
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        _loginListener?.Stop();
        _gameListener?.Stop();
        try { await Task.WhenAll(_connectionTasks); } catch { /* individual tasks report their own errors */ }
        _connectionTasks.Clear();
        Status = PositionProviderStatus.Stopped;
    }

    private async Task AcceptLoop(TcpListener listener, int gameHopLocalPort, bool isGameHop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            _ = HandleConnectionAsync(client, gameHopLocalPort, isGameHop, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient clientSocket, int gameHopLocalPort, bool isGameHop, CancellationToken ct)
    {
        using var _ = clientSocket;

        string upstreamHost;
        int upstreamPort;
        if (isGameHop)
        {
            // Wait briefly for the login hop to have told us where the real game server is
            // (it saw this before redirecting the client to us).
            for (int i = 0; i < 50 && _pendingRealGameHost is null; i++) await Task.Delay(20, ct);
            upstreamHost = _pendingRealGameHost ?? RealServerHost;
            upstreamPort = _pendingRealGamePort != 0 ? _pendingRealGamePort : RealServerPort;
        }
        else
        {
            upstreamHost = RealServerHost;
            upstreamPort = RealServerPort;
        }

        string hopName = isGameHop ? "game hop" : "login hop";
        StatusMessage?.Invoke(this, $"[{hopName}] klient připojen, připojuji se na {upstreamHost}:{upstreamPort}…");

        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(upstreamHost, upstreamPort, ct);
        }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(this, $"[{hopName}] nepodařilo se připojit na {upstreamHost}:{upstreamPort}: {ex.Message}");
            return;
        }
        StatusMessage?.Invoke(this, $"[{hopName}] připojeno na {upstreamHost}:{upstreamPort}, relay běží.");

        // Attach a position extractor on BOTH hops, not just the game hop: OrionUO may not make
        // the client do a literal second TCP connection after the redirect (it has its own
        // "useproxy"/"proxyname" connection handling, seen in its profile config, whose exact
        // behaviour is undocumented) - if real gameplay traffic ends up flowing over the login
        // hop instead of a genuine second connection, this still catches it.
        var extractor = new PositionExtractor();
        extractor.EnterWorld += info =>
        {
            StatusMessage?.Invoke(this, $"[{hopName}] Enter World: serial=0x{info.Serial:X8} pos=({info.X},{info.Y},{info.Z}) mapa {info.MapWidthTiles}x{info.MapHeightTiles}.");
            OnEnterWorld(info);
        };
        extractor.PositionChanged += (x, y, z) => OnPositionSample(x, y, z);

        var clientToUpstream = RelayAsync(clientSocket.GetStream(), upstream.GetStream(),
            fromClient: true, isGameHop, hopName, gameHopLocalPort, extractor, ct);
        var upstreamToClient = RelayAsync(upstream.GetStream(), clientSocket.GetStream(),
            fromClient: false, isGameHop, hopName, gameHopLocalPort, extractor, ct);

        try { await Task.WhenAll(clientToUpstream, upstreamToClient); }
        catch (Exception ex) { StatusMessage?.Invoke(this, $"[{hopName}] neočekávaná výjimka: {ex.GetType().Name}: {ex.Message}"); }

        StatusMessage?.Invoke(this, $"[{hopName}] spojení skončilo.");
    }

    /// <summary>Copies bytes from <paramref name="from"/> to <paramref name="to"/> unmodified,
    /// except for the one login-hop 0x8C rewrite (via <see cref="LoginRedirectRewriter"/>), while
    /// feeding a side-channel packet walker (both hops) for position extraction and diagnostics.</summary>
    private async Task RelayAsync(NetworkStream from, NetworkStream to, bool fromClient, bool isGameHop,
        string hopName, int gameHopLocalPort, PositionExtractor extractor, CancellationToken ct)
    {
        var walker = new PacketStreamWalker();
        string dir = fromClient ? "client->server" : "server->client";
        var seenIds = new List<byte>();
        bool loggedFirstIds = false;
        bool gaveUpReported = false;

        LoginRedirectRewriter? rewriter = null;
        if (!isGameHop && !fromClient)
        {
            rewriter = new LoginRedirectRewriter();
            rewriter.RedirectSeen += (host, port) =>
            {
                _pendingRealGameHost = host;
                _pendingRealGamePort = port;
                StatusMessage?.Invoke(this, $"Herní server přesměrován: {host}:{port} -> proxy na 127.0.0.1:{gameHopLocalPort}.");
            };
            rewriter.PacketObserved += packet =>
            {
                if (seenIds.Count < 12) seenIds.Add(packet[0]);
                extractor.OnServerPacket(packet);
            };
            walker = rewriter.Walker; // so the GaveUp diagnostics below read the same instance
        }

        var readBuf = new byte[8192];
        bool loggedFirstBytes = false;

        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = await from.ReadAsync(readBuf, ct); }
            catch (Exception ex)
            {
                StatusMessage?.Invoke(this, $"[{hopName}, {dir}] čtení skončilo výjimkou: {ex.GetType().Name}: {ex.Message}");
                break;
            }
            if (n == 0)
            {
                StatusMessage?.Invoke(this, $"[{hopName}, {dir}] druhá strana zavřela spojení (0 bajtů při čtení).");
                break;
            }

            if (!loggedFirstBytes)
            {
                loggedFirstBytes = true;
                string hex = string.Join(" ", readBuf.Take(Math.Min(n, 32)).Select(b => $"{b:X2}"));
                StatusMessage?.Invoke(this, $"[{hopName}, {dir}] první data: {n} bajtů, prvních {Math.Min(n, 32)}: {hex}");
            }

            try
            {
                if (rewriter is not null)
                {
                    // Login hop, server -> client: only ever forwards once it has the complete,
                    // possibly-rewritten packet (or has given up) - see LoginRedirectRewriter's
                    // docs for why chunk-by-chunk in-place rewriting isn't safe here.
                    byte[] toSend = rewriter.Feed(readBuf.AsSpan(0, n), IPAddress.Loopback, gameHopLocalPort);
                    if (toSend.Length > 0) await to.WriteAsync(toSend, ct);
                }
                else
                {
                    await to.WriteAsync(readBuf.AsMemory(0, n), ct);

                    foreach (var packet in walker.Feed(readBuf.AsSpan(0, n)))
                    {
                        if (seenIds.Count < 6) seenIds.Add(packet[0]);
                        if (fromClient) extractor.OnClientPacket(packet);
                        else extractor.OnServerPacket(packet);
                    }
                }
            }
            catch (Exception ex)
            {
                StatusMessage?.Invoke(this, $"[{hopName}, {dir}] zápis/zpracování skončilo výjimkou: {ex.GetType().Name}: {ex.Message}");
                break;
            }

            if (!loggedFirstIds && seenIds.Count >= 6)
            {
                loggedFirstIds = true;
                StatusMessage?.Invoke(this, $"[{hopName}, {dir}] prvních packet ID: {string.Join(" ", seenIds.Select(b => $"0x{b:X2}"))}");
            }

            if (walker.GaveUp && !gaveUpReported)
            {
                gaveUpReported = true;
                StatusMessage?.Invoke(this, $"[{hopName}, {dir}] parsování vzdalo po {walker.FramedPacketCount} packetech na ID 0x{walker.GaveUpOnPacketId:X2} - provoz se dál přeposílá normálně, jen bez čtení pozice z tohoto směru.");
            }
        }
    }

    private void OnEnterWorld(EnterWorldInfo info)
    {
        int facet = ResolveFacetIndex(info.MapWidthTiles, info.MapHeightTiles);
        PositionChanged?.Invoke(this, new PositionUpdate(info.X, info.Y, info.Z, facet, null, DateTimeOffset.UtcNow));
    }

    private int _lastKnownFacet = 0;

    private void OnPositionSample(int x, int y, int z)
    {
        PositionChanged?.Invoke(this, new PositionUpdate(x, y, z, _lastKnownFacet, null, DateTimeOffset.UtcNow));
    }

    /// <summary>0x1B gives map width/height in tiles but not a facet index - match it against the
    /// known stock facet dimensions. Falls back to 0 (Felucca) if nothing matches (e.g. a very
    /// unusually-resized custom facet), which just means the app looks at the wrong facet until
    /// the next 0x1B (re-login/facet change) - not fatal, just imprecise.</summary>
    private int ResolveFacetIndex(int widthTiles, int heightTiles)
    {
        foreach (var f in FacetInfo.Known.Values)
        {
            if (f.WidthTiles == widthTiles && f.HeightTiles == heightTiles)
            {
                _lastKnownFacet = f.Index;
                return f.Index;
            }
        }
        return _lastKnownFacet;
    }

    public void Dispose()
    {
        _loginListener?.Stop();
        _gameListener?.Stop();
        _cts?.Dispose();
    }
}
