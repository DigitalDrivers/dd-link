using System.Net;
using System.Net.Http.Headers;

namespace DDLink.Core;

/// <summary>
/// Durable delivery of messages to the platform. A message is written to the spool directory first
/// and only removed once the platform has accepted it, so a result survives platform outages and
/// server restarts. Delivery is at-least-once; the platform deduplicates by message id.
/// </summary>
public sealed class Outbox
{
    private readonly string _pendingDir;
    private readonly string _failedDir;
    private readonly Uri _endpoint;
    private readonly string _secret;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    public Outbox(string spoolDirectory, Uri endpoint, string secret, HttpClient http, Action<string>? log = null)
    {
        _pendingDir = Path.Combine(spoolDirectory, "pending");
        _failedDir = Path.Combine(spoolDirectory, "failed");
        _endpoint = endpoint;
        _secret = secret;
        _http = http;
        _log = log ?? (_ => { });
        Directory.CreateDirectory(_pendingDir);
        Directory.CreateDirectory(_failedDir);
    }

    public int PendingCount => Directory.GetFiles(_pendingDir, "*.json").Length;
    public int FailedCount => Directory.GetFiles(_failedDir, "*.json").Length;

    /// <summary>Stores the message on disk. The write is atomic: a crash never leaves a half-written file behind.</summary>
    public async Task EnqueueAsync(Guid id, byte[] body, CancellationToken ct = default)
    {
        var target = Path.Combine(_pendingDir, $"{id:N}.json");
        var temp = target + ".tmp";
        await File.WriteAllBytesAsync(temp, body, ct);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>Tries to deliver every pending message once, oldest first. Returns the number delivered.</summary>
    public async Task<int> FlushAsync(CancellationToken ct = default)
    {
        await _flushLock.WaitAsync(ct);
        try
        {
            var delivered = 0;
            var files = new DirectoryInfo(_pendingDir).GetFiles("*.json").OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.Name);
            foreach (var file in files)
            {
                var outcome = await SendAsync(file, ct);
                if (outcome == Outcome.Delivered)
                {
                    file.Delete();
                    delivered++;
                }
                else if (outcome == Outcome.Rejected)
                {
                    file.MoveTo(Path.Combine(_failedDir, file.Name), overwrite: true);
                }
                else
                {
                    // Platform unreachable or overloaded: keep the order, try again later.
                    break;
                }
            }
            return delivered;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>Waiting time before the next delivery attempt after <paramref name="failedAttempts"/> failures in a row.</summary>
    public static TimeSpan RetryDelay(int failedAttempts) => failedAttempts switch
    {
        <= 1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(15),
        3 => TimeSpan.FromSeconds(60),
        _ => TimeSpan.FromMinutes(5),
    };

    private enum Outcome { Delivered, Rejected, Retry }

    private async Task<Outcome> SendAsync(FileInfo file, CancellationToken ct)
    {
        var body = await File.ReadAllBytesAsync(file.FullName, ct);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add(Signer.TimestampHeader, timestamp.ToString());
        request.Headers.Add(Signer.SignatureHeader, Signer.Sign(_secret, timestamp, body));

        try
        {
            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                // 409 = the platform already has this message id.
                return Outcome.Delivered;
            }

            if (IsPermanent(response.StatusCode))
            {
                _log($"Platform rejected message {file.Name} with {(int)response.StatusCode}; moved to failed/");
                return Outcome.Rejected;
            }

            _log($"Platform answered {(int)response.StatusCode} for {file.Name}; will retry");
            return Outcome.Retry;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException || ex is TaskCanceledException))
        {
            _log($"Platform unreachable for {file.Name}: {ex.Message}; will retry");
            return Outcome.Retry;
        }
    }

    // Client errors that a retry cannot fix. 408 and 429 are temporary.
    private static bool IsPermanent(HttpStatusCode status)
        => (int)status is >= 400 and < 500 && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);
}
