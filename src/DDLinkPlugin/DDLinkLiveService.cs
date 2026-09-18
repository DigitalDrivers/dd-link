using System.Net.Http.Headers;
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
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly object _lock = new();
    private PositionUpdateIn[] _positions = [];
    private bool[] _hasPosition = [];
    private List<uint>[] _sectors = [];

    public DDLinkLiveService(
        DDLinkConfiguration configuration,
        ACServerConfiguration serverConfiguration,
        SessionManager sessionManager,
        EntryCarManager entryCarManager)
    {
        _configuration = configuration;
        _serverConfiguration = serverConfiguration;
        _sessionManager = sessionManager;
        _entryCarManager = entryCarManager;
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
            _entryCarManager.ClientDisconnected += (client, _) => { lock (_lock) _hasPosition[client.SessionId] = false; };
            _sessionManager.SessionChanged += (_, _) => { lock (_lock) foreach (var sectors in _sectors) sectors.Clear(); };
            Log.Information("DD Link: live feed to {Endpoint} every {Interval} ms", _configuration.LiveEndpoint, _configuration.LiveIntervalMilliseconds);

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_configuration.LiveIntervalMilliseconds));
            var failures = 0;
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
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

    private LiveStateMessage BuildMessage()
    {
        var current = _sessionManager.CurrentSession;
        var kind = current.Configuration.Type switch
        {
            SessionType.Race => SessionKind.Race,
            SessionType.Qualifying => SessionKind.Qualifying,
            _ => SessionKind.Practice,
        };

        var states = new List<(EntryCar Car, ACTcpClient Client, EntryCarResult Result, PositionUpdateIn Position, List<uint> Sectors)>();
        lock (_lock)
        {
            foreach (var car in _entryCarManager.EntryCars)
            {
                if (car.Client is not { } client || !_hasPosition[car.SessionId])
                    continue;
                if (current.Results == null || !current.Results.TryGetValue(car.SessionId, out var result))
                    continue;
                states.Add((car, client, result, _positions[car.SessionId], [.. _sectors[car.SessionId]]));
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
            s.Sectors)).OrderBy(c => c.Position).ToList();

        var server = _serverConfiguration.Server;
        // Not Server.Track: the server rewrites that to "csp/<version>/../<track>".
        var session = new LiveSession(kind, current.Configuration.Name ?? "", _serverConfiguration.CSPTrackOptions.Track, server.TrackConfig,
            current.Configuration.Laps, current.Configuration.Time, current.SessionTimeMilliseconds, current.TimeLeftMilliseconds);
        return new LiveStateMessage(LiveStateMessage.MessageType, _configuration.EventId, _configuration.ServerId, DateTimeOffset.UtcNow, session, cars);
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
