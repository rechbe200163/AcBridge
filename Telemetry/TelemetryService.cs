using AcBridge.Models;

namespace AcBridge.Telemetry;

public sealed class TelemetryOptions
{
    /// <summary>Auto = Shared Memory unter Windows, sonst Mock. Oder explizit "SharedMemory" / "Mock".</summary>
    public string Source { get; set; } = "Auto";
    /// <summary>Wie oft das Shared Memory gelesen wird.</summary>
    public int PollHz { get; set; } = 60;
    /// <summary>Standardrate pro WebSocket-Client (per ?hz= überschreibbar).</summary>
    public int DefaultStreamHz { get; set; } = 30;
    public int MaxStreamHz { get; set; } = 60;
    /// <summary>Anzahl Minisektoren pro Runde.</summary>
    public int MiniSectors { get; set; } = 45;
}

/// <summary>
/// Hält den jeweils letzten Stand. Die WebSocket-Clients lesen von hier in ihrem eigenen Takt,
/// damit ein langsamer Client (iPad im schlechten WLAN) niemanden sonst ausbremst.
/// </summary>
public sealed class TelemetryStore
{
    private volatile TelemetryFrame? _frame;
    private volatile SessionInfo? _session;
    private int _sessionVersion;
    private int _streamClients;

    public TelemetryFrame? Latest => _frame;
    public SessionInfo? Session => _session;
    public int SessionVersion => Volatile.Read(ref _sessionVersion);
    public bool Connected { get; set; }
    public string SourceName { get; set; } = "";
    public int StreamClients => Volatile.Read(ref _streamClients);

    public void Publish(TelemetryFrame frame) => _frame = frame;

    /// <returns>true, wenn sich die Session geändert hat</returns>
    public bool SetSession(SessionInfo? session)
    {
        if (Equals(_session, session)) return false;
        _session = session;
        Interlocked.Increment(ref _sessionVersion);
        return true;
    }

    public void Reset()
    {
        _frame = null;
        SetSession(null);
    }

    public int ClientConnected() => Interlocked.Increment(ref _streamClients);
    public int ClientDisconnected() => Interlocked.Decrement(ref _streamClients);

    public BridgeStatus GetStatus() =>
        new(Connected, SourceName, _frame?.Status, StreamClients, _frame?.Timestamp);
}

/// <summary>Liest im Hintergrund die Quelle und schreibt in den Store.</summary>
public sealed class TelemetryPoller(
    ITelemetrySource source,
    IFieldSource fieldSource,
    TelemetryStore store,
    TelemetryOptions options,
    ILogger<TelemetryPoller> log) : BackgroundService
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StaticRefresh = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        store.SourceName = source.Name;
        log.LogInformation("Telemetrie-Quelle: {Source}, Poll-Rate {Hz} Hz", source.Name, options.PollHz);

        var period = TimeSpan.FromSeconds(1.0 / Math.Clamp(options.PollHz, 1, 333));
        using var timer = new PeriodicTimer(period);
        long seq = 0;
        var lastPacket = int.MinValue;
        var lastStaticRead = DateTime.MinValue;
        var lastChange = DateTime.UtcNow;
        var field = new FieldTracker(options.MiniSectors);
        var miniSectors = new MiniSectorTracker(options.MiniSectors) { Field = field };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var fieldWasActive = false;

        while (!ct.IsCancellationRequested)
        {
            if (!source.TryConnect())
            {
                if (store.Connected) log.LogInformation("Verbindung zu AC verloren");
                store.Connected = false;
                store.Reset();
                await Task.Delay(ReconnectDelay, ct);
                continue;
            }

            if (!store.Connected) log.LogInformation("Mit AC verbunden");
            store.Connected = true;

            try
            {
                if (DateTime.UtcNow - lastStaticRead > StaticRefresh)
                {
                    lastStaticRead = DateTime.UtcNow;
                    var st = source.ReadStatic();
                    var changed = store.SetSession(st is { } s && !string.IsNullOrEmpty(s.CarModel) ? AcMapper.ToSession(s) : null);
                    if (changed) { miniSectors.Reset(); field.Reset(); }   // anderes Auto / andere Strecke → neu
                }

                if (source.ReadFrame() is { } raw && raw.Physics.PacketId != lastPacket)
                {
                    lastPacket = raw.Physics.PacketId;
                    lastChange = DateTime.UtcNow;
                    field.Update(fieldSource.Read(), clock.Elapsed.TotalMilliseconds);
                    if (field.Active != fieldWasActive)
                    {
                        fieldWasActive = field.Active;
                        if (field.Active) log.LogInformation("Feld-Modus: {Count} andere Autos", field.OpponentCount);
                        else log.LogInformation("Solo-Modus");
                    }
                    var mini = miniSectors.Update(raw);
                    store.Publish(AcMapper.ToFrame(raw, ++seq, mini));
                }
                else if (DateTime.UtcNow - lastChange > StaleAfter)
                {
                    // Keine neuen Pakete (Pause, Menü oder AC beendet). Handles schließen:
                    // Läuft AC noch, verbinden wir sofort wieder; sonst ist die Map weg.
                    lastChange = DateTime.UtcNow;
                    source.Dispose();
                    continue;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Lesen fehlgeschlagen, verbinde neu");
                source.Dispose();
                store.Connected = false;
            }

            await timer.WaitForNextTickAsync(ct);
        }
    }
}
