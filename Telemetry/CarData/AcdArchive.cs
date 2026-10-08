using System.Text;

namespace AcBridge.Telemetry.CarData;

/// <summary>
/// Liest die verschlüsselte data.acd eines Autos (content/cars/&lt;auto&gt;/data.acd).
/// Aufbau: hintereinander je Datei
///   int32 Namenslänge, Name (ASCII), int32 Anzahl Bytes, dann pro Byte ein int32 (verschlüsselt).
/// Entschlüsseln: Byte = (Wert − Schlüssel[i % Länge]) &amp; 0xFF.
/// Der Schlüssel wird aus dem Ordnernamen des Autos berechnet (derselbe Algorithmus wie in Content Manager).
/// </summary>
public static class AcdArchive
{
    public static Dictionary<string, byte[]> Read(string acdPath, string carFolderName)
    {
        var key = Encoding.ASCII.GetBytes(CreateKey(carFolderName));
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        using var reader = new BinaryReader(File.OpenRead(acdPath));
        var length = reader.BaseStream.Length;
        while (reader.BaseStream.Position < length)
        {
            var nameLength = reader.ReadInt32();
            var name = Encoding.ASCII.GetString(reader.ReadBytes(nameLength));
            var count = reader.ReadInt32();
            var data = new byte[count];
            for (var i = 0; i < count; i++)
                data[i] = (byte)((reader.ReadInt32() - key[i % key.Length]) & 0xFF);
            files[name] = data;
        }
        return files;
    }

    /// <summary>Liest stattdessen einen entpackten data-Ordner (falls vorhanden).</summary>
    public static Dictionary<string, byte[]> ReadFolder(string dataFolder) =>
        Directory.EnumerateFiles(dataFolder)
            .ToDictionary(p => Path.GetFileName(p), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Schlüssel aus dem Ordnernamen, z. B. "rss_formula_hybrid_2020" → "177-238-20-68-134-16-9-49".
    /// Bewusst int-Arithmetik ohne checked: Überläufe sind gewollt, nur die untersten 8 Bit zählen.
    /// </summary>
    public static string CreateKey(string carFolderName)
    {
        var s = carFolderName.ToLowerInvariant().Select(c => (int)c).ToArray();
        var n = s.Length;
        var k = new int[8];

        foreach (var c in s) k[0] += c;

        var v = 0;
        for (var i = 0; i < n - 1; i += 2) { v *= s[i]; v -= s[i + 1]; }
        k[1] = v;

        v = 0;
        for (var i = 1; i < n - 3; i += 3) { v *= s[i]; v /= s[i + 1] + 27; v += -27 - s[i - 1]; }
        k[2] = v;

        v = 5763;
        for (var i = 1; i < n; i++) v -= s[i];
        k[3] = v;

        v = 66;
        for (var i = 1; i < n - 4; i += 4) { v = (s[i] + 15) * v; v *= s[i - 1] + 15; v += 22; }
        k[4] = v;

        v = 101;
        for (var i = 0; i < n - 2; i += 2) v -= s[i];
        k[5] = v;

        v = 171;
        for (var i = 0; i < n - 2; i += 2) v %= s[i];
        k[6] = v;

        v = 171;
        for (var i = 0; i < n - 1; i++) { v /= s[i]; v += s[i + 1]; }
        k[7] = v;

        return string.Join("-", k.Select(x => x & 0xFF));
    }
}
