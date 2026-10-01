using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NewUOAM.Positioning.Providers;

/// <summary>
/// Variant A: receives position updates pushed by an OrionUO script
/// (see tools/OrionScripts/PositionFeed.ojs) over loopback UDP. The script runs in-process with
/// the game client and pushes on every tick, so latency is essentially the script's own loop
/// delay (tens of milliseconds) plus loopback network time (sub-millisecond).
///
/// Wire format (ASCII, one line per packet, '|'-separated):
///   UOAM1|X|Y|Z|MAP|NAME
/// Example:
///   UOAM1|1234|1567|0|0|Ondra
/// </summary>
public sealed class OrionUdpPositionProvider : IPositionProvider
{
    public const string ProtocolTag = "UOAM1";

    private readonly int _port;
    private readonly IPAddress _bindAddress;
    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    public string Name => "Orion script (UDP)";
    public string Description => $"Listens on 127.0.0.1:{_port} for position pushes from an OrionUO script.";
    public PositionProviderStatus Status { get; private set; } = PositionProviderStatus.Stopped;

    public event EventHandler<PositionUpdate>? PositionChanged;
    public event EventHandler<string>? StatusMessage;

    public OrionUdpPositionProvider(int port = 27974, IPAddress? bindAddress = null)
    {
        _port = port;
        _bindAddress = bindAddress ?? IPAddress.Loopback;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Status == PositionProviderStatus.Running) return Task.CompletedTask;

        Status = PositionProviderStatus.Starting;
        try
        {
            _client = new UdpClient(new IPEndPoint(_bindAddress, _port));
        }
        catch (SocketException ex)
        {
            Status = PositionProviderStatus.Error;
            StatusMessage?.Invoke(this, $"Failed to bind UDP port {_port}: {ex.Message}");
            throw;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        Status = PositionProviderStatus.Running;
        StatusMessage?.Invoke(this, $"Listening on 127.0.0.1:{_port}.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        _client?.Close();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; } catch (OperationCanceledException) { }
        }
        Status = PositionProviderStatus.Stopped;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        if (_client is null) return;

        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _client.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                StatusMessage?.Invoke(this, $"UDP receive error: {ex.Message}");
                continue;
            }

            string text = Encoding.ASCII.GetString(result.Buffer);
            if (TryParse(text, out var update))
                PositionChanged?.Invoke(this, update);
            else
                StatusMessage?.Invoke(this, $"Ignored malformed packet: '{text}'");
        }
    }

    internal static bool TryParse(string line, out PositionUpdate update)
    {
        update = default!;
        string[] parts = line.Trim().Split('|');
        if (parts.Length < 5 || parts[0] != ProtocolTag) return false;

        if (!int.TryParse(parts[1], out int x)) return false;
        if (!int.TryParse(parts[2], out int y)) return false;
        if (!int.TryParse(parts[3], out int z)) return false;
        if (!int.TryParse(parts[4], out int map)) return false;
        string? name = parts.Length > 5 ? parts[5] : null;

        update = PositionUpdate.Now(x, y, z, map, name);
        return true;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _cts?.Dispose();
    }
}
