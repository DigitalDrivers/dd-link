namespace DDLink.Core;

public static class SessionReport
{
    /// <summary>
    /// Builds the message for a finished session. Returns null when nobody took part:
    /// empty sessions are not reported.
    /// </summary>
    public static SessionCompletedMessage? Create(
        string eventId,
        string serverId,
        SessionInfo session,
        IEnumerable<DriverResult> results,
        IReadOnlyList<LapEntry> laps,
        IReadOnlyList<CollisionEntry> collisions,
        IReadOnlyList<ConnectionEntry> connections)
    {
        var classified = Classification.Classify(session.Kind, results);

        // The grid index counts empty slots; report the rank among the drivers who took part.
        var gridPositions = classified
            .OrderBy(c => c.Driver.GridIndex).ThenBy(c => c.Driver.CarId)
            .Select((c, index) => (c.Driver.CarId, Position: index + 1))
            .ToDictionary(x => x.CarId, x => x.Position);

        var classification = classified
            .Select(c => new ClassificationEntry(
                c.Position,
                c.Status,
                c.Driver.SteamId.ToString(),
                c.Driver.Name,
                c.Driver.CarModel,
                c.Driver.Skin,
                c.Driver.Laps,
                c.Driver.TotalTimeMs,
                c.Driver.BestLapMs < Classification.NoLapTime ? c.Driver.BestLapMs : null,
                session.Kind == SessionKind.Race ? gridPositions[c.Driver.CarId] : null,
                c.Driver.Crew is { Count: > 0 } crew ? crew : [new CrewMember(c.Driver.SteamId.ToString(), c.Driver.Name, c.Driver.Laps)]))
            .ToList();

        if (classification.Count == 0)
            return null;

        return new SessionCompletedMessage(
            Guid.NewGuid(),
            SessionCompletedMessage.MessageType,
            eventId,
            serverId,
            DateTimeOffset.UtcNow,
            session,
            classification,
            laps,
            collisions,
            connections);
    }
}
