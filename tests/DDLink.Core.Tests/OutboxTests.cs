using System.Text;
using DDLink.Core;

namespace DDLink.Core.Tests;

public sealed class OutboxTests : IDisposable
{
    private const string Secret = "test-secret";
    private readonly string _spool = Path.Combine(Path.GetTempPath(), "dd-link-tests", Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly int _port = TestReceiver.FreePort();

    private Outbox NewOutbox() => new(_spool, TestReceiver.EndpointFor(_port), Secret, _http);
    private static byte[] Body(string text = "{\"id\":\"abc\"}") => Encoding.UTF8.GetBytes(text);

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_spool)) Directory.Delete(_spool, recursive: true);
    }

    [Fact]
    public async Task Delivers_exactly_once_after_the_platform_comes_back()
    {
        var outbox = NewOutbox();
        await outbox.EnqueueAsync(Guid.NewGuid(), Body());

        // Platform down: nothing is delivered, the message stays on disk.
        Assert.Equal(0, await outbox.FlushAsync());
        Assert.Equal(1, outbox.PendingCount);

        using var receiver = new TestReceiver(_port);
        Assert.Equal(1, await outbox.FlushAsync());
        Assert.Equal(0, outbox.PendingCount);

        // A further flush must not send it again.
        Assert.Equal(0, await outbox.FlushAsync());
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task Pending_messages_survive_a_restart()
    {
        await NewOutbox().EnqueueAsync(Guid.NewGuid(), Body());

        using var receiver = new TestReceiver(_port);
        var afterRestart = NewOutbox();

        Assert.Equal(1, afterRestart.PendingCount);
        Assert.Equal(1, await afterRestart.FlushAsync());
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task Every_request_carries_a_valid_signature_over_timestamp_and_body()
    {
        using var receiver = new TestReceiver(_port);
        var outbox = NewOutbox();
        await outbox.EnqueueAsync(Guid.NewGuid(), Body("{\"id\":\"signed\"}"));
        await outbox.FlushAsync();

        var request = Assert.Single(receiver.Requests);
        Assert.Equal("{\"id\":\"signed\"}", Encoding.UTF8.GetString(request.Body));
        Assert.True(Signer.Verify(Secret, long.Parse(request.Timestamp!), request.Body, request.Signature!));
    }

    [Fact]
    public async Task A_duplicate_answer_counts_as_delivered()
    {
        using var receiver = new TestReceiver(_port) { StatusCode = 409 };
        var outbox = NewOutbox();
        await outbox.EnqueueAsync(Guid.NewGuid(), Body());

        Assert.Equal(1, await outbox.FlushAsync());
        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(0, outbox.FailedCount);
    }

    [Fact]
    public async Task A_rejected_message_is_moved_aside_and_never_sent_again()
    {
        using var receiver = new TestReceiver(_port) { StatusCode = 401 };
        var outbox = NewOutbox();
        await outbox.EnqueueAsync(Guid.NewGuid(), Body());

        Assert.Equal(0, await outbox.FlushAsync());
        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(1, outbox.FailedCount);

        await outbox.FlushAsync();
        Assert.Single(receiver.Requests);
    }

    [Fact]
    public async Task A_server_error_keeps_the_message_for_a_later_attempt()
    {
        using var receiver = new TestReceiver(_port) { StatusCode = 503 };
        var outbox = NewOutbox();
        await outbox.EnqueueAsync(Guid.NewGuid(), Body());

        Assert.Equal(0, await outbox.FlushAsync());
        Assert.Equal(1, outbox.PendingCount);

        receiver.StatusCode = 200;
        Assert.Equal(1, await outbox.FlushAsync());
        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(2, receiver.Requests.Count);
    }

    [Fact]
    public void Retry_delay_grows_and_is_capped_at_five_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), Outbox.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(15), Outbox.RetryDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(60), Outbox.RetryDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(5), Outbox.RetryDelay(4));
        Assert.Equal(TimeSpan.FromMinutes(5), Outbox.RetryDelay(40));
    }
}
