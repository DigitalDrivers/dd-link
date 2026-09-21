using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace DDLink.Core.Tests;

/// <summary>A real local HTTP server standing in for the platform.</summary>
public sealed class TestReceiver : IDisposable
{
    public sealed record Received(byte[] Body, string? Timestamp, string? Signature);

    private readonly HttpListener _listener = new();
    public ConcurrentQueue<Received> Requests { get; } = new();
    public int StatusCode { get; set; } = 200;
    /// <summary>What the platform's ban list says (GET .../bans); requests for it are kept apart from the messages.</summary>
    public IReadOnlyList<ulong> Bans { get; set; } = [];
    public ConcurrentQueue<Received> BanRequests { get; } = new();
    /// <summary>What the platform's notices say (GET .../notices), the whole answer; and the query of each request for them.</summary>
    public string Notices { get; set; } = "{\"notices\":[]}";
    public ConcurrentQueue<(string Query, Received Request)> NoticeRequests { get; } = new();

    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static Uri EndpointFor(int port) => new($"http://127.0.0.1:{port}/api/link/messages");

    public TestReceiver(int port)
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; } // listener stopped

            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer);
            if (context.Request.HttpMethod == "GET" && context.Request.Url!.AbsolutePath.EndsWith("/bans"))
            {
                BanRequests.Enqueue(new Received(buffer.ToArray(), context.Request.Headers["X-DD-Timestamp"], context.Request.Headers["X-DD-Signature"]));
                var answer = System.Text.Encoding.UTF8.GetBytes($"{{\"steamIds\":[{string.Join(",", Bans.Select(id => $"\"{id}\""))}]}}");
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(answer);
                context.Response.Close();
                continue;
            }
            if (context.Request.HttpMethod == "GET" && context.Request.Url!.AbsolutePath.EndsWith("/notices"))
            {
                NoticeRequests.Enqueue((context.Request.Url.Query, new Received(buffer.ToArray(), context.Request.Headers["X-DD-Timestamp"], context.Request.Headers["X-DD-Signature"])));
                var answer = System.Text.Encoding.UTF8.GetBytes(Notices);
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(answer);
                context.Response.Close();
                continue;
            }
            Requests.Enqueue(new Received(
                buffer.ToArray(),
                context.Request.Headers["X-DD-Timestamp"],
                context.Request.Headers["X-DD-Signature"]));

            context.Response.StatusCode = StatusCode;
            context.Response.Close();
        }
    }

    public void Dispose() => _listener.Close();
}
