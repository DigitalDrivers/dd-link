using System.Text.Json;

namespace DDLink.Core;

/// <summary>
/// The running session as the plugin sees it right now: sent about once a second while a live endpoint
/// is configured, for live timing and the track map. Nothing is stored or retried; a lost message is
/// replaced by the next one.
/// </summary>
public sealed record LiveStateMessage(
    string Type,
    string EventId,
    string ServerId,
    DateTimeOffset SentAt,
    LiveSession Session,
    IReadOnlyList<LiveCar> Cars)
{
    public const string MessageType = "live.state";

    public static byte[] Serialize(LiveStateMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, MessageJson.Options);
}

public sealed record LiveSession(
    SessionKind Kind,
    string Name,
    string Track,
    string TrackLayout,
    int Laps,
    int TimeMinutes,
    long ElapsedMs,
    long TimeLeftMs);

/// <summary>A connected car. Position is the live rank, see <see cref="LiveOrder"/>.</summary>
public sealed record LiveCar(
    byte CarId,
    string SteamId,
    string Name,
    string CarModel,
    int Position,
    uint Laps,
    /// <summary>The race clock when the car last crossed the line; gaps are read off it.</summary>
    uint TotalTimeMs,
    uint? BestLapMs,
    uint? LastLapMs,
    bool Finished,
    /// <summary>Progress along the lap, 0 to 1.</summary>
    float Spline,
    /// <summary>World position on the ground plane, in metres.</summary>
    float X,
    float Z,
    int SpeedKmh,
    /// <summary>-1 reverse, 0 neutral, 1 first gear, ...</summary>
    int Gear,
    int Rpm,
    /// <summary>Throttle in percent.</summary>
    int Gas,
    /// <summary>Sector times of the lap in progress, as far as they are set.</summary>
    IReadOnlyList<uint> Sectors);

/// <summary>What is known about a car before it is ranked.</summary>
public sealed record LiveCarState(byte CarId, uint Laps, uint TotalTimeMs, uint BestLapMs, bool Finished, float Spline);

public static class LiveOrder
{
    /// <summary>
    /// Live rank per car id, 1 = first. In a race the car further along wins: more laps, then (once
    /// finished) the earlier finish, then the progress along the current lap. In practice and qualifying
    /// the best lap decides; cars without a lap time come last.
    /// </summary>
    public static IReadOnlyDictionary<byte, int> Rank(SessionKind kind, IEnumerable<LiveCarState> cars)
    {
        var ordered = kind == SessionKind.Race
            ? cars.OrderByDescending(c => c.Laps)
                .ThenBy(c => c.Finished ? 0 : 1)
                .ThenBy(c => c.Finished ? c.TotalTimeMs : 0)
                .ThenByDescending(c => c.Spline)
                .ThenBy(c => c.CarId)
            : cars.OrderBy(c => c.BestLapMs)
                .ThenBy(c => c.CarId);

        return ordered.Select((c, index) => (c.CarId, Position: index + 1)).ToDictionary(x => x.CarId, x => x.Position);
    }
}
