using DDLink.Core;

namespace DDLink.ServerTests;

/// <summary>
/// The stewards' bans on a real server: within ten seconds of a ban on the platform the SteamID is in the
/// server's blacklist.txt, the server has reloaded it, the driver is kicked and cannot join again.
/// </summary>
public class BanTests
{
    private static async Task EventuallyAsync(Func<bool> probe, string what, RaceServer server, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (probe()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out after {seconds} s waiting for {what}.\nblacklist.txt:\n{server.Blacklist}\nServer log:\n{server.Log}");
    }

    [Fact]
    public async Task A_ban_on_the_platform_reaches_the_server_within_ten_seconds_and_a_lifted_ban_lets_the_driver_back_in()
    {
        await using var server = await RaceServer.StartAsync(raceLaps: 50);

        // The plugin asks for the list with a signature over an empty body.
        await EventuallyAsync(() => !server.Receiver.BanRequests.IsEmpty, "the plugin to ask for the ban list", server);
        Assert.True(server.Receiver.BanRequests.TryPeek(out var asked));
        Assert.True(Signer.Verify(RaceServer.Secret, long.Parse(asked.Timestamp!), [], asked.Signature!));

        var (cleo, answer) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Cleo, "Cleo", RaceServer.Car, 1);
        Assert.NotNull(cleo);
        Assert.Equal(FakeDriver.Accepted, answer);

        // The stewards ban Cleo on the platform.
        server.Receiver.Bans = [RaceServer.Cleo];
        await EventuallyAsync(() => server.Blacklist.Contains(RaceServer.Cleo.ToString()), "the SteamID in blacklist.txt", server);
        await EventuallyAsync(() => server.Log.Contains("was banned after reloading blacklist"), "the server to reload the blacklist and kick the driver", server);
        await cleo!.DisposeAsync();

        // Banned: the slot is hers, but the server turns her away. Anna is not banned and gets in.
        var (again, refused) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Cleo, "Cleo", RaceServer.Car, 1);
        Assert.Null(again);
        Assert.NotEqual(FakeDriver.Accepted, refused);
        var (anna, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Anna, "Anna", RaceServer.Car, 0);
        Assert.NotNull(anna);

        // The ban is lifted: the line goes, and Cleo is let in again.
        server.Receiver.Bans = [];
        await EventuallyAsync(() => !server.Blacklist.Contains(RaceServer.Cleo.ToString()), "the SteamID to leave blacklist.txt", server);
        FakeDriver? back = null;
        await EventuallyAsync(() =>
        {
            (back, _) = FakeDriver.JoinAsync(server.GamePort, RaceServer.Cleo, "Cleo", RaceServer.Car, 1).GetAwaiter().GetResult();
            return back != null;
        }, "Cleo to be let in again", server);

        await back!.DisposeAsync();
        await anna!.DisposeAsync();
    }
}
