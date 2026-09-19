using AssettoServer.Network.ClientMessages;

namespace DDLinkPlugin;

/// <summary>
/// What lua/telemetry.lua sends from a driver's game once a second. Names, types and sizes mirror the
/// structure in the script; the server derives the message id from them, as the game does.
/// </summary>
[OnlineEvent(Key = "DD_Telemetry")]
public class TelemetryPacket : OnlineEvent<TelemetryPacket>
{
    [OnlineEventField(Name = "fuel")] public float Fuel;
    [OnlineEventField(Name = "maxFuel")] public float MaxFuel;
    [OnlineEventField(Name = "fuelPerLap")] public float FuelPerLap;
    [OnlineEventField(Name = "engineLife")] public float EngineLife;
    [OnlineEventField(Name = "brake")] public float Brake;
    [OnlineEventField(Name = "tyreWear", Size = 4)] public float[] TyreWear = new float[4];
    [OnlineEventField(Name = "tyreTemperature", Size = 4)] public float[] TyreTemperature = new float[4];
    [OnlineEventField(Name = "tyrePressure", Size = 4)] public float[] TyrePressure = new float[4];
    [OnlineEventField(Name = "damage", Size = 4)] public float[] Damage = new float[4];
    [OnlineEventField(Name = "inPitLane")] public bool InPitLane;
}
