using AssettoServer.Server.Configuration;

namespace DDLinkPlugin;

/// <summary>
/// Which slots of the entry list are for watching (SPECTATOR_MODE other than 0). Read from the entry
/// list itself: the server's own copy on the car is set back to 0 whenever a driver takes the slot.
/// </summary>
public class SpectatorSlots
{
    private readonly HashSet<int> _slots;

    public SpectatorSlots(ACServerConfiguration configuration)
    {
        _slots = configuration.EntryList.Cars
            .Select((entry, index) => (entry, index))
            .Where(x => x.entry.SpectatorMode != 0)
            .Select(x => x.index)
            .ToHashSet();
    }

    public bool Contains(int sessionId) => _slots.Contains(sessionId);
}
