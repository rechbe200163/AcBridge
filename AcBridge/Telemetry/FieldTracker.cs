namespace AcBridge.Telemetry;

/// <summary>
/// Misst die Minisektoren aller ANDEREN Autos (Daten von der In-Game-App) und merkt sich
/// den schnellsten Wert je Minisektor im ganzen Feld. Der MiniSectorTracker des Spielers
/// fragt hier nach, um "lila = Feld-Bestwert" zu vergeben.
///
/// Gleiche Methode wie beim Spieler: Grenzen über die Streckenposition erkennen,
/// Zeit zwischen zwei Frames linear interpolieren.
/// </summary>
public sealed class FieldTracker(int count)
{
    private const double LineDebounceMs = 3000;
    private const double ActiveTimeoutMs = 2000;

    private sealed class CarState(int count)
    {
        public double PrevPos = double.NaN;
        public double PrevTime;
        public double PrevClockMs = double.NaN;
        public int PrevLaps = -1;
        public double LastLineClockMs = double.NegativeInfinity;
        public bool AwaitWrap;
        public bool Valid;                          // Runde an der Linie begonnen, nicht in der Boxengasse
        public double[] Crossings = NewCrossings(count);
    }

    private readonly int _count = Math.Clamp(count, 3, 500);
    private readonly Dictionary<int, CarState> _cars = new();
    private double[] _best = [];
    private double _lastOpponentSeenMs = double.NegativeInfinity;

    /// <summary>Feld-Modus aktiv: es fahren gerade andere Autos und die App liefert Daten.</summary>
    public bool Active { get; private set; }

    /// <summary>Anzahl verbundener anderer Autos.</summary>
    public int OpponentCount { get; private set; }

    /// <summary>Schnellster Wert des Feldes (ohne Spieler) für Minisektor i, NaN wenn unbekannt.</summary>
    public double Best(int i) => _best.Length == 0 ? double.NaN : _best[i];

    public void Reset()
    {
        _cars.Clear();
        _best = Enumerable.Repeat(double.NaN, _count).ToArray();
        Active = false;
        OpponentCount = 0;
        _lastOpponentSeenMs = double.NegativeInfinity;
    }

    /// <param name="cars">Daten aller Autos, null = gerade keine Daten</param>
    /// <param name="nowMs">Echtzeit in ms</param>
    public void Update(IReadOnlyList<FieldCar>? cars, double nowMs)
    {
        if (_best.Length == 0) Reset();

        var opponents = 0;
        if (cars is not null)
        {
            foreach (var car in cars)
            {
                if (car.CarId == 0) continue;               // Spieler macht der MiniSectorTracker
                if (!car.Connected) { _cars.Remove(car.CarId); continue; }
                opponents++;
                UpdateCar(car, nowMs);
            }
        }

        OpponentCount = opponents;
        if (opponents > 0) _lastOpponentSeenMs = nowMs;
        // kurze Aussetzer (halb geschriebener Frame) nicht sofort als "Solo" werten
        Active = nowMs - _lastOpponentSeenMs < ActiveTimeoutMs;
    }

    private void UpdateCar(FieldCar car, double now)
    {
        if (!_cars.TryGetValue(car.CarId, out var s))
            _cars[car.CarId] = s = new CarState(_count);

        double pos = car.SplinePosition;
        double time = car.LapTimeMs;

        // Rundenzähler zurück → Session neu / Auto zurückgesetzt
        if (s.PrevLaps >= 0 && car.LapCount < s.PrevLaps)
            _cars[car.CarId] = s = new CarState(_count);

        var lapCounted = s.PrevLaps >= 0 && car.LapCount > s.PrevLaps;
        var clockReset = s.PrevLaps >= 0 && s.PrevTime > 1000 && time < s.PrevTime - 1000;

        if ((lapCounted || clockReset) && now - s.LastLineClockMs > LineDebounceMs)
        {
            s.LastLineClockMs = now;

            // letzten Minisektor mit geschätztem Rundenende abschließen
            if (s.Valid && !double.IsNaN(s.Crossings[_count - 1]) && !double.IsNaN(s.PrevClockMs))
            {
                var lapEnd = s.PrevTime + Math.Max(0, (now - s.PrevClockMs) - time);
                s.Crossings[_count] = lapEnd;
                Report(_count - 1, lapEnd - s.Crossings[_count - 1]);
            }

            s.Crossings = NewCrossings(_count);
            s.Valid = pos < 0.05 || pos > 0.95;
            s.AwaitWrap = pos > 0.5;
            s.PrevPos = double.NaN;
        }
        s.PrevLaps = car.LapCount;

        if (s.AwaitWrap && pos < 0.5) { s.AwaitWrap = false; s.PrevPos = double.NaN; }
        if (car.InPitline) s.Valid = false;

        if (!s.AwaitWrap && !double.IsNaN(s.PrevPos) && pos > s.PrevPos && pos - s.PrevPos < 0.5)
        {
            var first = (int)Math.Floor(s.PrevPos * _count) + 1;
            var last = (int)Math.Floor(pos * _count);
            for (var k = Math.Max(first, 1); k <= Math.Min(last, _count - 1); k++)
            {
                var f = ((double)k / _count - s.PrevPos) / (pos - s.PrevPos);
                s.Crossings[k] = s.PrevTime + f * (time - s.PrevTime);
                if (s.Valid && !double.IsNaN(s.Crossings[k - 1]))
                    Report(k - 1, s.Crossings[k] - s.Crossings[k - 1]);
            }
        }

        s.PrevPos = pos;
        s.PrevTime = time;
        s.PrevClockMs = now;
    }

    private void Report(int i, double t)
    {
        if (t <= 0) return;
        if (double.IsNaN(_best[i]) || t < _best[i]) _best[i] = t;
    }

    private static double[] NewCrossings(int count)
    {
        var c = Enumerable.Repeat(double.NaN, count + 1).ToArray();
        c[0] = 0;
        return c;
    }
}
