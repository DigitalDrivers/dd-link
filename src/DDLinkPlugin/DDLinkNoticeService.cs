using System.Collections.Concurrent;
using System.Reflection;
using AssettoServer.Network.Tcp;
using AssettoServer.Server;
using DDLink.Core;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DDLinkPlugin;

/// <summary>
/// Notices for the drivers in the game. Every few seconds the plugin asks the platform for new ones (a message
/// of race control, a driver's provisional result, the server closing) and hands each to the game of everybody
/// or of the one driver it is for. The game shows it as a banner (lua/notices.lua) and in the chat. Three
/// notices the server writes itself, because only it sees the moment a driver's game is ready: the briefing of
/// the race, the note for a spectator slot and the driver swap of a crew. Off while no NoticesEndpoint is set.
/// </summary>
public class DDLinkNoticeService : BackgroundService
{
    private readonly DDLinkConfiguration _configuration;
    private readonly EntryCarManager _entryCarManager;
    private readonly SpectatorSlots _spectatorSlots;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    // A server started again for the same event does not repeat what race control said before.
    private readonly NoticeCursor _cursor = new(DateTimeOffset.UtcNow.AddMinutes(-1));
    // Games whose notices script runs; the others get the chat line only.
    private readonly ConcurrentDictionary<ACTcpClient, bool> _ready = new();
    // The last driver of each car, by car id: whom a crew-mate takes over from.
    private readonly ConcurrentDictionary<byte, string> _lastDriver = new();

    public DDLinkNoticeService(
        DDLinkConfiguration configuration,
        EntryCarManager entryCarManager,
        SpectatorSlots spectatorSlots,
        CSPServerScriptProvider scriptProvider,
        CSPClientMessageTypeManager clientMessageTypes)
    {
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        _spectatorSlots = spectatorSlots;
        if (configuration.NoticesEndpoint == "")
            return;
        // Scripts and message types have to be known before the server starts, hence here.
        scriptProvider.AddScript(Assembly.GetExecutingAssembly().GetManifestResourceStream("DDLinkPlugin.lua.notices.lua")!, "dd-notices.lua");
        clientMessageTypes.RegisterOnlineEvent<NoticesReadyPacket>(OnReady);
        _entryCarManager.ClientDisconnected += (client, args) =>
        {
            _ready.TryRemove(client, out _);
            _lastDriver[client.SessionId] = client.Name ?? "";
        };
    }

    private void OnReady(ACTcpClient client, NoticesReadyPacket _)
    {
        if (!_ready.TryAdd(client, true))
            return;
        var now = DateTimeOffset.UtcNow;
        if (_configuration.BriefingTitle != "")
            Send(client, new Notice(0, NoticeKind.Info, _configuration.BriefingTitle, _configuration.BriefingText, null, now));
        if (_spectatorSlots.Contains(client.SessionId))
            Send(client, new Notice(0, NoticeKind.Warning, "SPECTATOR SLOT", "You watch from the pits: this car takes no part in the race.", null, now));
        else if (client.EntryCar.AllowedGuids.Count > 1 && _lastDriver.TryGetValue(client.SessionId, out var previous) && previous != "" && previous != client.Name)
            Send(client, new Notice(0, NoticeKind.Info, "DRIVER SWAP", $"You take over the car from {previous}.", null, now));
    }

    private void Send(ACTcpClient client, Notice notice)
    {
        var fitted = notice.Fitted();
        if (_ready.ContainsKey(client))
            client.SendPacket(new NoticePacket { Kind = (byte)fitted.Kind, Title = fitted.Title, Text = fitted.Text });
        client.SendChatMessage(notice.ChatText);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_configuration.NoticesEndpoint == "")
            return;
        Log.Information("DD Link: notices from {Endpoint} every {Interval} s", _configuration.NoticesEndpoint, _configuration.NoticesIntervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_configuration.NoticesIntervalSeconds));
        var failing = false;
        do
        {
            try
            {
                await DeliverAsync(stoppingToken);
                failing = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Say it once, not every few seconds.
                if (!failing) Log.Warning("DD Link: notices could not be fetched: {Message}", ex.Message);
                failing = true;
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DeliverAsync(CancellationToken ct)
    {
        // Signed like a message, over an empty body, the way the ban list is asked for.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_configuration.NoticesEndpoint}?eventId={Uri.EscapeDataString(_configuration.EventId)}&after={_cursor.After}");
        request.Headers.Add(Signer.TimestampHeader, timestamp.ToString());
        request.Headers.Add(Signer.SignatureHeader, Signer.Sign(_configuration.Secret, timestamp, []));
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var notices = NoticeList.Parse(await response.Content.ReadAsStringAsync(ct)) ?? throw new InvalidDataException("the answer is no notice list");
        foreach (var notice in _cursor.Take(notices))
        {
            var clients = _entryCarManager.ConnectedCars.Values
                .Select(car => car.Client)
                .OfType<ACTcpClient>()
                .Where(client => notice.SteamId == null || client.Guid == notice.SteamId)
                .ToList();
            foreach (var client in clients)
                Send(client, notice);
            Log.Information("DD Link: notice {Id} ({Kind}) to {Count} drivers: {Text}", notice.Id, notice.Kind, clients.Count, notice.ChatText);
        }
    }
}
