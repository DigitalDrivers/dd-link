using DDLink.Core;

namespace DDLink.ServerTests;

/// <summary>
/// Notices of the platform on a real server: the plugin asks for them with a signature, hands a message of
/// race control to every driver and a driver's own notice to that driver only, and never repeats one.
/// </summary>
public class NoticeTests
{
    private static async Task EventuallyAsync(Func<bool> probe, string what, RaceServer server, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (probe()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out after {seconds} s waiting for {what}.\nServer log:\n{server.Log}");
    }

    [Fact]
    public async Task Race_control_reaches_every_driver_and_a_result_only_its_driver_once()
    {
        await using var server = await RaceServer.StartAsync(raceLaps: 50);
        await EventuallyAsync(() => !server.Receiver.NoticeRequests.IsEmpty, "the plugin to ask for notices", server);
        Assert.True(server.Receiver.NoticeRequests.TryPeek(out var asked));
        Assert.Contains("eventId=server-test-event", asked.Query);
        Assert.True(Signer.Verify(RaceServer.Secret, long.Parse(asked.Request.Timestamp!), [], asked.Request.Signature!));

        var (anna, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Anna, "Anna", RaceServer.Car, 0);
        var (cleo, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Cleo, "Cleo", RaceServer.Car, 1);
        Assert.NotNull(anna);
        Assert.NotNull(cleo);

        var now = DateTimeOffset.UtcNow.ToString("O");
        server.Receiver.Notices = $$"""
            {"notices":[
              {"id":1,"kind":"race-control","title":"RACE CONTROL","text":"Said before this server started.","steamId":null,"createdAt":"2020-01-01T00:00:00Z"},
              {"id":2,"kind":"race-control","title":"RACE CONTROL","text":"Track limits at turn 1 are enforced.","steamId":null,"createdAt":"{{now}}"},
              {"id":3,"kind":"result","title":"P2 OF 2","text":"Provisional until Thursday 19:10.","steamId":"{{RaceServer.Cleo}}","createdAt":"{{now}}"}
            ]}
            """;

        await EventuallyAsync(() => cleo!.Chat.Contains("P2 OF 2: Provisional until Thursday 19:10."), "Cleo's result in her chat", server);
        await EventuallyAsync(() => anna!.Chat.Contains("RACE CONTROL: Track limits at turn 1 are enforced."), "race control in Anna's chat", server);
        Assert.Contains("RACE CONTROL: Track limits at turn 1 are enforced.", cleo!.Chat);
        Assert.DoesNotContain(anna!.Chat, line => line.StartsWith("P2 OF 2"));
        Assert.DoesNotContain(anna.Chat, line => line.Contains("Said before this server started."));

        // The plugin asks for what came after the last one, and delivers nothing twice.
        await EventuallyAsync(() => server.Receiver.NoticeRequests.Any(r => r.Query.Contains("after=3")), "a request for notices after the third", server);
        await Task.Delay(1500);
        Assert.Single(anna.Chat, line => line.StartsWith("RACE CONTROL: Track limits"));

        await anna.DisposeAsync();
        await cleo.DisposeAsync();
    }
}
