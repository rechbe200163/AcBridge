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
    private PendingLap? _pending;

    public void StartSession(SessionInfo? session, bool cloudEnabled)
    {
        _session = session;
        _enabled = cloudEnabled && session is not null;
        _sessionId = Guid.NewGuid();
        _startedAt = DateTimeOffset.Now;
        _lapCount = 0;
        _bestLapMs = null;
        _recording = false;
        _pending = null;
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
            if (_pending is not null) Enqueue(_pending, sectors);   // falls die vorige noch wartet
            _pending = _recording ? Freeze(EstimateLapMs(time, now), now, g) : null;
            Clear();
            _recording = true;
            _valid = true;
        }
        _prevLaps = g.CompletedLaps;
        _prevTime = time;
        _prevClock = now;

        // Abgeschlossene Runde wartet kurz, bis AC Rundenzeit und Sektoren nachgezogen hat
        if (_pending is { } pend && (now >= pend.ReadyAtMs || SectorsFit(pend, sectors) && g.ILastTime != pend.LastTimeBefore && now >= pend.MinReadyMs))
        {
            if (g.ILastTime > 0 && g.ILastTime != pend.LastTimeBefore && Math.Abs(g.ILastTime - pend.LapMs) < 300)
                pend = pend with { LapMs = g.ILastTime };              // exakte Zeit von AC
            Enqueue(pend, sectors);
            _pending = null;
        }

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

    /// <summary>Fertige Runde, die noch auf AC's Rundenzeit / Sektoren wartet.</summary>
    private sealed record PendingLap(
        int LapMs, bool Valid, string? Compound, AcSessionType SessionType, int LastTimeBefore,
        double MinReadyMs, double ReadyAtMs, Dictionary<string, object> Channels, int Points);

    /// <summary>
    /// Runde an der Linie einfrieren. Nur vollständige Runden: von der Linie bis zur Linie,
    /// Spline-Position deckt die ganze Runde ab. Restart mitten in der Runde oder die Anfahrt
    /// im Hotlap-Modus (Start kurz vor der Linie) fallen hier raus.
    /// </summary>
    private PendingLap? Freeze(int lapMs, double now, AcGraphics g)
    {
        if (_session is null || lapMs <= 0 || _distM.Count < 50) return Discard("zu kurz");
        if (_s[0] is > 0.05f and < 0.97f) return Discard($"Start bei s={_s[0]:0.00}, nicht an der Linie");
        var buckets = new bool[20];
        foreach (var s in _s) buckets[Math.Clamp((int)(s * 20), 0, 19)] = true;
        if (buckets.Any(b => !b)) return Discard("Runde unvollständig (Restart?)");

        return new PendingLap(lapMs, _valid, g.TyreCompound, g.Session, g.ILastTime,
            MinReadyMs: now + 1600,   // SectorTracker korrigiert den letzten Sektor bis 1,5 s nach der Linie
            ReadyAtMs: now + 3000,    // spätestens dann hochladen, auch ohne Sektoren
            new Dictionary<string, object>
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
            },
            _distM.Count);

        PendingLap? Discard(string why)
        {
            log.LogInformation("Runde nicht hochgeladen: {Why}", why);
            return null;
        }
    }

    /// <summary>Sektoren gehören zu dieser Runde, wenn alle da sind und ihre Summe zur Rundenzeit passt.</summary>
    private static bool SectorsFit(PendingLap lap, SectorData sectors) =>
        sectors.LastLap.Length > 0 && sectors.LastLap.All(v => v is not null)
        && Math.Abs(sectors.LastLap.Sum(v => v!.Value) - lap.LapMs) < 300;

    private void Enqueue(PendingLap lap, SectorData sectors)
    {
        var sectorMs = SectorsFit(lap, sectors) ? sectors.LastLap.Select(v => v!.Value).ToArray() : [];
        var lapMs = sectorMs.Length > 0 && Math.Abs(sectorMs.Sum() - lap.LapMs) < 50 ? sectorMs.Sum() : lap.LapMs;

        _lapCount++;
        if (lap.Valid && (_bestLapMs is null || lapMs < _bestLapMs)) _bestLapMs = lapMs;

        var lapId = Guid.NewGuid();
        outbox.Enqueue(new UploadJob(
            Kind: "lap",
            Session: new SessionRow(_sessionId, _session!.Track, _session.TrackConfiguration, _session.Car,
                lap.SessionType.ToString().ToLowerInvariant(), _startedAt, _bestLapMs, _lapCount),
            Lap: new LapRow(lapId, _sessionId, _lapCount, lapMs, sectorMs, lap.Valid, lap.Compound, DateTimeOffset.Now),
            Trace: new LapTraceRow(lapId, SpacingM, lap.Channels)));

        log.LogInformation("Runde {Lap} aufgezeichnet: {Ms} ms, {Count} Punkte, {Valid}, Sektoren {Sectors}",
            _lapCount, lapMs, lap.Points, lap.Valid ? "gültig" : "ungültig", sectorMs.Length > 0 ? "ok" : "fehlen");
    }

    /// <summary>Rundenzeit aus der Rundenuhr geschätzt; wird später durch AC's iLastTime ersetzt.</summary>
    private int EstimateLapMs(int newLapTime, double now) =>
        _prevTime + (int)Math.Max(0, (now - _prevClock) - newLapTime);

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
