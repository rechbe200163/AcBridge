namespace AcBridge.Telemetry;

/// <summary>Eine erkannte Kurve. Positionen als Spline-Position 0…1 (wie lap.trackPosition).</summary>
public record Corner(int Number, string Direction, float EntryS, float ApexS, float ExitS);

/// <summary>
/// Erkennt Kurven aus dem Streckenumriss über die Krümmung (wie stark sich die Fahrtrichtung pro Meter ändert).
///   Einlenken = Krümmung steigt über die Schwelle
///   Apex      = stärkste Krümmung
///   Ausgang   = Krümmung fällt wieder unter die Schwelle
/// AC selbst liefert keine Kurvendaten, darum wird das einmal pro Strecke berechnet.
///
/// Richtung: bezogen auf die Karte, wie die App sie zeichnet (x nach rechts, z nach unten).
/// Ist die Karte in der App gespiegelt, sind auch links/rechts vertauscht.
/// </summary>
public static class CornerDetector
{
    private const double SmoothWindowM = 20;      // Krümmung über ±20 m glätten (Messrauschen)
    private const double MinCurvature = 1 / 220.0; // ab Radius < 220 m gilt es als Kurve
    private const double MergeGapM = 35;          // gleiche Richtung, Lücke < 35 m → eine Kurve (Doppel-Apex)
    private const double MinTurnDeg = 20;         // weniger Richtungsänderung → Knick, keine Kurve

    public static List<Corner> Detect(IReadOnlyList<TrackPoint> pts)
    {
        var n = pts.Count;
        if (n < 20) return [];

        // Segmentlängen und Richtungswechsel je Punkt (geschlossene Runde)
        var len = new double[n];
        var heading = new double[n];
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % n];
            var dx = b.X - a.X;
            var dz = b.Z - a.Z;
            len[i] = Math.Max(0.01, Math.Sqrt(dx * dx + dz * dz));
            heading[i] = Math.Atan2(dz, dx);
        }
        var turn = new double[n];   // Richtungsänderung am Punkt i (rad)
        for (var i = 0; i < n; i++)
        {
            var d = heading[i] - heading[(i - 1 + n) % n];
            while (d > Math.PI) d -= 2 * Math.PI;
            while (d < -Math.PI) d += 2 * Math.PI;
            turn[i] = d;
        }

        // geglättete Krümmung = Summe der Richtungsänderung im Fenster / Fensterlänge
        var curv = new double[n];
        for (var i = 0; i < n; i++)
        {
            double sumTurn = turn[i], sumLen = len[i];
            for (int k = 1, j = i; sumLen < 2 * SmoothWindowM && k < n / 2; k++)
            {
                j = (i + k) % n; sumTurn += turn[j]; sumLen += len[j];
                j = (i - k + n) % n; sumTurn += turn[j]; sumLen += len[j];
            }
            curv[i] = sumTurn / sumLen;
        }

        // Start an einer Stelle ohne Kurve, damit keine Kurve über das Array-Ende geschnitten wird
        var start = Enumerable.Range(0, n).FirstOrDefault(i => Math.Abs(curv[i]) < MinCurvature * 0.5);

        // zusammenhängende Bereiche über der Schwelle
        var regions = new List<(int From, int To, int Sign)>();
        for (var k = 0; k < n; k++)
        {
            var i = (start + k) % n;
            var sign = Math.Abs(curv[i]) >= MinCurvature ? Math.Sign(curv[i]) : 0;
            if (sign == 0) continue;
            if (regions.Count > 0 && regions[^1].Sign == sign && Offset(regions[^1].To, i, n) == 1)
                regions[^1] = (regions[^1].From, i, sign);
            else
                regions.Add((i, i, sign));
        }

        // gleiche Richtung mit kleiner Lücke zusammenführen
        for (var r = regions.Count - 1; r > 0; r--)
        {
            var (aFrom, aTo, aSign) = regions[r - 1];
            var (_, bTo, bSign) = regions[r];
            if (aSign == bSign && Distance(len, aTo, regions[r].From, n) < MergeGapM)
            {
                regions[r - 1] = (aFrom, bTo, aSign);
                regions.RemoveAt(r);
            }
        }

        var corners = new List<(int Apex, Corner Corner)>();
        foreach (var (from, to, sign) in regions)
        {
            var count = Offset(from, to, n) + 1;
            double total = 0, best = 0;
            var apex = from;
            for (var k = 0; k < count; k++)
            {
                var i = (from + k) % n;
                total += turn[i];
                if (Math.Abs(curv[i]) > best) { best = Math.Abs(curv[i]); apex = i; }
            }
            if (Math.Abs(total) * 180 / Math.PI < MinTurnDeg) continue;

            corners.Add((apex, new Corner(0, sign > 0 ? "right" : "left", pts[from].S, pts[apex].S, pts[to].S)));
        }

        // in Fahrtrichtung nummerieren: der Umriss beginnt an der Ziellinie → Reihenfolge im Array
        return corners
            .OrderBy(c => c.Apex)
            .Select((c, i) => c.Corner with { Number = i + 1 })
            .ToList();
    }

    private static int Offset(int from, int to, int n) => (to - from + n) % n;

    private static double Distance(double[] len, int from, int to, int n)
    {
        double d = 0;
        for (var i = from; i != to; i = (i + 1) % n) d += len[i];
        return d;
    }
}
