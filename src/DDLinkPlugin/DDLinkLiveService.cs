using System.Net.Http.Headers;
using System.Reflection;
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
/// The live feed: once a second the state of every connected car goes to the platform for live timing
/// and the track map. Off while no LiveEndpoint is configured. Best effort: a message that cannot be
/// delivered is dropped, the next one carries the current state anyway.
/// </summary>
public class DDLinkLiveService : BackgroundService
{
    private readonly DDLinkConfiguration _configuration;
    private readonly ACServerConfiguration _serverConfiguration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly SpectatorSlots _spectatorSlots;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly object _lock = new();
    private PositionUpdateIn[] _positions = [];
    private bool[] _hasPosition = [];
    private List<uint>[] _sectors = [];
    // The latest telemetry per car with the time it arrived; a game that stops reporting is not shown forever.
    private readonly Dictionary<byte, (LiveTelemetry Telemetry, long ReceivedAt)> _telemetry = new();
    private const long TelemetryMaxAgeMs = 5000;
    // Spectators belong in the pits while cars qualify or race: the game puts every connected car on the
    // grid when a race starts, and a parked car on the main straight is an obstacle. When a spectator was
    // last sent back, by car id.
    private readonly Dictionary<byte, long> _sentToPits = new();
    private const long SendToPitsEveryMs = 10_000;
    private long _sessionStartedAt;

    public DDLinkLiveService(
        DDLinkConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        SessionManager sessionManager,
        EntryCarManager entryCarManager,
        CSPServerScriptProvider scriptProvider,
        CSPClientMessageTypeManager clientMessageTypes,
        SpectatorSlots spectatorSlots)
    {
        _spectatorSlots = spectatorSlots;
        _configuration = configuration;
        _serverConfiguration = serverConfiguration;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;

        if (string.IsNullOrEmpty(configuration.LiveEndpoint))
            return;
        // The pit wall: every driver's game runs this script and reports fuel, tyres and damage. Scripts and
        // message types have to be known before the server starts, hence here and not in ExecuteAsync.
        scriptProvider.AddScript(Assembly.GetExecutingAssembly().GetManifestResourceStream("DDLinkPlugin.lua.telemetry.lua")!, "dd-telemetry.lua");
        clientMessageTypes.RegisterOnlineEvent<TelemetryPacket>(OnTelemetry);
    }

    // The server handles a registered message itself and does not pass it on to other drivers.
    private void OnTelemetry(ACTcpClient sender, TelemetryPacket packet)
    {
        var telemetry = LiveTelemetry.Create(packet.Fuel, packet.MaxFuel, packet.FuelPerLap, packet.EngineLife, packet.Brake,
            packet.TyreWear, packet.TyreTemperature, packet.TyrePressure, packet.Damage, packet.InPitLane);
        bool first;
        lock (_lock)
        {
            first = !_telemetry.ContainsKey(sender.SessionId);
            _telemetry[sender.SessionId] = (telemetry, _sessionManager.ServerTimeMilliseconds);
        }
        if (first)
            Log.Information("DD Link: {Name} reports telemetry, {Fuel:F1} of {MaxFuel:F0} litres of fuel", sender.Name, telemetry.FuelLitres, telemetry.MaxFuelLitres);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_configuration.LiveEndpoint))
            return;

        try
        {
            // The entry cars exist once the server has started.
            while (_entryCarManager.EntryCars.Length == 0)
                await Task.Delay(500, stoppingToken);

            var count = _entryCarManager.EntryCars.Length;
            _positions = new PositionUpdateIn[count];
            _hasPosition = new bool[count];
            _sectors = Enumerable.Range(0, count).Select(_ => new List<uint>()).ToArray();
            foreach (var car in _entryCarManager.EntryCars)
                car.PositionUpdateReceived += OnPositionUpdate;
            _entryCarManager.ClientConnected += OnClientConnected;
            _entryCarManager.ClientDisconnected += (client, _) =>
            {
                lock (_lock)
                {
                    _hasPosition[client.SessionId] = false;
                    _telemetry.Remove(client.SessionId);
                }
            };
            _sessionManager.SessionChanged += (_, _) =>
            {
                lock (_lock)
                {
                    foreach (var sectors in _sectors) sectors.Clear();
                    _sentToPits.Clear();
                    _sessionStartedAt = _sessionManager.ServerTimeMilliseconds;
                }
            };
            Log.Information("DD Link: live feed to {Endpoint} every {Interval} ms", _configuration.LiveEndpoint, _configuration.LiveIntervalMilliseconds);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_configuration.LiveIntervalMilliseconds));
            var failures = 0;
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    KeepSpectatorsInThePits();
                    await SendAsync(BuildMessage(), stoppingToken);
                    failures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // Say it once per outage, not once a second.
                    if (failures++ == 0)
                        Log.Warning("DD Link: live feed not delivered: {Message}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Runs on the network thread for every position packet: only keep the latest one.
    private void OnPositionUpdate(EntryCar sender, in PositionUpdateIn update)
    {
        lock (_lock)
        {
            _positions[sender.SessionId] = update;
            _hasPosition[sender.SessionId] = true;
        }
    }

    private void OnClientConnected(ACTcpClient client, EventArgs args)
    {
        client.SectorSplit += (sender, split) =>
        {
            lock (_lock)
            {
                var sectors = _sectors[sender.SessionId];
                if (split.Packet.SplitIndex == 0)
                    sectors.Clear();
                sectors.Add(split.Packet.SplitTime);
            }
        };
        client.LapCompleted += (sender, _) => { lock (_lock) _sectors[sender.SessionId].Clear(); };
    }

    /// <summary>
    /// In qualifying and races, a spectator who is not in the pit lane is sent back to the pit box, again
    /// every ten seconds until the spectator's game reports the pit lane. Without a report from the game
    /// (it needs a few seconds after joining) the spectator is sent back once per session.
    /// </summary>
    private void KeepSpectatorsInThePits()
    {
        if (_sessionManager.CurrentSession.Configuration.Type is not (SessionType.Race or SessionType.Qualifying))
            return;
        var now = _sessionManager.ServerTimeMilliseconds;
        // The game needs a moment to put the cars on the grid; a teleport before that would be undone.
        if (now - _sessionStartedAt < 5000)
            return;

        foreach (var car in _entryCarManager.EntryCars)
        {
            if (!_spectatorSlots.Contains(car.SessionId) || car.Client is not { HasSentFirstUpdate: true } client)
                continue;
            bool? inPitLane;
            bool sentBefore;
            long lastSent;
            lock (_lock)
            {
                inPitLane = _telemetry.TryGetValue(car.SessionId, out var latest) && now - latest.ReceivedAt <= TelemetryMaxAgeMs ? latest.Telemetry.InPitLane : null;
                sentBefore = _sentToPits.TryGetValue(car.SessionId, out lastSent);
            }
            if (inPitLane == true || (inPitLane == null && sentBefore) || (sentBefore && now - lastSent < SendToPitsEveryMs))
                continue;

            client.SendPacket(new TeleportToPitsPacket { SessionId = client.SessionId });
            lock (_lock) _sentToPits[car.SessionId] = now;
            Log.Information("DD Link: sent spectator {Name} back to the pits", client.Name);
        }
    }

    private LiveStateMessage BuildMessage()
    {
        var current = _sessionManager.CurrentSession;
        var kind = current.Configuration.Type switch
        {
            SessionType.Race => SessionKind.Race,
            SessionType.Qualifying => SessionKind.Qualifying,
            _ => SessionKind.Practice,
        };

        var states = new List<(EntryCar Car, ACTcpClient Client, EntryCarResult Result, PositionUpdateIn Position, List<uint> Sectors, LiveTelemetry? Telemetry)>();
        var spectators = new List<LiveSpectator>();
        var now = _sessionManager.ServerTimeMilliseconds;
        lock (_lock)
        {
            foreach (var car in _entryCarManager.EntryCars)
            {
                if (car.Client is not { } client || !_hasPosition[car.SessionId])
                    continue;
                // A spectator slot is not a car of the race.
                if (_spectatorSlots.Contains(car.SessionId))
                {
                    bool? inPitLane = _telemetry.TryGetValue(car.SessionId, out var seen) && now - seen.ReceivedAt <= TelemetryMaxAgeMs ? seen.Telemetry.InPitLane : null;
                    spectators.Add(new LiveSpectator(client.Guid.ToString(), client.Name ?? "", inPitLane));
                    continue;
                }
                if (current.Results == null || !current.Results.TryGetValue(car.SessionId, out var result))
                    continue;
                var telemetry = _telemetry.TryGetValue(car.SessionId, out var latest) && now - latest.ReceivedAt <= TelemetryMaxAgeMs ? latest.Telemetry : null;
                states.Add((car, client, result, _positions[car.SessionId], [.. _sectors[car.SessionId]], telemetry));
            }
        }

        var rank = LiveOrder.Rank(kind, states.Select(s =>
            new LiveCarState(s.Car.SessionId, s.Result.NumLaps, s.Result.TotalTime, s.Result.BestLap, s.Result.HasCompletedLastLap, s.Position.NormalizedPosition)));

        var cars = states.Select(s => new LiveCar(
            s.Car.SessionId,
            s.Client.Guid.ToString(),
            s.Client.Name ?? "",
            s.Car.Model,
            rank[s.Car.SessionId],
            s.Result.NumLaps,
            s.Result.TotalTime,
            s.Result.BestLap < Classification.NoLapTime ? s.Result.BestLap : null,
            s.Result.LastLap < Classification.NoLapTime ? s.Result.LastLap : null,
            s.Result.HasCompletedLastLap,
            s.Position.NormalizedPosition,
            s.Position.Position.X,
            s.Position.Position.Z,
            (int)Math.Round(s.Position.Velocity.Length() * 3.6f),
            s.Position.Gear - 1,
            s.Position.EngineRpm,
            (int)Math.Round(s.Position.Gas / 255f * 100f),
            s.Sectors,
            s.Telemetry)).OrderBy(c => c.Position).ToList();

        var server = _serverConfiguration.Server;
        // Not Server.Track: the server rewrites that to "csp/<version>/../<track>".
        var session = new LiveSession(kind, current.Configuration.Name ?? "", _serverConfiguration.CSPTrackOptions.Track, server.TrackConfig,
            current.Configuration.Laps, current.Configuration.Time, current.SessionTimeMilliseconds, current.TimeLeftMilliseconds);
        return new LiveStateMessage(LiveStateMessage.MessageType, _configuration.EventId, _configuration.ServerId, DateTimeOffset.UtcNow, session, cars, spectators);
    }

    private async Task SendAsync(LiveStateMessage message, CancellationToken ct)
    {
        var body = LiveStateMessage.Serialize(message);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, _configuration.LiveEndpoint);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(Signer.TimestampHeader, timestamp.ToString());
        request.Headers.Add(Signer.SignatureHeader, Signer.Sign(_configuration.Secret, timestamp, body));
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"the platform answered {(int)response.StatusCode}");
    }
}
