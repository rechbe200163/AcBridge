using AcBridge.Models;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AcBridge.Telemetry;

public readonly record struct RawSnapshot(AcPhysics Physics, AcGraphics Graphics);

public interface ITelemetrySource : IDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    bool TryConnect();
    RawSnapshot? ReadFrame();
    AcStatic? ReadStatic();
}

/// <summary>Liest die drei Memory-Mapped-Files, die AC unter Windows anlegt.</summary>
[SupportedOSPlatform("windows")]
public sealed class AcSharedMemorySource : ITelemetrySource
{
    private const string PhysicsMap = "Local\\acpmf_physics";
    private const string GraphicsMap = "Local\\acpmf_graphics";
    private const string StaticMap = "Local\\acpmf_static";

    private MemoryMappedFile? _physics, _graphics, _static;

    public string Name => "SharedMemory";
    public bool IsConnected => _physics is not null;

    public bool TryConnect()
    {
        if (IsConnected) return true;
        try
        {
            _physics = MemoryMappedFile.OpenExisting(PhysicsMap, MemoryMappedFileRights.Read);
            _graphics = MemoryMappedFile.OpenExisting(GraphicsMap, MemoryMappedFileRights.Read);
            _static = MemoryMappedFile.OpenExisting(StaticMap, MemoryMappedFileRights.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            Dispose(); // AC läuft (noch) nicht
            return false;
        }
    }

    public RawSnapshot? ReadFrame()
    {
        if (_physics is null || _graphics is null) return null;
        return new RawSnapshot(Read<AcPhysics>(_physics), Read<AcGraphics>(_graphics));
    }

    public AcStatic? ReadStatic() => _static is null ? null : Read<AcStatic>(_static);

    private static T Read<T>(MemoryMappedFile mmf) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var buffer = new byte[size];
        using (var view = mmf.CreateViewStream(0, size, MemoryMappedFileAccess.Read))
        {
            view.ReadExactly(buffer, 0, size);
        }
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject()); }
        finally { handle.Free(); }
    }

    public void Dispose()
    {
        _physics?.Dispose(); _graphics?.Dispose(); _static?.Dispose();
        _physics = _graphics = _static = null;
    }
}

/// <summary>
/// Fake-Daten: ein Auto fährt Runden auf einer ~4 km Strecke.
/// Damit kannst du am Mac/iPad das Frontend bauen, ohne dass AC läuft.
/// </summary>
/// <summary>Autodaten für den Mock (Werte aus der data.acd der RSS Formula Hybrid 2020).</summary>
public static class MockCarData
{
    private static TyreCompoundInfo Compound(int i, string name, string shortName, int peak) =>
        new(i, name, shortName,
            Front: new TempWindow(peak, peak - 10, peak + 10, peak - 22, peak + 22),
            Rear: new TempWindow(peak, peak - 10, peak + 10, peak - 22, peak + 22),
            IdealPressureFront: 22, IdealPressureRear: 22);

    public static readonly CarPhysics Physics = new(
        [
            Compound(0, "UltraSoft", "C5", 85),
            Compound(1, "SuperSoft", "C4", 95),
            Compound(2, "Soft", "C3", 105),
            Compound(3, "Medium", "C2", 115),
            Compound(4, "Hard", "C1", 125),
        ],
        new BrakeWindows(new TempWindow(550, 400, 700, 270, 900), new TempWindow(550, 400, 700, 270, 900)));
}

public sealed class MockTelemetrySource : ITelemetrySource
{
    private const float TrackLength = 4000f;
    private readonly DateTime _start = DateTime.UtcNow;
    private DateTime _lastTick = DateTime.UtcNow;
    private float _distance, _lapTime, _fuel = 60f;
    private int _laps, _lastLap, _bestLap, _packet;

    public string Name => "Mock";
    public bool IsConnected => true;
    public bool TryConnect() => true;

    public RawSnapshot? ReadFrame()
    {
        var now = DateTime.UtcNow;
        var dt = (float)(now - _lastTick).TotalSeconds;
        _lastTick = now;
        var t = (float)(now - _start).TotalSeconds;

        var pos = _distance / TrackLength;
        // Geschwindigkeitsprofil: 3 Geraden, 3 Kurven pro Runde
        var wave = MathF.Sin(pos * MathF.PI * 6);
        // pro Runde etwas anders schnell, damit Minisektoren mal lila/grün/gelb werden
        var variation = 1f + 0.03f * MathF.Sin(_laps * 2.1f + pos * 17f);
        var speed = (150f + 110f * wave) * variation;      // ca. 40–260 km/h
        var accel = MathF.Cos(pos * MathF.PI * 6);         // >0 = beschleunigt
        var gear = Math.Clamp((int)(speed / 45f) + 1, 1, 6);
        var rpm = (int)(3500 + (speed % 45f) / 45f * 4500);

        _distance += speed / 3.6f * dt;
        _lapTime += dt * 1000f;
        _fuel = MathF.Max(0, _fuel - 0.0007f * speed * dt);
        if (_distance >= TrackLength)
        {
            _distance -= TrackLength;
            _laps++;
            _lastLap = (int)_lapTime;
            _bestLap = _bestLap == 0 ? _lastLap : Math.Min(_bestLap, _lastLap);
            _lapTime = 0;
        }

        float W(float baseV, float amp, float phase) => baseV + amp * MathF.Sin(t * 0.3f + phase);
        float[] Four(float baseV, float amp) => [W(baseV, amp, 0), W(baseV, amp, 1), W(baseV, amp, 2), W(baseV, amp, 3)];

        var physics = new AcPhysics
        {
            PacketId = ++_packet,
            Gas = accel > 0 ? MathF.Min(1, 0.4f + accel) : 0,
            Brake = accel < -0.3f ? MathF.Min(1, -accel) : 0,
            Clutch = 1,
            Fuel = _fuel,
            Gear = gear + 1, // AC-Kodierung: 0=R, 1=N
            Rpms = rpm,
            SpeedKmh = speed,
            SteerAngle = 0.4f * MathF.Sin(pos * MathF.PI * 6 + 1.5f),
            Velocity = new float[3],
            AccG = [1.8f * MathF.Sin(pos * MathF.PI * 6 + 1.5f), 1f, 1.2f * accel],
            WheelSlip = Four(0.05f, 0.04f),
            WheelLoad = Four(3500, 600),
            WheelsPressure = Four(22f, 0.6f),
            WheelAngularSpeed = new float[4],
            TyreWear = Four(98f - _laps * 0.4f, 0.2f),
            TyreDirtyLevel = new float[4],
            // um das C1-Fenster (111–139 °C) herum, damit alle Farben vorkommen
            TyreCoreTemperature = Four(120, 18),
            TyreTempI = Four(127, 18),
            TyreTempM = Four(121, 18),
            TyreTempO = Four(115, 18),
            CamberRad = new float[4],
            SuspensionTravel = Four(0.04f, 0.015f),
            BrakeTemp = Four(300 + 500 * MathF.Max(0, -accel), 20),
            CarDamage = new float[5],
            RideHeight = new float[2],
            LocalAngularVelocity = new float[3],
            TyreContactPoint = new float[12],
            TyreContactNormal = new float[12],
            TyreContactHeading = new float[12],
            LocalVelocity = new float[3],
            Tc = 0.2f, Abs = 0.3f, BrakeBias = 0.58f,
            // Delta wie AC's performanceMeter (Sekunden): pendelt über die Runde zwischen ca. −0,6 und +0,6 s
            PerformanceMeter = 0.6f * MathF.Sin(pos * MathF.PI * 2 + _laps * 1.7f),
            AirTemp = 24, RoadTemp = 32,
        };

        var angle = pos * MathF.PI * 2;
        var graphics = new AcGraphics
        {
            PacketId = _packet,
            Status = AcStatus.Live,
            Session = AcSessionType.Practice,
            CurrentTime = "", LastTime = "", BestTime = "", Split = "", TyreCompound = "Hard (C1)",
            CompletedLaps = _laps,
            Position = 1,
            ICurrentTime = (int)_lapTime,
            ILastTime = _lastLap,
            IBestTime = _bestLap,
            SessionTimeLeft = -1,
            CurrentSectorIndex = Math.Min(2, (int)(pos * 3)),
            NormalizedCarPosition = pos,
            CarCoordinates = [600 * MathF.Cos(angle), 0, 400 * MathF.Sin(angle)],
            Flag = AcFlagType.None,
            SurfaceGrip = 0.98f,
        };
        return new RawSnapshot(physics, graphics);
    }

    public AcStatic? ReadStatic() => new AcStatic
    {
        SmVersion = "1.7", AcVersion = "mock",
        NumberOfSessions = 1, NumCars = 1,
        CarModel = "ks_porsche_911_gt3_r_2016", CarSkin = "mock",
        Track = "ks_red_bull_ring", TrackConfiguration = "layout_gp",
        PlayerName = "Michael", PlayerSurname = "", PlayerNick = "",
        SectorCount = 3, MaxRpm = 9000, MaxFuel = 120, MaxPower = 368000, MaxTorque = 480,
        SuspensionMaxTravel = new float[4], TyreRadius = new float[4],
        TrackSplineLength = TrackLength, AidFuelRate = 1, AidTireRate = 1, AidMechanicalDamage = 1,
    };

    public void Dispose() { }
}
