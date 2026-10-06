using System.Text.Json;
using AcBridge.Models;

namespace AcBridge.Telemetry;

/// <summary>
/// Beste Runde aller Zeiten für eine Kombination Auto + Strecke + Layout.
/// Trace[i] = Rundenzeit (ms) an Streckenposition i / (Trace.Length - 1).
/// </summary>
public sealed record ReferenceLap(
    int LapMs,
    string Car,
    string Track,
    string Layout,
    DateTimeOffset RecordedAt,
    double[] Trace);

/// <summary>
/// Speichert die Referenzrunden als JSON-Dateien, eine pro Auto/Strecke/Layout.
/// Standardordner: %LOCALAPPDATA%\AcBridge\references (Windows), ~/.local/share/AcBridge/references (Mac/Linux).
/// </summary>
public sealed class ReferenceLapStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AcBridge", "references");

    public string Directory { get; } = directory;

    public static string KeyFor(SessionInfo s)
    {
        var raw = $"{s.Car}__{s.Track}__{s.TrackConfiguration}";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(raw.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
    }

    public ReferenceLap? Load(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ReferenceLap>(File.ReadAllText(path), Json); }
        catch { return null; }   // kaputte Datei → so tun, als gäbe es keine
    }

    public void Save(string key, ReferenceLap lap)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(key);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(lap, Json));
        File.Move(tmp, path, overwrite: true);     // erst fertig schreiben, dann ersetzen
    }

    private string PathFor(string key) => Path.Combine(Directory, key + ".json");
}
