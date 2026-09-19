using AssettoServer.Server.Configuration;
using DDLink.Core;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace DDLinkPlugin;

/// <summary>
/// Keeps the server's blacklist in step with the bans of the platform: asks for the list every few seconds
/// and rewrites the platform's part of the blacklist file when it has changed. AssettoServer watches that
/// file: it reloads it, turns banned drivers away and kicks those who are connected. A platform that does
/// not answer changes nothing: the bans the server knows stay.
/// </summary>
public class DDLinkBanService : BackgroundService
{
    private readonly DDLinkConfiguration _configuration;
    private readonly string _blacklistPath;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public DDLinkBanService(DDLinkConfiguration configuration, ACServerConfiguration serverConfiguration)
    {
        _configuration = configuration;
        var extra = serverConfiguration.Extra;
        _blacklistPath = extra.UserGroups.GetValueOrDefault(extra.BlacklistUserGroup, "blacklist.txt");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_configuration.BansEndpoint == "")
            return;
        Log.Information("DD Link: bans from {Endpoint} every {Interval} s into {Path}", _configuration.BansEndpoint, _configuration.BansIntervalSeconds, _blacklistPath);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_configuration.BansIntervalSeconds));
        var failing = false;
        do
        {
            try
            {
                await SyncAsync(stoppingToken);
                failing = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Say it once, not every few seconds.
                if (!failing) Log.Warning("DD Link: the ban list could not be fetched, the blacklist stays as it is: {Message}", ex.Message);
                failing = true;
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        // Signed like a message, over an empty body; the event tells the platform which secret to expect.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_configuration.BansEndpoint}?eventId={Uri.EscapeDataString(_configuration.EventId)}");
        request.Headers.Add(Signer.TimestampHeader, timestamp.ToString());
        request.Headers.Add(Signer.SignatureHeader, Signer.Sign(_configuration.Secret, timestamp, []));
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var bans = BanList.Parse(await response.Content.ReadAsStringAsync(ct)) ?? throw new InvalidDataException("the answer is no ban list");
        var file = File.Exists(_blacklistPath) ? await File.ReadAllTextAsync(_blacklistPath, ct) : "";
        var merged = BanList.Merge(file, bans);
        if (merged == file)
            return;
        // Written in place: the server watches this very file.
        await File.WriteAllTextAsync(_blacklistPath, merged, ct);
        Log.Information("DD Link: {Count} bans of the platform written to {Path}", bans.Count, _blacklistPath);
    }
}
