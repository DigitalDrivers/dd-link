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
