namespace DDLink.Core;

/// <summary>What a car has achieved in the running session, as the server keeps it per slot.</summary>
public sealed record CarSnapshot(uint Laps, uint TotalTimeMs, uint BestLapMs, uint LastLapMs, bool TookChequeredFlag, uint RacePos);

/// <summary>A driver who drove a car in a session, with the laps they completed in it.</summary>
public sealed record CrewMember(string SteamId, string Name, uint Laps);

/// <summary>
/// Keeps a car's result across driver swaps. Assetto Corsa has no driver swap: a slot can list several
/// SteamIDs, and the swap is one driver leaving and a crew-mate joining the same slot. The server starts
/// the slot's result from zero when a different driver joins, so the ledger remembers what the car had
/// achieved and hands it to the plugin to restore. The total time is the race clock at the last
/// completed lap, so the time the car stood in the pits counts by itself; what matters is the lap count.
/// One ledger per session. Safe to call from several threads.
/// </summary>
public sealed class StintLedger
{
    private sealed class Stint(ulong steamId, string name)
    {
        public ulong SteamId { get; } = steamId;
        public string Name { get; set; } = name;
        public uint Laps { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<byte, CarSnapshot> _snapshots = new();
    private readonly Dictionary<byte, List<Stint>> _crews = new();

    /// <summary>
    /// A driver took the car. Returns what the car had achieved before, to be restored into the
    /// server's result of the slot; null when nobody drove the car in this session yet.
    /// </summary>
    public CarSnapshot? DriverJoined(byte carId, ulong steamId, string name)
    {
        lock (_lock)
        {
            StintOf(carId, steamId, name);
            return _snapshots.GetValueOrDefault(carId);
        }
    }

    /// <summary>The driver completed a lap; <paramref name="after"/> is the car's result including it.</summary>
    public void LapCompleted(byte carId, ulong steamId, string name, CarSnapshot after)
    {
        lock (_lock)
        {
            StintOf(carId, steamId, name).Laps++;
            _snapshots[carId] = after;
        }
    }

    /// <summary>The driver left the car with this result.</summary>
    public void DriverLeft(byte carId, CarSnapshot snapshot)
    {
        lock (_lock) _snapshots[carId] = snapshot;
    }

    /// <summary>Everyone who drove the car in this session, in the order of their first stint.</summary>
    public IReadOnlyList<CrewMember> CrewOf(byte carId)
    {
        lock (_lock)
        {
            return _crews.TryGetValue(carId, out var crew)
                ? crew.Select(s => new CrewMember(s.SteamId.ToString(), s.Name, s.Laps)).ToList()
                : [];
        }
    }

    private Stint StintOf(byte carId, ulong steamId, string name)
    {
        if (!_crews.TryGetValue(carId, out var crew))
            _crews[carId] = crew = [];

        var stint = crew.Find(s => s.SteamId == steamId);
        if (stint == null)
            crew.Add(stint = new Stint(steamId, name));
        else if (name.Length > 0)
            stint.Name = name;
        return stint;
    }
}
