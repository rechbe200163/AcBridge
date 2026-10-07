using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;
using System.Text;

namespace AcBridge.Telemetry;

/// <summary>Ein Auto im Feld, wie es die In-Game-App liefert. CarId 0 ist der Spieler.</summary>
public readonly record struct FieldCar(
    int CarId,
    bool Connected,
    int LapCount,
    int LapTimeMs,
    int LastLapMs,
    int BestLapMs,
    float SplinePosition,
    float SpeedKmh,
    bool InPitline,
    int Position,
    string Name);

/// <summary>Liefert die Daten aller Autos. null = gerade keine Daten (App nicht aktiv, AC aus …).</summary>
public interface IFieldSource : IDisposable
{
    IReadOnlyList<FieldCar>? Read();
}

/// <summary>
/// Liest das Shared Memory "Local\acbridge_cars", das die Python-App "AcBridge" in AC schreibt.
/// Layout muss zu apps/python/AcBridge/AcBridge.py passen.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AcAppFieldSource : IFieldSource
{
    private const string MapName = "Local\\acbridge_cars";
    private const int Version = 1;
    private const int MaxCars = 64;
    private const int HeaderSize = 16;
    private const int CarSize = 72;
    private const int Size = HeaderSize + CarSize * MaxCars + 4;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2);

    private readonly byte[] _buffer = new byte[Size];
    private MemoryMappedFile? _map;
    private int _lastPacket = -1;
    private DateTime _lastChange = DateTime.MinValue;

    public IReadOnlyList<FieldCar>? Read()
    {
        if (!TryOpen()) return null;

        try
        {
            using (var view = _map!.CreateViewStream(0, Size, MemoryMappedFileAccess.Read))
                view.ReadExactly(_buffer, 0, Size);
        }
        catch (IOException) { Dispose(); return null; }

        var span = _buffer.AsSpan();
        var version = BinaryPrimitives.ReadInt32LittleEndian(span);
        var packet = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        var count = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(span[8..]), 0, MaxCars);
        var trailer = BinaryPrimitives.ReadInt32LittleEndian(span[(Size - 4)..]);

        // Falsche Version oder gerade halb geschrieben → diesen Frame überspringen
        if (version != Version || packet != trailer) return null;

        // App aktiv, aber AC pausiert/geschlossen → Daten veraltet
        if (packet != _lastPacket) { _lastPacket = packet; _lastChange = DateTime.UtcNow; }
        else if (DateTime.UtcNow - _lastChange > StaleAfter) return null;

        var cars = new FieldCar[count];
        for (var i = 0; i < count; i++)
        {
            var c = span.Slice(HeaderSize + i * CarSize, CarSize);
            var nameBytes = c.Slice(40, 32);
            var nameLength = nameBytes.IndexOf((byte)0);
            cars[i] = new FieldCar(
                CarId: BinaryPrimitives.ReadInt32LittleEndian(c),
                Connected: BinaryPrimitives.ReadInt32LittleEndian(c[4..]) != 0,
                LapCount: BinaryPrimitives.ReadInt32LittleEndian(c[8..]),
                LapTimeMs: BinaryPrimitives.ReadInt32LittleEndian(c[12..]),
                LastLapMs: BinaryPrimitives.ReadInt32LittleEndian(c[16..]),
                BestLapMs: BinaryPrimitives.ReadInt32LittleEndian(c[20..]),
                SplinePosition: BinaryPrimitives.ReadSingleLittleEndian(c[24..]),
                SpeedKmh: BinaryPrimitives.ReadSingleLittleEndian(c[28..]),
                InPitline: BinaryPrimitives.ReadInt32LittleEndian(c[32..]) != 0,
                Position: BinaryPrimitives.ReadInt32LittleEndian(c[36..]),
                Name: Encoding.UTF8.GetString(nameLength < 0 ? nameBytes : nameBytes[..nameLength]));
        }
        return cars;
    }

    private bool TryOpen()
    {
        if (_map is not null) return true;
        try
        {
            _map = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            return true;
        }
        catch (FileNotFoundException) { return false; }   // App nicht aktiv → Solo-Modus
    }

    public void Dispose()
    {
        _map?.Dispose();
        _map = null;
        _lastPacket = -1;
    }
}

/// <summary>Keine Felddaten → immer Solo-Modus.</summary>
public sealed class NoFieldSource : IFieldSource
{
    public IReadOnlyList<FieldCar>? Read() => null;
    public void Dispose() { }
}

/// <summary>Zwei simulierte KI-Autos, damit der Feld-Modus auch am Mac testbar ist.</summary>
public sealed class MockFieldSource : IFieldSource
{
    private const float TrackLength = 4000f;

    private sealed class Car(int id, string name, float pace, float startDistance)
    {
        public readonly int Id = id;
        public readonly string Name = name;
        public readonly float Pace = pace;
        public float Distance = startDistance;
        public float LapTime;
        public int Laps;
        public int LastLap;
        public int BestLap;
    }

    private readonly Car[] _cars =
    [
        new(1, "KI Schnell", 1.012f, 1200f),
        new(2, "KI Langsam", 0.985f, 2600f),
    ];

    private DateTime _lastTick = DateTime.UtcNow;

    public IReadOnlyList<FieldCar>? Read()
    {
        var now = DateTime.UtcNow;
        var dt = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;

        var result = new List<FieldCar>
        {
            // Spieler (Id 0) – wird vom FieldTracker ohnehin ignoriert
            new(0, true, 0, 0, 0, 0, 0, 0, false, 1, "Spieler"),
        };

        foreach (var c in _cars)
        {
            var pos = c.Distance / TrackLength;
            var speed = (150f + 110f * MathF.Sin(pos * MathF.PI * 6))
                        * c.Pace * (1f + 0.02f * MathF.Sin(c.Laps * 1.3f + pos * 11f));
            c.Distance += speed / 3.6f * dt;
            c.LapTime += dt * 1000f;
            if (c.Distance >= TrackLength)
            {
                c.Distance -= TrackLength;
                c.Laps++;
                c.LastLap = (int)c.LapTime;
                c.BestLap = c.BestLap == 0 ? c.LastLap : Math.Min(c.BestLap, c.LastLap);
                c.LapTime = 0;
            }
            result.Add(new FieldCar(c.Id, true, c.Laps, (int)c.LapTime, c.LastLap, c.BestLap,
                c.Distance / TrackLength, speed, false, 0, c.Name));
        }
        return result;
    }

    public void Dispose() { }
}
