using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDLink.Core;

/// <summary>One message per finished session: classification plus everything recorded during it.</summary>
public sealed record SessionCompletedMessage(
    Guid Id,
    string Type,
    string EventId,
    string ServerId,
    DateTimeOffset SentAt,
    SessionInfo Session,
    IReadOnlyList<ClassificationEntry> Classification,
    IReadOnlyList<LapEntry> Laps,
    IReadOnlyList<CollisionEntry> Collisions,
    IReadOnlyList<ConnectionEntry> Connections)
{
    public const string MessageType = "session.completed";
}

public sealed record SessionInfo(
    SessionKind Kind,
    string Name,
    string Track,
    string TrackLayout,
    int Laps,
    int TimeMinutes,
    long DurationMs);

public sealed record ClassificationEntry(
    int Position,
    DriverStatus Status,
    string SteamId,
    string Name,
    string CarModel,
    string Skin,
    uint Laps,
    uint TotalTimeMs,
    uint? BestLapMs,
    /// <summary>Starting position among the drivers of this message (1 = pole). Null outside races.</summary>
    int? GridPosition,
    /// <summary>Everyone who drove the car, with their laps. SteamId and Name above are the driver who had the car last.</summary>
    IReadOnlyList<CrewMember> Crew);

public sealed record LapEntry(string SteamId, uint LapNumber, uint LapTimeMs, int Cuts, long SessionTimeMs);

/// <summary>OtherSteamId is null for contact with the environment.</summary>
/// <param name="SpeedKmh">Speed of the impact itself: the game reports it, and both cars report the same one.</param>
/// <param name="RelX">Where the impact hit this car, in the car's own coordinates: sideways,</param>
/// <param name="RelY">upwards,</param>
/// <param name="RelZ">and along the car. Which end of the car is which sign is the game's business; the
/// platform only compares the two reports of one contact with each other.</param>
/// <param name="BrakeTest">This car braked hard for no reason just before the contact (see <see cref="BrakeTests"/>).</param>
/// <param name="OtherBrakeTest">The other car did.</param>
public sealed record CollisionEntry(string SteamId, string? OtherSteamId, float SpeedKmh, float X, float Y, float Z, float RelX, float RelY, float RelZ, long SessionTimeMs,
    bool BrakeTest = false, bool OtherBrakeTest = false);

public sealed record ConnectionEntry(string SteamId, string Name, bool Connected, long SessionTimeMs);

public static class MessageJson
{
    /// <summary>camelCase, enums as strings. SteamIDs are strings because they exceed JavaScript's safe integer range.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static byte[] Serialize(SessionCompletedMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, Options);
}
