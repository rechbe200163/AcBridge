using System.Collections.Concurrent;
using System.Diagnostics;
using AcBridge.Models;

namespace AcBridge.Telemetry.CarData;

/// <summary>
/// Liest aus den Autodateien (data.acd bzw. data/) die Temperaturfenster für Reifen und Bremsen.
/// Ergebnis wird pro Auto gecacht. Wichtig: immer dieselbe Instanz zurückgeben,
/// sonst gilt die SessionInfo bei jedem Vergleich als "geändert".
/// </summary>
public sealed class CarDataService(TelemetryOptions options, ILogger<CarDataService> log)
{
    // Grip-Anteil vom Maximum: ab hier "optimal" (grün) bzw. "ok"
    private const float OptimalShare = 0.995f;
    private const float OkShare = 0.98f;

    private readonly ConcurrentDictionary<string, CarPhysics> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);
    private string? _acRoot;

    /// <summary>Wird verwendet, wenn keine Autodateien gefunden werden (Mock-Modus am Mac).</summary>
    public CarPhysics? Fallback { get; init; }

    public CarPhysics? Get(string carModel)
    {
        if (string.IsNullOrWhiteSpace(carModel)) return null;
        if (_cache.TryGetValue(carModel, out var cached)) return cached;

        try
        {
            var files = LoadFiles(carModel);
            if (files is null) return Fallback;   // nicht gefunden → beim nächsten Mal wieder probieren

            var physics = new CarPhysics(ReadTyres(files), ReadBrakes(files));
            log.LogInformation("Autodaten {Car}: {Count} Reifenmischungen, Bremsen {Brakes}",
                carModel, physics.TyreCompounds.Count, physics.Brakes is null ? "ohne Temperaturmodell" : "mit Temperaturmodell");
            return _cache.GetOrAdd(carModel, physics);
        }
        catch (Exception ex)
        {
            WarnOnce(carModel, $"Autodaten von {carModel} konnten nicht gelesen werden: {ex.Message}");
            return _cache.GetOrAdd(carModel, CarPhysics.Empty);   // kaputt → nicht jede 2 s neu versuchen
        }
    }

    /// <summary>Index der aktuellen Mischung. AC liefert je nach Auto "Hard", "C1" oder "Hard (C1)".</summary>
    public static int? FindCompound(CarPhysics? physics, string? compound)
    {
        if (physics is null || string.IsNullOrWhiteSpace(compound)) return null;
        var c = compound.Trim();
        foreach (var t in physics.TyreCompounds)
        {
            if (c.Equals(t.Name, StringComparison.OrdinalIgnoreCase)
                || c.Equals(t.ShortName, StringComparison.OrdinalIgnoreCase)
                || c.Equals($"{t.Name} ({t.ShortName})", StringComparison.OrdinalIgnoreCase))
                return t.Index;
        }
        // Fallback: Kurzname in Klammern irgendwo im String
        foreach (var t in physics.TyreCompounds)
            if (t.ShortName.Length > 0 && c.Contains($"({t.ShortName})", StringComparison.OrdinalIgnoreCase))
                return t.Index;
        return null;
    }

    // ---------- Dateien finden ----------

    private Dictionary<string, byte[]>? LoadFiles(string carModel)
    {
        var root = FindAcRoot();
        if (root is null)
        {
            if (Fallback is not null) return null;
            WarnOnce("root", "AC-Ordner nicht gefunden. Setz Telemetry:AcRoot in appsettings.json.");
            return null;
        }

        var carDir = Path.Combine(root, "content", "cars", carModel);
        var dataDir = Path.Combine(carDir, "data");
        var acd = Path.Combine(carDir, "data.acd");

        if (Directory.Exists(dataDir)) return AcdArchive.ReadFolder(dataDir);
        if (File.Exists(acd)) return AcdArchive.Read(acd, carModel);

        if (Fallback is null) WarnOnce(carModel, $"Keine Autodaten unter {carDir}");
        return null;
    }

    /// <summary>1. appsettings  2. Pfad vom laufenden acs.exe  3. Steam-Standardpfad</summary>
    private string? FindAcRoot()
    {
        if (_acRoot is not null) return _acRoot;

        if (!string.IsNullOrWhiteSpace(options.AcRoot) && Directory.Exists(options.AcRoot))
            return _acRoot = options.AcRoot;

        foreach (var name in new[] { "acs", "acs_x86" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    var dir = Path.GetDirectoryName(p.MainModule?.FileName);
                    if (dir is not null && Directory.Exists(Path.Combine(dir, "content", "cars")))
                        return _acRoot = dir;
                }
                catch { /* kein Zugriff auf den Prozess → nächster Weg */ }
                finally { p.Dispose(); }
            }
        }

        var steam = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "assettocorsa");
        return Directory.Exists(steam) ? _acRoot = steam : null;
    }

    // ---------- Auswerten ----------

    private static List<TyreCompoundInfo> ReadTyres(IReadOnlyDictionary<string, byte[]> files)
    {
        var result = new List<TyreCompoundInfo>();
        var ini = IniFile.From(files, "tyres.ini");
        if (ini is null) return result;

        // [FRONT], [FRONT_1], [FRONT_2] … bis eine fehlt
        for (var i = 0; ; i++)
        {
            var suffix = i == 0 ? "" : $"_{i}";
            if (!ini.HasSection("FRONT" + suffix)) break;

            result.Add(new TyreCompoundInfo(
                Index: i,
                Name: ini.Get("FRONT" + suffix, "NAME") ?? $"Compound {i}",
                ShortName: ini.Get("FRONT" + suffix, "SHORT_NAME") ?? "",
                Front: Window(Lut.Load(ini.Get("THERMAL_FRONT" + suffix, "PERFORMANCE_CURVE"), files)),
                Rear: Window(Lut.Load(ini.Get("THERMAL_REAR" + suffix, "PERFORMANCE_CURVE"), files)),
                IdealPressureFront: ini.GetFloat("FRONT" + suffix, "PRESSURE_IDEAL"),
                IdealPressureRear: ini.GetFloat("REAR" + suffix, "PRESSURE_IDEAL")));
        }
        return result;
    }

    private static BrakeWindows? ReadBrakes(IReadOnlyDictionary<string, byte[]> files)
    {
        var ini = IniFile.From(files, "brakes.ini");
        if (ini is null) return null;
        var front = Window(Lut.Load(ini.Get("TEMPS_FRONT", "PERF_CURVE"), files));
        var rear = Window(Lut.Load(ini.Get("TEMPS_REAR", "PERF_CURVE"), files));
        return front is null && rear is null ? null : new BrakeWindows(front, rear);
    }

    /// <summary>Tastet die Kurve in 1-°C-Schritten ab und sucht die Bereiche um das Maximum.</summary>
    private static TempWindow? Window(List<(float X, float Y)>? lut)
    {
        if (lut is null) return null;
        var from = (int)Math.Floor(lut[0].X);
        var to = (int)Math.Ceiling(lut[^1].X);
        if (to <= from) return null;

        var peakX = from;
        var peakY = float.MinValue;
        for (var x = from; x <= to; x++)
        {
            var y = Lut.Sample(lut, x);
            if (y > peakY) { peakY = y; peakX = x; }
        }

        (int Min, int Max) Range(float share)
        {
            int min = peakX, max = peakX;
            while (min > from && Lut.Sample(lut, min - 1) >= peakY * share) min--;
            while (max < to && Lut.Sample(lut, max + 1) >= peakY * share) max++;
            return (min, max);
        }

        // Plateau (z. B. 500–600 °C bei Bremsen): Peak = Mitte des Plateaus
        var plateau = Range(0.9999f);
        var optimal = Range(OptimalShare);
        var ok = Range(OkShare);
        return new TempWindow(
            PeakC: (plateau.Min + plateau.Max) / 2,
            OptimalMinC: optimal.Min, OptimalMaxC: optimal.Max,
            OkMinC: ok.Min, OkMaxC: ok.Max);
    }

    private void WarnOnce(string key, string message)
    {
        lock (_warned)
            if (_warned.Add(key)) log.LogWarning("{Message}", message);
    }
}
