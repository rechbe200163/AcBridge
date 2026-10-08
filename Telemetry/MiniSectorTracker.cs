using System.Diagnostics;
using AcBridge.Models;

namespace AcBridge.Telemetry;

/// <summary>
/// Teilt die Runde in N gleich lange Minisektoren (über trackPosition 0…1) und misst die Zeit
/// an jeder Grenze. Daraus entstehen Farben pro Minisektor (pro Session).
/// Das Live-Delta kommt direkt von AC (lap.acDeltaMs), nicht von hier.
///
/// Zwei Modi, automatisch gewählt (Field.Active):
///   solo  (allein auf der Strecke)
///     purple  = neuer eigener Bestwert in diesem Minisektor
///     green   = schneller als in der eigenen besten Runde, aber kein Bestwert
///     yellow  = langsamer
///   field (andere Autos fahren, Daten von der In-Game-App) – F1-Logik
///     purple  = schnellster Wert des ganzen Feldes
///     green   = eigener Bestwert
///     yellow  = langsamer als eigener Bestwert
///   neutral = noch kein Vergleichswert
///
/// Läuft im Poller mit voller Poll-Rate. Nicht threadsicher, braucht es auch nicht.
/// </summary>
public sealed class MiniSectorTracker(int count)
{
    public const string Pending = "pending";
    public const string Purple = "purple";
    public const string Green = "green";
    public const string Yellow = "yellow";
    public const string Neutral = "neutral";   // gefahren, aber noch kein Vergleichswert

    private readonly int _count = Math.Clamp(count, 3, 500);

    // Aktuelle Runde: Zeit (ms) an Grenze k, k = 0…count. NaN = noch nicht erreicht.
    private double[] _crossings = [];
    private string[] _results = [];
    private string[] _lastLapResults = [];
    private bool _lapValid;

    // Referenz: beste gültige Runde (Grenzzeiten) und Bestzeit je Minisektor
    private double[]? _bestLap;
    private double[] _bestSector = [];

    private double _prevPos = double.NaN;
    private double _prevTime;
    private double _prevClockMs = double.NaN;
    private int _prevLaps = -1;
    private double _lastLineClockMs = double.NegativeInfinity;
    private const double LineDebounceMs = 3000;
    private bool _awaitWrap;   // Runde begann kurz VOR Spline-Null → erst nach dem Umbruch 1→0 Grenzen zählen

    public int Count => _count;

    /// <summary>Daten der anderen Autos. Wenn aktiv → Feld-Modus (F1-Logik).</summary>
    public FieldTracker? Field { get; set; }

    private bool FieldMode => Field is { Active: true };

    public void Reset()
    {
        _bestLap = null;
        _bestSector = Enumerable.Repeat(double.NaN, _count).ToArray();
        _prevPos = double.NaN;
        _prevLaps = -1;
        _prevClockMs = double.NaN;
        _awaitWrap = false;
        _lastLineClockMs = double.NegativeInfinity;
        _lastLapResults = [];
        StartLap(startedAtLine: false);
    }

    /// <param name="clockMs">Echtzeit in ms (nur für Tests, sonst Stopwatch)</param>
    public MiniSectorData Update(RawSnapshot s, double? clockMs = null)
    {
        if (_results.Length == 0) Reset();

        var g = s.Graphics;
        var pos = (double)g.NormalizedCarPosition;
        var time = (double)g.ICurrentTime;
        var laps = g.CompletedLaps;
        var now = clockMs ?? Stopwatch.GetElapsedTime(StartTicks).TotalMilliseconds;

        if (g.Status != AcStatus.Live)
            return Snapshot(pos, time);

        // Rundenzähler zurückgesprungen → neue Session / Restart
        if (_prevLaps >= 0 && laps < _prevLaps) Reset();

        // Ziellinie überfahren? Zwei Signale, je nach Modus kommt eins davon zuerst oder nur eins:
        //  - completedLaps zählt hoch (normale Runde)
        //  - Rundenuhr springt zurück (z. B. Hotlap: Anlauf → erste fliegende Runde, ohne dass laps hochzählt)
        var lapCounted = _prevLaps >= 0 && laps > _prevLaps;
        var clockReset = _prevLaps >= 0 && _prevTime > 1000 && time < _prevTime - 1000;

        // Beide Signale gehören zur selben Linie, wenn sie kurz hintereinander kommen → nur einmal auswerten
        if ((lapCounted || clockReset) && now - _lastLineClockMs > LineDebounceMs)
        {
            _lastLineClockMs = now;

            // Abschließen nur, wenn das eine echte Runde war. Der Hotlap-Anlauf ist ungültig
            // und zählt nicht hoch → einfach verwerfen.
            if (lapCounted || _lapValid)
                FinishLap(EstimateLapTime(g.ILastTime, time, now));

            // AC's Spline-Null liegt nicht exakt auf der Ziellinie → beide Seiten zulassen
            StartLap(startedAtLine: pos < 0.05 || pos > 0.95);
            _awaitWrap = pos > 0.5;
            _prevPos = double.NaN; // keine Interpolation über die Ziellinie
        }
        _prevLaps = laps;

        if (_awaitWrap && pos < 0.5) { _awaitWrap = false; _prevPos = double.NaN; }

        // AC: mehr als 2 Räder neben der Strecke = Cut → Runde ungültig
        if (s.Physics.NumberOfTyresOut > 2) _lapValid = false;

        // Grenzen erkennen, die seit dem letzten Frame überfahren wurden
        if (!_awaitWrap && !double.IsNaN(_prevPos) && pos > _prevPos && pos - _prevPos < 0.5)
        {
            var first = (int)Math.Floor(_prevPos * _count) + 1;
            var last = (int)Math.Floor(pos * _count);
            for (var k = Math.Max(first, 1); k <= Math.Min(last, _count - 1); k++)
            {
                var boundary = (double)k / _count;
                var f = (boundary - _prevPos) / (pos - _prevPos);   // lineare Interpolation
                _crossings[k] = _prevTime + f * (time - _prevTime);
                CloseSector(k - 1);
            }
        }

        _prevPos = pos;
        _prevTime = time;
        _prevClockMs = now;
        return Snapshot(pos, time);
    }

    private static readonly long StartTicks = Stopwatch.GetTimestamp();

    /// <summary>
    /// Rundenzeit der gerade beendeten Runde. AC aktualisiert iLastTime nicht immer im selben
    /// Frame wie completedLaps → zusätzlich aus dem letzten Frame schätzen:
    /// Rundenende ≈ letzte Rundenzeit + (Echtzeit seit letztem Frame − Zeit, die schon in der neuen Runde vergangen ist)
    /// </summary>
    private double EstimateLapTime(int acLastLapMs, double newLapTime, double now)
    {
        if (double.IsNaN(_prevClockMs) || _prevTime <= 0) return acLastLapMs;
        var estimate = _prevTime + Math.Max(0, (now - _prevClockMs) - newLapTime);
        // iLastTime schon aktuell und plausibel → die exakte Zahl von AC nehmen
        return acLastLapMs > 0 && Math.Abs(acLastLapMs - estimate) < 250 ? acLastLapMs : estimate;
    }

    private void StartLap(bool startedAtLine)
    {
        _crossings = Enumerable.Repeat(double.NaN, _count + 1).ToArray();
        _crossings[0] = 0;
        _results = Enumerable.Repeat(Pending, _count).ToArray();
        _lapValid = startedAtLine; // Out-Lap oder Einstieg mitten in der Runde zählt nicht
    }

    private void FinishLap(double lapMs)
    {
        if (lapMs <= 0) { _lastLapResults = []; return; }
        _crossings[_count] = lapMs;
        CloseSector(_count - 1);
        _lastLapResults = (string[])_results.Clone();

        var complete = _crossings.All(c => !double.IsNaN(c));
        if (!_lapValid || !complete) return;

        // Bestwerte je Minisektor fortschreiben
        for (var i = 0; i < _count; i++)
        {
            var t = _crossings[i + 1] - _crossings[i];
            if (double.IsNaN(_bestSector[i]) || t < _bestSector[i]) _bestSector[i] = t;
        }

        if (_bestLap is null || lapMs < _bestLap[_count])
            _bestLap = (double[])_crossings.Clone();
    }

    /// <summary>Färbt Minisektor i, sobald seine Endzeit bekannt ist.</summary>
    private void CloseSector(int i)
    {
        if (i < 0 || double.IsNaN(_crossings[i]) || double.IsNaN(_crossings[i + 1])) return;
        var t = _crossings[i + 1] - _crossings[i];

        var own = _bestSector[i];

        if (FieldMode)
        {
            // F1-Logik: lila = Feld-Bestwert (inkl. eigener Bestwerte), grün = eigener Bestwert
            var field = Field!.Best(i);
            var overall = double.IsNaN(field) ? own : double.IsNaN(own) ? field : Math.Min(field, own);

            if (double.IsNaN(overall))
                _results[i] = Neutral;
            else if (t < overall)
                _results[i] = Purple;
            else if (double.IsNaN(own) || t < own)
                _results[i] = Green;                  // noch kein eigener Wert → jeder ist persönliche Bestzeit
            else
                _results[i] = Yellow;
            return;
        }

        // Solo
        if (double.IsNaN(own))
            _results[i] = Neutral;                    // noch keine gültige Runde → nichts zu vergleichen
        else if (t < own)
            _results[i] = Purple;
        else if (_bestLap is not null && t < _bestLap[i + 1] - _bestLap[i])
            _results[i] = Green;
        else
            _results[i] = Yellow;
    }

    private MiniSectorData Snapshot(double pos, double time)
    {
        var current = Math.Clamp((int)Math.Floor(pos * _count), 0, _count - 1);
        return new MiniSectorData(
            Count: _count,
            Current: current,
            Results: (string[])_results.Clone(),
            LastLapResults: _lastLapResults,
            LapValid: _lapValid,
            Mode: FieldMode ? "field" : "solo",
            FieldCars: Field?.OpponentCount ?? 0);
    }
}
