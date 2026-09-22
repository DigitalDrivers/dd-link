using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Incoming;
using DDLink.Core;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DDLinkPlugin;

/// <summary>
/// Records laps, collisions and connections during a session and hands one signed message per
/// finished session to the outbox.
/// </summary>
public class DDLinkService : BackgroundService
{
    private readonly DDLinkConfiguration _configuration;
    private readonly ACServerConfiguration _serverConfiguration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly SpectatorSlots _spectators;
    private readonly BrakeTests _brakeTests;
    private readonly Outbox _outbox;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly object _lock = new();
    private List<LapEntry> _laps = [];
    private List<Contact> _collisions = [];
    private List<ConnectionEntry> _connections = [];
    private StintLedger _ledger = new();
    private readonly SemaphoreSlim _wakeUp = new(0);

    // A contact as it happened: which cars, when on the server's clock, and whether either braked for no reason.
    private sealed record Contact(CollisionEntry Entry, byte Car, byte? Other, long AtMs, bool Braked, bool OtherBraked);

    // Whoever is in the pit lane from shortly before to 20 seconds after a contact was slowing down for the pit entry.
    private bool PittedAround(byte car, long atMs) => _brakeTests.UsedThePitLane(car, atMs - 5_000, atMs + 20_000);

    public DDLinkService(
        DDLinkConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        SessionManager sessionManager,
        EntryCarManager entryCarManager,
        SpectatorSlots spectators,
        BrakeTests brakeTests)
    {
        _spectators = spectators;
        _brakeTests = brakeTests;
        _configuration = configuration;
        _serverConfiguration = serverConfiguration;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
        _outbox = new Outbox(configuration.SpoolDirectory, new Uri(configuration.Endpoint), configuration.Secret, _http,
            message => Log.Warning("DD Link: {Message}", message));

        // The session manager subscribed to ClientConnected in its own constructor, before ours ran, so its
        // handler runs first: it starts the slot's result from zero for a new driver, then we restore it.
        _entryCarManager.ClientConnected += OnClientConnected;
        _entryCarManager.ClientDisconnected += OnClientDisconnected;
        _sessionManager.SessionChanged += OnSessionChanged;
    }

    private static CarSnapshot SnapshotOf(EntryCarResult r) => new(r.NumLaps, r.TotalTime, r.BestLap, r.LastLap, r.HasCompletedLastLap, r.RacePos);

    private long SessionTime => _sessionManager.CurrentSession.SessionTimeMilliseconds;

    // Spectator slots (SPECTATOR_MODE in the entry list) are not cars of the race: nothing they do is reported.
    private bool IsSpectator(ACTcpClient client) => _spectators.Contains(client.SessionId);

    private void OnClientConnected(ACTcpClient client, EventArgs args)
    {
        if (IsSpectator(client))
            return;
        client.LapCompleted += OnLapCompleted;
        client.Collision += OnCollision;
        StintLedger ledger;
        lock (_lock)
        {
            _connections.Add(new ConnectionEntry(client.Guid.ToString(), client.Name ?? "", true, SessionTime));
            ledger = _ledger;
        }

        // Driver swap: a crew-mate takes over the car and continues with what it had achieved.
        var before = ledger.DriverJoined(client.SessionId, client.Guid, client.Name ?? "");
        var result = _sessionManager.CurrentSession.Results?[client.SessionId];
        if (before == null || result == null)
            return;
        if (result.Guid != client.Guid)
        {
            Log.Warning("DD Link: slot {Slot} still belongs to another driver, the car's laps were not restored", client.SessionId);
            return;
        }
        result.NumLaps = before.Laps;
        result.TotalTime = before.TotalTimeMs;
        result.BestLap = before.BestLapMs;
        result.LastLap = before.LastLapMs;
        result.HasCompletedLastLap = before.TookChequeredFlag;
        result.RacePos = before.RacePos;
        Log.Information("DD Link: {Name} took over car {Slot} with {Laps} laps", client.Name, client.SessionId, before.Laps);
    }

    private void OnClientDisconnected(ACTcpClient client, EventArgs args)
    {
        if (IsSpectator(client))
            return;
        StintLedger ledger;
        lock (_lock)
        {
            _connections.Add(new ConnectionEntry(client.Guid.ToString(), client.Name ?? "", false, SessionTime));
            ledger = _ledger;
        }
        _brakeTests.Forget(client.SessionId);
        var result = _sessionManager.CurrentSession.Results?[client.SessionId];
        if (result != null && result.Guid == client.Guid)
            ledger.DriverLeft(client.SessionId, SnapshotOf(result));
    }

    private void OnLapCompleted(ACTcpClient client, LapCompletedEventArgs args)
    {
        // The session manager has already counted this lap when the event fires.
        var result = _sessionManager.CurrentSession.Results?[client.SessionId];
        var lapNumber = result?.NumLaps ?? 0;
        StintLedger ledger;
        lock (_lock)
        {
            _laps.Add(new LapEntry(client.Guid.ToString(), lapNumber, args.Packet.LapTime, args.Packet.Cuts, SessionTime));
            ledger = _ledger;
        }
        if (result != null)
            ledger.LapCompleted(client.SessionId, client.Guid, client.Name ?? "", SnapshotOf(result));
    }

    private void OnPositionUpdate(EntryCar sender, in PositionUpdateIn update) => _brakeTests.Moved(sender.SessionId,
        _sessionManager.ServerTimeMilliseconds, update.Position, update.Velocity, update.Rotation.X, update.NormalizedPosition);

    private void OnCollision(ACTcpClient client, CollisionEventArgs args)
    {
        var other = args.TargetCar?.Client?.Guid.ToString();
        var now = _sessionManager.ServerTimeMilliseconds;
        // Judged now, while the seconds before the contact are still known; the pit lane is looked at in the report.
        var target = args.TargetCar?.SessionId;
        var braked = target is { } t && _brakeTests.BrakedWithoutReason(client.SessionId, t, now);
        var otherBraked = target is { } o && _brakeTests.BrakedWithoutReason(o, client.SessionId, now);
        _brakeTests.Collided(client.SessionId, target, now);
        if (braked || otherBraked)
            Log.Information("DD Link: {Name} braked hard for no reason before the contact between {Reporter} and {Other}",
                braked ? client.Name : args.TargetCar?.Client?.Name, client.Name, args.TargetCar?.Client?.Name);

        var entry = new CollisionEntry(client.Guid.ToString(), other, args.Speed,
            args.Position.X, args.Position.Y, args.Position.Z,
            args.RelPosition.X, args.RelPosition.Y, args.RelPosition.Z, SessionTime);
        lock (_lock)
        {
            _collisions.Add(new Contact(entry, client.SessionId, target, now, braked, otherBraked));
        }
    }

    private void OnSessionChanged(SessionManager sender, SessionChangedEventArgs args)
    {
        List<LapEntry> laps;
        List<Contact> contacts;
        List<ConnectionEntry> connections;
        StintLedger ledger;
        var nextLedger = new StintLedger();
        // Drivers who stay connected start the new session in their cars.
        foreach (var car in _entryCarManager.EntryCars)
        {
            if (!_spectators.Contains(car.SessionId) && car.Client is { } connected)
                nextLedger.DriverJoined(car.SessionId, connected.Guid, connected.Name ?? "");
        }
        lock (_lock)
        {
            (laps, _laps) = (_laps, []);
            (contacts, _collisions) = (_collisions, []);
            (connections, _connections) = (_connections, []);
            (ledger, _ledger) = (_ledger, nextLedger);
        }

        var previous = args.PreviousSession;
        if (previous?.Results == null || previous.Configuration.Type == SessionType.Booking)
            return;

        var kind = previous.Configuration.Type switch
        {
            SessionType.Race => SessionKind.Race,
            SessionType.Qualifying => SessionKind.Qualifying,
            _ => SessionKind.Practice,
        };

        // Starting grid as the server built it: qualifying order, or entry list order without qualifying.
        var gridIndex = (previous.Grid ?? _entryCarManager.EntryCars)
            .Select((car, index) => (car.SessionId, index))
            .ToDictionary(x => x.SessionId, x => x.index);

        var results = previous.Results.Where(pair => !_spectators.Contains(pair.Key)).Select(pair =>
        {
            var car = _entryCarManager.EntryCars[pair.Key];
            var r = pair.Value;
            return new DriverResult(pair.Key, r.Guid, r.Name, car.Model, car.Skin, r.NumLaps, r.TotalTime, r.BestLap,
                r.HasCompletedLastLap, gridIndex[pair.Key], ledger.CrewOf(pair.Key));
        });

        var server = _serverConfiguration.Server;
        // The server rewrites Server.Track to "csp/<version>/../<track>" to demand a CSP version; report the plain folder name.
        var session = new SessionInfo(kind, previous.Configuration.Name ?? "", _serverConfiguration.CSPTrackOptions.Track, server.TrackConfig,
            previous.Configuration.Laps, previous.Configuration.Time, previous.SessionTimeMilliseconds);

        var collisions = contacts.Select(c => c.Entry with
        {
            BrakeTest = c.Braked && !PittedAround(c.Car, c.AtMs),
            OtherBrakeTest = c.OtherBraked && c.Other is { } other && !PittedAround(other, c.AtMs),
        }).ToList();

        var message = SessionReport.Create(_configuration.EventId, _configuration.ServerId, session, results, laps, collisions, connections);
        if (message != null)
            _ = EnqueueAsync(message);
    }

    private async Task EnqueueAsync(SessionCompletedMessage message)
    {
        try
        {
            await _outbox.EnqueueAsync(message.Id, MessageJson.Serialize(message));
            Log.Information("DD Link: queued {Kind} result with {Count} drivers", message.Session.Kind, message.Classification.Count);
            _wakeUp.Release();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DD Link: could not store the session result");
        }
    }

    // Watches the cars, then the delivery loop: send what is pending, back off while the platform is unreachable.
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Every car's movements, to tell a brake test from a car that had to slow down. The entry cars exist once the server has started.
        while (_entryCarManager.EntryCars.Length == 0)
            await Task.Delay(500, stoppingToken);
        foreach (var car in _entryCarManager.EntryCars.Where(car => !_spectators.Contains(car.SessionId)))
            car.PositionUpdateReceived += OnPositionUpdate;

        var failedAttempts = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _outbox.FlushAsync(stoppingToken);
                failedAttempts = _outbox.PendingCount == 0 ? 0 : failedAttempts + 1;

                if (failedAttempts == 0)
                    await _wakeUp.WaitAsync(stoppingToken);
                else
                    await _wakeUp.WaitAsync(Outbox.RetryDelay(failedAttempts), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DD Link: delivery loop failed");
                await Task.Delay(Outbox.RetryDelay(3), stoppingToken);
            }
        }
    }
}
