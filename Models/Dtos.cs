namespace AcBridge.Models;

// Das sind die Objekte, die als JSON rausgehen (camelCase).
// Rad-Reihenfolge in AC ist immer FL, FR, RL, RR.

public record Wheels(float Fl, float Fr, float Rl, float Rr)
{
    public static Wheels From(float[]? a) =>
        a is { Length: >= 4 } ? new(a[0], a[1], a[2], a[3]) : new(0, 0, 0, 0);
}

public record GForce(float Lateral, float Longitudinal, float Vertical);

public record TyreData(
    Wheels CoreTemp,      // °C
    Wheels InnerTemp,     // °C
    Wheels MiddleTemp,    // °C
    Wheels OuterTemp,     // °C
    Wheels Pressure,      // psi
    Wheels Wear,          // 0–100 (100 = neu)
    Wheels Slip,
    Wheels Load,          // N
    string Compound,
    int? CompoundIndex);  // Index in SessionInfo.CarPhysics.TyreCompounds (null = unbekannt)

public record LapData(
    int CompletedLaps,
    int Position,
    int CurrentLapMs,
    int LastLapMs,
    int BestLapMs,        // von AC; wenn AC 0 liefert (z. B. Hotlap): beste gültige Runde, die die Bridge gemessen hat
    int CurrentSector,
    int LastSectorMs,
    int NumberOfLaps,
    float TrackPosition,  // 0.0–1.0 entlang des Splines
    int SessionTimeLeftMs,
    int AcDeltaMs,        // Live-Delta von AC (performanceMeter) in ms: −1500 = 1,5 s schneller, +1500 = langsamer
    bool IsInPit,
    bool IsInPitLane);

public record TelemetryFrame(
    long Seq,
    DateTimeOffset Timestamp,
    string Status,        // off | replay | live | pause
    string Session,       // practice | qualify | race | …
    string Flag,
    float SpeedKmh,
    int Rpm,
    int Gear,             // -1 = R, 0 = N, 1.. = Gang
    float Throttle,       // 0–1
    float Brake,          // 0–1
    float Clutch,         // 0–1 (1 = eingekuppelt/nicht getreten)
    float SteerAngle,
    float Fuel,           // Liter
    float TurboBoost,
    float Tc,
    float Abs,
    float BrakeBias,
    bool DrsAvailable,
    bool DrsEnabled,
    bool PitLimiterOn,
    GForce GForce,
    TyreData Tyres,
    Wheels BrakeTemp,     // °C
    Wheels SuspensionTravel,
    LapData Lap,
    float[] CarCoordinates,
    float AirTemp,
    float RoadTemp,
    float SurfaceGrip,
    float[] CarDamage,
    MiniSectorData MiniSectors,
    SectorData Sectors);

/// <summary>
/// Echte AC-Sektoren (meist 3), alle Zeiten in ms. Array-Index = Sektor (0 = S1).
/// CurrentLap[i] / LastLap[i] / Best[i] = null → (noch) kein Wert.
/// Results wie bei den Minisektoren: purple | green | yellow | neutral | pending
/// </summary>
public record SectorData(
    int Count,
    int Current,                 // aktueller Sektor (0-basiert)
    int? CurrentRunningMs,       // läuft gerade im aktuellen Sektor (null = Sektorstart nicht gesehen)
    int?[] CurrentLap,           // fertige Sektoren der laufenden Runde
    string[] CurrentResults,
    int?[] LastLap,              // komplette letzte Runde (leer, wenn es noch keine gibt)
    string[] LastLapResults,
    int?[] Best,                 // beste Zeit je Sektor in dieser Session (nur gültige Runden)
    int?[] BestLap,              // Sektoren der besten Runde (Rundenzeit: Lap.BestLapMs)
    int? TheoreticalBestMs);     // Summe der besten Sektoren

/// <summary>
/// Minisektoren der aktuellen Runde.
/// Mode: "solo" (allein) | "field" (andere Autos, Daten von der In-Game-App)
/// Results[i]:
///   solo : "purple" eigener Bestwert | "green" schneller als eigene beste Runde | "yellow" langsamer
///   field: "purple" Feld-Bestwert    | "green" eigener Bestwert              | "yellow" langsamer
///   beide: "neutral" gefahren, noch kein Vergleichswert | "pending" noch nicht gefahren
/// Das Live-Delta steht in Lap.AcDeltaMs (direkt von AC).
/// </summary>
public record MiniSectorData(
    int Count,
    int Current,
    string[] Results,
    string[] LastLapResults,   // komplett gefärbte letzte Runde (leer, wenn es keine gibt)
    bool LapValid,
    string Mode,
    int FieldCars);            // Anzahl anderer Autos auf der Strecke

public record SessionInfo(
    string AcVersion,
    string SmVersion,
    string Car,
    string CarSkin,
    string Track,
    string TrackConfiguration,
    float TrackLengthM,
    string Player,
    int NumCars,
    int SectorCount,
    int MaxRpm,
    float MaxFuel,
    float MaxPower,
    float MaxTorque,
    bool HasDrs,
    bool HasErs,
    bool HasKers,
    bool IsTimedRace,
    float FuelRate,
    float TyreRate,
    float DamageRate,
    CarPhysics? CarPhysics);   // aus den Autodateien, null wenn nicht lesbar

/// <summary>Temperaturfenster aus einer AC-Performance-Kurve (°C).</summary>
/// <param name="PeakC">maximaler Grip / maximale Bremswirkung</param>
/// <param name="OptimalMinC">ab hier ≥ 99,5 % vom Maximum (grün)</param>
/// <param name="OkMinC">ab hier ≥ 98 % vom Maximum</param>
public record TempWindow(int PeakC, int OptimalMinC, int OptimalMaxC, int OkMinC, int OkMaxC);

public record TyreCompoundInfo(
    int Index,
    string Name,
    string ShortName,
    TempWindow? Front,
    TempWindow? Rear,
    float? IdealPressureFront,   // psi
    float? IdealPressureRear);

/// <summary>null pro Achse = Auto hat dort kein Bremstemperatur-Modell.</summary>
public record BrakeWindows(TempWindow? Front, TempWindow? Rear);

public record CarPhysics(IReadOnlyList<TyreCompoundInfo> TyreCompounds, BrakeWindows? Brakes)
{
    public static readonly CarPhysics Empty = new([], null);
}

public record BridgeStatus(
    bool Connected,
    string Source,
    string? GameStatus,
    int StreamClients,
    DateTimeOffset? LastFrameAt);

/// <summary>Umschlag für alle WebSocket-Nachrichten: { "type": "...", "data": {...} }</summary>
public record WsMessage<T>(string Type, T Data);
