using System.Globalization;
using System.Text;

namespace AcBridge.Telemetry.CarData;

/// <summary>Minimaler INI-Parser für AC-Dateien ([SECTION], KEY=VALUE, Kommentare mit ';').</summary>
public sealed class IniFile
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public IniFile(string text)
    {
        Dictionary<string, string>? current = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split(';')[0].Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _sections[line[1..^1].Trim()] = current;
                continue;
            }
            var eq = line.IndexOf('=');   // nur am ersten '=' trennen: Inline-LUTs enthalten selbst '='
            if (current is null || eq <= 0) continue;
            current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
    }

    public static IniFile? From(IReadOnlyDictionary<string, byte[]> files, string name) =>
        files.TryGetValue(name, out var bytes) ? new IniFile(Encoding.Latin1.GetString(bytes)) : null;

    public bool HasSection(string section) => _sections.ContainsKey(section);

    public string? Get(string section, string key) =>
        _sections.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    public float? GetFloat(string section, string key) =>
        float.TryParse(Get(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;
}

/// <summary>
/// AC-Lookup-Tabelle (x → y). Entweder eigene Datei ("x|y" pro Zeile)
/// oder inline im INI-Wert: "(|0=0.93|100=0.948|…|)".
/// </summary>
public static class Lut
{
    public static List<(float X, float Y)>? Load(string? value, IReadOnlyDictionary<string, byte[]> files)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        IEnumerable<string[]> pairs;
        if (value.StartsWith('('))
            pairs = value.Trim('(', ')').Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('='));
        else if (files.TryGetValue(value, out var bytes))
            pairs = Encoding.Latin1.GetString(bytes).Split('\n')
                .Select(l => l.Split(';')[0].Trim())
                .Where(l => l.Contains('|'))
                .Select(l => l.Split('|'));
        else
            return null;

        var points = new List<(float, float)>();
        foreach (var p in pairs)
        {
            if (p.Length == 2
                && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                points.Add((x, y));
        }
        points.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return points.Count > 0 ? points : null;
    }

    /// <summary>Linear interpoliert, außerhalb der Tabelle wird wie in AC der Randwert genommen.</summary>
    public static float Sample(IReadOnlyList<(float X, float Y)> lut, float x)
    {
        if (x <= lut[0].X) return lut[0].Y;
        if (x >= lut[^1].X) return lut[^1].Y;
        for (var i = 1; i < lut.Count; i++)
        {
            if (x > lut[i].X) continue;
            var (x0, y0) = lut[i - 1];
            var (x1, y1) = lut[i];
            return x1 == x0 ? y1 : y0 + (y1 - y0) * (x - x0) / (x1 - x0);
        }
        return lut[^1].Y;
    }
}
