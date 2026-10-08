using System.Diagnostics;
using AcBridge.Models;

namespace AcBridge.Telemetry;

/// <summary>
/// Sektorzeiten (die echten AC-Sektoren, meist 3). AC liefert nur den aktuellen Sektor-Index
/// und die Zeit des zuletzt beendeten Sektors → die Bridge sammelt daraus:
///   - Sektoren der laufenden Runde (sobald einer fertig ist)
///   - laufende Zeit im aktuellen Sektor
///   - komplette letzte Runde
///   - beste Zeit je Sektor (Session) und Sektoren der besten Runde
///
/// Farben wie bei den Minisektoren (solo):
///   purple = neue Session-Bestzeit in diesem Sektor
///   green  = schneller als dieser Sektor in der besten Runde
///   yellow = langsamer
///   neutral = noch kein Vergleich, pending = noch nicht gefahren
/// </summary>
public sealed class SectorTracker(int count)
{
    private readonly int _count = Math.Clamp(count, 1, 20);

    private int?[] _current = [];
    private string[] _currentResults = [];
    private int?[] _lastLap = [];
    private string[] _lastLapResults = [];
    private int?[] _best = [];
    private int?[] _bestLap = [];
    private int? _bestLapMs;

    private int _prevIndex = -1;
    private int _prevLaps = -1;
    private int _prevTime;
    private int _prevAcSectorMs;
    private int _sectorStartMs;   // Rundenzeit, bei der der aktuelle Sektor begonnen hat
    private double _prevClockMs = double.NaN;
    private bool _sawSectorStart;   // Start des aktuellen Sektors miterlebt? (sonst kein gültiger Wert)
    private bool _lapValid;
    private bool _lapStartSeen;

    // AC schreibt lastSectorTime / iLastTime manchmal ein paar Frames später → nachträglich korrigieren
    private int _pendingSector = -1;
    private int _pendingAcValueBefore;
    private int _pendingLastLapBefore;
    private double _pendingUntilMs;

    public int Count => _count;

    /// <summary>Beste gültige Runde (ms), Fallback für Lap.BestLapMs, wenn AC 0 liefert.</summary>
    public int? BestLapMs => _bestLapMs;

    public void Reset()
    {
        _current = new int?[_count];
        _currentResults = Enumerable.Repeat(MiniSectorTracker.Pending, _count).ToArray();
        _lastLap = [];
        _lastLapResults = [];
        _best = new int?[_count];
        _bestLap = new int?[_count];
        _bestLapMs = null;
        _prevIndex = -1;
        _prevLaps = -1;
        _prevClockMs = double.NaN;
        _sawSectorStart = false;
        _lapValid = false;
        _lapStartSeen = false;
        _pendingSector = -1;
    }

    public SectorData Update(RawSnapshot s, double? clockMs = null)
    {
        if (_current.Length == 0) Reset();

        var g = s.Graphics;
        var index = Math.Clamp(g.CurrentSectorIndex, 0, _count - 1);
        var time = g.ICurrentTime;
        var now = clockMs ?? Stopwatch.GetElapsedTime(StartTicks).TotalMilliseconds;

        if (g.Status != AcStatus.Live) return Snapshot(time);

        // Neue Session / Restart
        if (_prevLaps >= 0 && g.CompletedLaps < _prevLaps) Reset();
        _prevLaps = g.CompletedLaps;

        if (s.Physics.NumberOfTyresOut > 2) _lapValid = false;

        ApplyPendingCorrection(g, now);

        // Erster Frame direkt nach der Linie (Rundenuhr fast 0) → S1 ist schon messbar
        if (_prevIndex < 0 && index == 0 && time is >= 0 and < 1000)
        {
            _sawSectorStart = true;
            _sectorStartMs = 0;
        }

        if (_prevIndex >= 0 && index != _prevIndex)
        {
            var finished = _prevIndex;
            var lapEnded = index == 0 && finished == _count - 1 && g.IsInPit == 0;
            var sequential = lapEnded || index == finished + 1;

            if (!sequential)
            {
                // Sprung (Back to Pits, Teleport, Restart) → laufende Runde verwerfen
                _current = new int?[_count];
                _currentResults = Enumerable.Repeat(MiniSectorTracker.Pending, _count).ToArray();
                _sawSectorStart = false;
                _lapStartSeen = false;
            }
            else
            {
                if (_sawSectorStart)
                {
                    int sectorMs;
                    if (lapEnded)
                    {
                        // Rundenende: Zeit des letzten Frames + Echtzeit bis zur Linie (Uhr springt auf ~0)
                        var lapMs = _prevTime + (int)Math.Max(0, (now - _prevClockMs) - time);
                        sectorMs = lapMs - _sectorStartMs;
                    }
                    else
                    {
                        // Mitten in der Runde: lastSectorTime nur, wenn AC sie in diesem Frame neu gesetzt hat
                        var fromClock = time - _sectorStartMs;
                        var acFresh = g.LastSectorTime != _prevAcSectorMs && Math.Abs(g.LastSectorTime - fromClock) < 250;
                        sectorMs = acFresh ? g.LastSectorTime : fromClock;
                    }
                    CloseSector(finished, sectorMs);

                    _pendingSector = finished;
                    _pendingAcValueBefore = g.LastSectorTime;
                    _pendingLastLapBefore = g.ILastTime;
                    _pendingUntilMs = now + 1500;
                }

                if (lapEnded) FinishLap();
                _sectorStartMs = lapEnded ? 0
                    : _sawSectorStart && _current[finished] is { } done ? _sectorStartMs + done
                    : time;
                _sawSectorStart = true;   // ab jetzt kennen wir den Start dieses Sektors
            }
        }

        _prevAcSectorMs = g.LastSectorTime;
        _prevIndex = index;
        _prevTime = time;
        _prevClockMs = now;
        return Snapshot(time);
    }

    private static readonly long StartTicks = Stopwatch.GetTimestamp();

    /// <summary>Exakte AC-Werte übernehmen, sobald sie da sind (≤ 1,5 s nach dem Sektorwechsel).</summary>
    private void ApplyPendingCorrection(AcGraphics g, double now)
    {
        if (_pendingSector < 0) return;
        if (now > _pendingUntilMs) { _pendingSector = -1; return; }

        int? exact = null;
        var isLast = _pendingSector == _count - 1;
        if (isLast && g.ILastTime > 0 && g.ILastTime != _pendingLastLapBefore)
        {
            var known = Enumerable.Range(0, _count - 1).Select(LastLapOrCurrent).ToArray();
            if (known.All(k => k is not null)) exact = g.ILastTime - known.Sum(k => k!.Value);
        }
        else if (!isLast && g.LastSectorTime > 0 && g.LastSectorTime != _pendingAcValueBefore)
            exact = g.LastSectorTime;

        if (exact is not { } ms) return;
        var current = isLast ? (_lastLap.Length == _count ? _lastLap[_pendingSector] : null) : _current[_pendingSector];
        if (current is { } c && Math.Abs(c - ms) < 250 && c != ms)
        {
            if (isLast && _lastLap.Length == _count) _lastLap[_pendingSector] = ms;
            else { _current[_pendingSector] = ms; _sectorStartMs += ms - c; }
            if (_best[_pendingSector] == c) _best[_pendingSector] = ms;          // Schätzung war Bestwert → exakt machen
            if (isLast && _bestLap[_pendingSector] == c && _bestLapMs is { } bl)
            {
                _bestLap[_pendingSector] = ms;
                _bestLapMs = bl - c + ms;
            }
        }
        _pendingSector = -1;
    }

    // letzter Sektor wurde schon in _lastLap verschoben → Werte der Runde dort lesen
    private int? LastLapOrCurrent(int i) => _lastLap.Length == _count ? _lastLap[i] : _current[i];

    private void CloseSector(int i, int ms)
    {
        if (ms <= 0) return;
        _current[i] = ms;

        var best = _best[i];
        var bestLap = _bestLap[i];
        _currentResults[i] =
            best is null ? MiniSectorTracker.Neutral :
            ms < best ? MiniSectorTracker.Purple :
            bestLap is { } bl && ms < bl ? MiniSectorTracker.Green :
            MiniSectorTracker.Yellow;

        // Bestwerte nur aus sauberen Runden (kein Cut, Rundenstart gesehen)
        if (_lapValid && _lapStartSeen && (best is null || ms < best)) _best[i] = ms;
    }

    private void FinishLap()
    {
        var complete = _current.All(t => t is not null);
        _lastLap = (int?[])_current.Clone();
        _lastLapResults = (string[])_currentResults.Clone();

        if (complete && _lapValid && _lapStartSeen)
        {
            var lapMs = _current.Sum(t => t!.Value);
            if (_bestLapMs is null || lapMs < _bestLapMs)
            {
                _bestLapMs = lapMs;
                _bestLap = (int?[])_current.Clone();
            }
        }

        _current = new int?[_count];
        _currentResults = Enumerable.Repeat(MiniSectorTracker.Pending, _count).ToArray();
        _lapValid = true;
        _lapStartSeen = true;
    }

    private SectorData Snapshot(int lapTimeMs)
    {
        var index = Math.Max(0, _prevIndex);
        int? running = _sawSectorStart ? Math.Max(0, lapTimeMs - _sectorStartMs) : null;

        int? theoretical = _best.All(b => b is not null) ? _best.Sum(b => b!.Value) : null;

        return new SectorData(
            Count: _count,
            Current: index,
            CurrentRunningMs: running,
            CurrentLap: (int?[])_current.Clone(),
            CurrentResults: (string[])_currentResults.Clone(),
            LastLap: _lastLap,
            LastLapResults: _lastLapResults,
            Best: (int?[])_best.Clone(),
            BestLap: (int?[])_bestLap.Clone(),
            TheoreticalBestMs: theoretical);
    }
}
