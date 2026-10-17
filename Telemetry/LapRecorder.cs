using System.Diagnostics;
using AcBridge.Cloud;
using AcBridge.Models;

namespace AcBridge.Telemetry;

/// <summary>
/// Zeichnet jede Runde als Messspur auf (alle ~3 m ein Punkt) und legt sie in die Upload-Warteschlange.
///
/// Aufgezeichnet wird nur, was an der Linie begonnen hat (Einstieg mitten in der Runde → verworfen).
/// Hochgeladen werden auch ungültige Runden (valid = false), damit man sich auch Fehler anschauen kann.
/// Abgetastet nach gefahrener Strecke, nicht nach Zeit → zwei Runden lassen sich direkt übereinanderlegen.
/// </summary>
public sealed class LapRecorder(CloudOutbox outbox, ILogger log)
{
    private const float SpacingM = 3f;
    private const double LineDebounceMs = 3000;
    private const float TeleportM = 60f;        // Sprung zwischen zwei Frames → Back to Pits / Restart

    // Session
    private Guid _sessionId;
    private SessionInfo? _session;
    private DateTimeOffset _startedAt;
    private int _lapCount;
    private int? _bestLapMs;
    private bool _enabled;

    // laufende Runde
    private bool _recording;
    private bool _valid;
    private float _dist, _nextSample;
    private float? _lastX, _lastZ;
    private int _prevLaps = -1, _prevTime;
    private double _prevClock = double.NaN, _lastLineMs = double.NegativeInfinity;
    private readonly List<float> _distM = [], _s = [], _speed = [], _throttle = [], _brake = [], _steer = [], _x = [], _z = [];
    private readonly List<int> _timeMs = [], _gear = [];

    public void StartSession(SessionInfo? session, bool cloudEnabled)
    {
        _session = session;
        _enabled = cloudEnabled && session is not null;
        _sessionId = Guid.NewGuid();
        _startedAt = DateTimeOffset.Now;
        _lapCount = 0;
        _bestLapMs = null;
        _recording = false;
        _prevLaps = -1;
        _lastLineMs = double.NegativeInfinity;
        Clear();
    }

    public void Update(RawSnapshot raw, SectorData sectors)
    {
        if (!_enabled) return;
        var g = raw.Graphics;
        var p = raw.Physics;
        if (g.Status != AcStatus.Live) return;

        var now = Stopwatch.GetElapsedTime(StartTicks).TotalMilliseconds;
        var time = g.ICurrentTime;

        // Ziellinie: Rundenzähler zählt hoch ODER Rundenuhr springt zurück (Hotlap)
        var lapCounted = _prevLaps >= 0 && g.CompletedLaps > _prevLaps;
        var clockReset = _prevLaps >= 0 && _prevTime > 1000 && time < _prevTime - 1000;
        if ((lapCounted || clockReset) && now - _lastLineMs > LineDebounceMs)
        {
            _lastLineMs = now;
            if (_recording) Finish(EstimateLapMs(time, now, sectors), sectors, g.TyreCompound, g.Session);
            Clear();
            _recording = true;
            _valid = true;
        }
        _prevLaps = g.CompletedLaps;
        _prevTime = time;
        _prevClock = now;

        if (!_recording) return;
        if (p.NumberOfTyresOut > 2 || g.IsInPit != 0 || g.IsInPitLane != 0) _valid = false;

        var c = g.CarCoordinates;
        if (c is not { Length: >= 3 }) return;
        float x = c[0], z = c[2];

        if (_lastX is { } lx && _lastZ is { } lz)
        {
            var d = MathF.Sqrt((x - lx) * (x - lx) + (z - lz) * (z - lz));
            if (d > TeleportM) { _recording = false; Clear(); return; }   // Runde verwerfen
            _dist += d;
        }
        _lastX = x;
        _lastZ = z;

        if (_dist < _nextSample) return;
        _nextSample = _dist + SpacingM;

        _distM.Add(MathF.Round(_dist, 1));
        _s.Add(MathF.Round(g.NormalizedCarPosition, 5));
        _timeMs.Add(time);
        _speed.Add(MathF.Round(p.SpeedKmh, 1));
        _throttle.Add(MathF.Round(p.Gas, 3));
        _brake.Add(MathF.Round(p.Brake, 3));
        _gear.Add(p.Gear - 1);                       // AC: 0 = R, 1 = N → bei uns -1 / 0
        _steer.Add(MathF.Round(p.SteerAngle, 3));
        _x.Add(MathF.Round(x, 1));
        _z.Add(MathF.Round(z, 1));
    }

    private void Finish(int lapMs, SectorData sectors, string? compound, AcSessionType sessionType)
    {
        if (_session is null || lapMs <= 0 || _distM.Count < 50) return;

        _lapCount++;
        if (_valid && (_bestLapMs is null || lapMs < _bestLapMs)) _bestLapMs = lapMs;

        var lapId = Guid.NewGuid();
        // Sektoren nur übernehmen, wenn sie zu dieser Runde passen (Summe ≈ Rundenzeit)
        var sectorMs = sectors.LastLap.Length > 0 && sectors.LastLap.All(v => v is not null)
                       && Math.Abs(sectors.LastLap.Sum(v => v!.Value) - lapMs) < 300
            ? sectors.LastLap.Select(v => v!.Value).ToArray()
            : [];

        outbox.Enqueue(new UploadJob(
            Kind: "lap",
            Session: new SessionRow(_sessionId, _session.Track, _session.TrackConfiguration, _session.Car,
                sessionType.ToString().ToLowerInvariant(), _startedAt, _bestLapMs, _lapCount),
            Lap: new LapRow(lapId, _sessionId, _lapCount, lapMs, sectorMs, _valid, compound, DateTimeOffset.Now),
            Trace: new LapTraceRow(lapId, SpacingM, new Dictionary<string, object>
            {
                ["distM"] = _distM.ToArray(),
                ["s"] = _s.ToArray(),
                ["timeMs"] = _timeMs.ToArray(),
                ["speedKmh"] = _speed.ToArray(),
                ["throttle"] = _throttle.ToArray(),
                ["brake"] = _brake.ToArray(),
                ["gear"] = _gear.ToArray(),
                ["steer"] = _steer.ToArray(),
                ["x"] = _x.ToArray(),
                ["z"] = _z.ToArray(),
            })));

        log.LogInformation("Runde {Lap} aufgezeichnet: {Ms} ms, {Count} Punkte, {Valid}",
            _lapCount, lapMs, _distM.Count, _valid ? "gültig" : "ungültig");
    }

    /// <summary>Sektorsumme, wenn vollständig (exakt von AC), sonst aus der Rundenuhr geschätzt.</summary>
    private int EstimateLapMs(int newLapTime, double now, SectorData sectors)
    {
        var estimate = _prevTime + (int)Math.Max(0, (now - _prevClock) - newLapTime);
        if (sectors.LastLap.Length > 0 && sectors.LastLap.All(v => v is not null))
        {
            var sum = sectors.LastLap.Sum(v => v!.Value);
            if (Math.Abs(sum - estimate) < 300) return sum;
        }
        return estimate;
    }

    private void Clear()
    {
        _dist = 0;
        _nextSample = 0;
        _lastX = _lastZ = null;
        _distM.Clear(); _s.Clear(); _timeMs.Clear(); _speed.Clear(); _throttle.Clear();
        _brake.Clear(); _gear.Clear(); _steer.Clear(); _x.Clear(); _z.Clear();
    }

    private static readonly long StartTicks = Stopwatch.GetTimestamp();
}
