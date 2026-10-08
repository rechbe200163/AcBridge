using AcBridge.Models;

namespace AcBridge.Telemetry;

public static class AcMapper
{
    public static TelemetryFrame ToFrame(RawSnapshot s, long seq, MiniSectorData miniSectors, SectorData sectors, int? ownBestLapMs, int? compoundIndex)
    {
        var p = s.Physics;
        var g = s.Graphics;
        var acc = p.AccG ?? new float[3];

        return new TelemetryFrame(
            Seq: seq,
            Timestamp: DateTimeOffset.UtcNow,
            Status: g.Status.ToString().ToLowerInvariant(),
            Session: g.Session.ToString().ToLowerInvariant(),
            Flag: g.Flag.ToString().ToLowerInvariant(),
            SpeedKmh: p.SpeedKmh,
            Rpm: p.Rpms,
            Gear: p.Gear - 1,
            Throttle: p.Gas,
            Brake: p.Brake,
            Clutch: p.Clutch,
            SteerAngle: p.SteerAngle,
            Fuel: p.Fuel,
            TurboBoost: p.TurboBoost,
            Tc: p.Tc,
            Abs: p.Abs,
            BrakeBias: p.BrakeBias,
            DrsAvailable: p.DrsAvailable != 0,
            DrsEnabled: p.DrsEnabled != 0,
            PitLimiterOn: p.PitLimiterOn != 0,
            GForce: new GForce(acc[0], acc[2], acc[1]),
            Tyres: new TyreData(
                CoreTemp: Wheels.From(p.TyreCoreTemperature),
                InnerTemp: Wheels.From(p.TyreTempI),
                MiddleTemp: Wheels.From(p.TyreTempM),
                OuterTemp: Wheels.From(p.TyreTempO),
                Pressure: Wheels.From(p.WheelsPressure),
                Wear: Wheels.From(p.TyreWear),
                Slip: Wheels.From(p.WheelSlip),
                Load: Wheels.From(p.WheelLoad),
                Compound: g.TyreCompound ?? "",
                CompoundIndex: compoundIndex),
            BrakeTemp: Wheels.From(p.BrakeTemp),
            SuspensionTravel: Wheels.From(p.SuspensionTravel),
            Lap: new LapData(
                CompletedLaps: g.CompletedLaps,
                Position: g.Position,
                CurrentLapMs: g.ICurrentTime,
                LastLapMs: g.ILastTime,
                BestLapMs: g.IBestTime > 0 ? g.IBestTime : ownBestLapMs ?? 0,
                CurrentSector: g.CurrentSectorIndex,
                LastSectorMs: g.LastSectorTime,
                NumberOfLaps: g.NumberOfLaps,
                TrackPosition: g.NormalizedCarPosition,
                SessionTimeLeftMs: (int)g.SessionTimeLeft,
                AcDeltaMs: (int)Math.Round(p.PerformanceMeter * 1000),   // Sekunden → ms
                IsInPit: g.IsInPit != 0,
                IsInPitLane: g.IsInPitLane != 0),
            CarCoordinates: g.CarCoordinates ?? new float[3],
            AirTemp: p.AirTemp,
            RoadTemp: p.RoadTemp,
            SurfaceGrip: g.SurfaceGrip,
            CarDamage: p.CarDamage ?? new float[5],
            MiniSectors: miniSectors,
            Sectors: sectors);
    }

    public static SessionInfo ToSession(AcStatic s, CarPhysics? carPhysics) => new(
        AcVersion: s.AcVersion ?? "",
        SmVersion: s.SmVersion ?? "",
        Car: s.CarModel ?? "",
        CarSkin: s.CarSkin ?? "",
        Track: s.Track ?? "",
        TrackConfiguration: s.TrackConfiguration ?? "",
        TrackLengthM: s.TrackSplineLength,
        Player: $"{s.PlayerName} {s.PlayerSurname}".Trim(),
        NumCars: s.NumCars,
        SectorCount: s.SectorCount,
        MaxRpm: s.MaxRpm,
        MaxFuel: s.MaxFuel,
        MaxPower: s.MaxPower,
        MaxTorque: s.MaxTorque,
        HasDrs: s.HasDrs != 0,
        HasErs: s.HasErs != 0,
        HasKers: s.HasKers != 0,
        IsTimedRace: s.IsTimedRace != 0,
        FuelRate: s.AidFuelRate,
        TyreRate: s.AidTireRate,
        DamageRate: s.AidMechanicalDamage,
        CarPhysics: carPhysics);
}
