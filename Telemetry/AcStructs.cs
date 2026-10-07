using System.Runtime.InteropServices;

namespace AcBridge.Telemetry;

// Layouts nach der offiziellen "Shared Memory Reference" von Kunos (Assetto Corsa 1.x).
// Nur der vordere, stabile Teil der Structs ist abgebildet – neuere AC-Versionen hängen
// hinten Felder an, das stört nicht, weil wir nur sizeof(T) Bytes lesen.

public enum AcStatus { Off = 0, Replay = 1, Live = 2, Pause = 3 }

public enum AcSessionType
{
    Unknown = -1, Practice = 0, Qualify = 1, Race = 2, Hotlap = 3,
    TimeAttack = 4, Drift = 5, Drag = 6
}

public enum AcFlagType
{
    None = 0, Blue = 1, Yellow = 2, Black = 3, White = 4, Checkered = 5, Penalty = 6
}

[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
public struct AcPhysics
{
    public int PacketId;
    public float Gas;
    public float Brake;
    public float Fuel;
    public int Gear;            // 0 = R, 1 = N, 2 = 1. Gang …
    public int Rpms;
    public float SteerAngle;
    public float SpeedKmh;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] Velocity;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] AccG;       // x = lateral, y = vertikal, z = longitudinal
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] WheelSlip;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] WheelLoad;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] WheelsPressure;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] WheelAngularSpeed;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreWear;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreDirtyLevel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreCoreTemperature;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] CamberRad;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SuspensionTravel;
    public float Drs;
    public float Tc;
    public float Heading;
    public float Pitch;
    public float Roll;
    public float CgHeight;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)] public float[] CarDamage;
    public int NumberOfTyresOut;
    public int PitLimiterOn;
    public float Abs;
    public float KersCharge;
    public float KersInput;
    public int AutoShifterOn;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public float[] RideHeight;
    public float TurboBoost;
    public float Ballast;
    public float AirDensity;
    public float AirTemp;
    public float RoadTemp;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] LocalAngularVelocity;
    public float FinalFF;
    public float PerformanceMeter;
    public int EngineBrake;
    public int ErsRecoveryLevel;
    public int ErsPowerLevel;
    public int ErsHeatCharging;
    public int ErsIsCharging;
    public float KersCurrentKJ;
    public int DrsAvailable;
    public int DrsEnabled;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] BrakeTemp;
    public float Clutch;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreTempI;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreTempM;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreTempO;
    public int IsAiControlled;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public float[] TyreContactPoint;   // [4][3]
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public float[] TyreContactNormal;  // [4][3]
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)] public float[] TyreContactHeading; // [4][3]
    public float BrakeBias;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] LocalVelocity;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
public struct AcGraphics
{
    public int PacketId;
    public AcStatus Status;
    public AcSessionType Session;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string CurrentTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string LastTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string BestTime;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string Split;
    public int CompletedLaps;
    public int Position;
    public int ICurrentTime;    // ms
    public int ILastTime;       // ms
    public int IBestTime;       // ms
    public float SessionTimeLeft;
    public float DistanceTraveled;
    public int IsInPit;
    public int CurrentSectorIndex;
    public int LastSectorTime;
    public int NumberOfLaps;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string TyreCompound;
    public float ReplayTimeMultiplier;
    public float NormalizedCarPosition;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)] public float[] CarCoordinates;
    public float PenaltyTime;
    public AcFlagType Flag;
    public int IdealLineOn;
    public int IsInPitLane;
    public float SurfaceGrip;
    public int MandatoryPitDone;
    public float WindSpeed;
    public float WindDirection;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
public struct AcStatic
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string SmVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 15)] public string AcVersion;
    public int NumberOfSessions;
    public int NumCars;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string CarModel;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string Track;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string PlayerName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string PlayerSurname;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string PlayerNick;
    public int SectorCount;
    public float MaxTorque;
    public float MaxPower;
    public int MaxRpm;
    public float MaxFuel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] SuspensionMaxTravel;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)] public float[] TyreRadius;
    public float MaxTurboBoost;
    public float Deprecated1;
    public float Deprecated2;
    public int PenaltiesEnabled;
    public float AidFuelRate;
    public float AidTireRate;
    public float AidMechanicalDamage;
    public int AidAllowTyreBlankets;
    public float AidStability;
    public int AidAutoClutch;
    public int AidAutoBlip;
    public int HasDrs;
    public int HasErs;
    public int HasKers;
    public float KersMaxJ;
    public int EngineBrakeSettingsCount;
    public int ErsPowerControllerCount;
    public float TrackSplineLength;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string TrackConfiguration;
    public float ErsMaxJ;
    public int IsTimedRace;
    public int HasExtraLap;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string CarSkin;
    public int ReversedGridPositions;
    public int PitWindowStart;
    public int PitWindowEnd;
}
