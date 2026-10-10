using System.Diagnostics;
using System.Text.Json;
using AcBridge.Models;

namespace AcBridge.Telemetry;

/// <summary>Ein Punkt des Streckenumrisses: Spline-Position (0…1) und Weltkoordinaten x/z in Metern.</summary>
public record TrackPoint(float S, float X, float Z);

/// <summary>Aufgezeichneter Streckenumriss, wird als JSON-Datei pro Strecke/Layout gespeichert.</summary>
public record TrackOutline(
    string Track,
    string Layout,
    string Car,
    int LapMs,
    DateTimeOffset RecordedAt,
    IReadOnlyList<TrackPoint> Points);

/// <summary>
/// Speichert/lädt Umrisse unter %LOCALAPPDATA%\AcBridge\tracks (Windows) bzw. ~/.local/share/AcBridge/tracks.
/// Eine Datei pro Strecke + Layout. Zum Neu-Aufzeichnen einfach die Datei löschen.
/// </summary>
public sealed class TrackOutlineStore
{
    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AcBridge", "tracks");

    public string Directory => _dir;

    public bool Exists(string track, string layout) => File.Exists(PathFor(track, layout));

    public TrackOutline? Load(string track, string layout)
    {
        var path = PathFor(track, layout);
        return File.Exists(path)
            ? JsonSerializer.Deserialize<TrackOutline>(File.ReadAllText(path), TelemetryWebSocket.Json)
            : null;
    }

    public void Save(TrackOutline outline)
    {
        System.IO.Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(outline.Track, outline.Layout), JsonSerializer.Serialize(outline, TelemetryWebSocket.Json));
    }

    private string PathFor(string track, string layout)
    {
        var name = string.IsNullOrWhiteSpace(layout) ? track : $"{track}__{layout}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(_dir, name + ".json");
    }
}

/// <summary>
/// Zeichnet während der Fahrt die eigene Position auf (alle ~5 m ein Punkt).
/// Eine gültige, vollständige Runde wird als Umriss gespeichert, aber nur, wenn es für diese Strecke noch keinen gibt.
///   gültig     = Rundenstart an der Linie gesehen, kein Cut (> 2 Räder neben der Strecke), nicht in der Box
///   vollständig = Spline-Position deckt die ganze Runde ab
/// Rundenwechsel wie beim MiniSectorTracker: completedLaps zählt hoch ODER die Rundenuhr springt zurück (Hotlap).
/// </summary>
public sealed class TrackRecorder(TrackOutlineStore store, ILogger log)
{
    private const float MinSpacingM = 5f;
    private const double LineDebounceMs = 3000;

    private readonly List<TrackPoint> _points = [];
    private bool _recording;      // Rundenstart gesehen → läuft
    private bool _valid;
    private int _prevLaps = -1;
    private int _prevTime;
    private double _lastLineMs = double.NegativeInfinity;
    private bool _done;           // für diese Strecke gibt es schon einen Umriss

    private string _track = "", _layout = "", _car = "";

    /// <summary>Bei neuer Session aufrufen.</summary>
    public void Reset(SessionInfo? session)
    {
        _track = session?.Track ?? "";
        _layout = session?.TrackConfiguration ?? "";
        _car = session?.Car ?? "";
        _done = _track.Length == 0 || store.Exists(_track, _layout);
        _points.Clear();
        _recording = false;
        _prevLaps = -1;
        _lastLineMs = double.NegativeInfinity;
    }

    public void Update(RawSnapshot s)
    {
        if (_done) return;
        var g = s.Graphics;
        if (g.Status != AcStatus.Live) return;

        var now = Stopwatch.GetElapsedTime(StartTicks).TotalMilliseconds;
        var lapCounted = _prevLaps >= 0 && g.CompletedLaps > _prevLaps;
        var clockReset = _prevLaps >= 0 && _prevTime > 1000 && g.ICurrentTime < _prevTime - 1000;
        _prevLaps = g.CompletedLaps;
        _prevTime = g.ICurrentTime;

        if ((lapCounted || clockReset) && now - _lastLineMs > LineDebounceMs)
        {
            _lastLineMs = now;
            if (_recording && _valid && TryFinish(g.ILastTime)) return;

            // neue Runde beginnt an der Linie
            _points.Clear();
            _recording = true;
            _valid = true;
        }

        if (!_recording) return;
        if (s.Physics.NumberOfTyresOut > 2 || g.IsInPit != 0 || g.IsInPitLane != 0) _valid = false;
        if (!_valid) return;

        var c = g.CarCoordinates;
        if (c is not { Length: >= 3 }) return;
        var p = new TrackPoint(g.NormalizedCarPosition, c[0], c[2]);

        if (_points.Count == 0 || Distance(_points[^1], p) >= MinSpacingM)
            _points.Add(p);
    }

    private bool TryFinish(int lapMs)
    {
        // Vollständig: Spline-Position hat (fast) alles abgedeckt → in 20 Abschnitte teilen, jeder muss Punkte haben
        var buckets = new bool[20];
        foreach (var p in _points) buckets[Math.Clamp((int)(p.S * 20), 0, 19)] = true;
        if (_points.Count < 50 || buckets.Any(b => !b))
        {
            log.LogInformation("Streckenumriss verworfen: Runde unvollständig ({Count} Punkte)", _points.Count);
            return false;
        }

        var outline = new TrackOutline(_track, _layout, _car, lapMs, DateTimeOffset.Now, [.. _points]);
        store.Save(outline);
        _done = true;
        log.LogInformation("Streckenumriss gespeichert: {Track} {Layout}, {Count} Punkte → {Dir}",
            _track, _layout, _points.Count, store.Directory);
        return true;
    }

    private static float Distance(TrackPoint a, TrackPoint b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static readonly long StartTicks = Stopwatch.GetTimestamp();
}
