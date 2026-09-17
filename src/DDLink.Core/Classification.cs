namespace DDLink.Core;

public enum SessionKind
{
    Practice,
    Qualifying,
    Race
}

public enum DriverStatus
{
    /// <summary>Race: took the chequered flag. Practice/qualifying: set a lap time.</summary>
    Classified,
    /// <summary>Race: did not take the chequered flag. Practice/qualifying: set no lap time.</summary>
    NotClassified
}

/// <summary>Raw result of one car as the server knows it at the end of a session.</summary>
public sealed record DriverResult(
    byte CarId,
    ulong SteamId,
    string Name,
    string CarModel,
    string Skin,
    uint Laps,
    uint TotalTimeMs,
    uint BestLapMs,
    bool TookChequeredFlag,
    /// <summary>Index of the car in the session's starting grid as built by the server (0 = pole). Counts empty slots.</summary>
    int GridIndex);

public sealed record ClassifiedDriver(int Position, DriverStatus Status, DriverResult Driver);

/// <summary>
/// Computes the final order of a session. The server's own position field is only refreshed when a
/// lap is completed, so the order is derived from laps and times instead.
/// Drivers who never connected are not part of the input: the platform knows the entry list and
/// marks them as "did not start".
///
/// Ties: in a race the driver who started further ahead wins (very common among first-lap
/// retirements, who all have 0 laps and no time). The server builds that grid from the qualifying
/// order, or from the entry list order when there was no qualifying. In practice and qualifying a
/// tie follows the entry list order, because that is how the server itself orders the race grid.
/// </summary>
public static class Classification
{
    /// <summary>The server's placeholder for "no lap time set".</summary>
    public const uint NoLapTime = 999_999_999;

    public static IReadOnlyList<ClassifiedDriver> Classify(SessionKind kind, IEnumerable<DriverResult> results)
    {
        // Empty entry list slots carry SteamId 0.
        var drivers = results.Where(r => r.SteamId != 0);

        var ordered = kind == SessionKind.Race
            ? drivers
                .OrderBy(r => r.TookChequeredFlag ? 0 : 1)
                .ThenByDescending(r => r.Laps)
                .ThenBy(r => r.TotalTimeMs)
                .ThenBy(r => r.GridIndex)
                .ThenBy(r => r.CarId)
            : drivers
                .OrderBy(r => r.BestLapMs)
                .ThenBy(r => r.CarId);

        return ordered
            .Select((r, index) => new ClassifiedDriver(index + 1, StatusOf(kind, r), r))
            .ToList();
    }

    private static DriverStatus StatusOf(SessionKind kind, DriverResult r)
    {
        var classified = kind == SessionKind.Race ? r.TookChequeredFlag : r.BestLapMs < NoLapTime;
        return classified ? DriverStatus.Classified : DriverStatus.NotClassified;
    }
}
