using System.Text.Json;
using DDLink.Core;
using DDLink.Core.Tests;

namespace DDLink.ServerTests;

/// <summary>
/// A whole race on a real server with simulated drivers: slots locked to SteamIDs, a driver swap in the
/// middle of the race, the live feed and the final result as the platform receives it.
/// </summary>
public class DriverSwapTests
{
    private static async Task<T> EventuallyAsync<T>(Func<T?> probe, string what, RaceServer server, int seconds = 30) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } found)
                return found;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out waiting for {what}. Server log:\n{server.Log}");
    }

    private static IEnumerable<JsonElement> Messages(RaceServer server, string type) => server.Receiver.Requests
        .Select(r => JsonDocument.Parse(r.Body).RootElement)
        .Where(m => m.GetProperty("type").GetString() == type);

    // The newest live state in which the first car looks the way the test waits for.
    private static JsonElement? LiveCar(RaceServer server, Func<JsonElement, bool> matches)
    {
        var latest = Messages(server, "live.state").LastOrDefault();
        if (latest.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var car in latest.GetProperty("cars").EnumerateArray())
        {
            if (car.GetProperty("carId").GetInt32() == 0 && matches(car))
                return car;
        }
        return null;
    }

    [Fact]
    public async Task A_crew_mate_takes_over_the_car_mid_race_and_the_result_counts_both_stints()
    {
        await using var server = await RaceServer.StartAsync(raceLaps: 4);

        // Slots belong to their SteamIDs: a stranger gets none, although two cars are free.
        var (stranger, answer) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Stranger, "Stranger", RaceServer.Car, 0);
        Assert.Null(stranger);
        Assert.Equal(FakeDriver.NoSlot, answer);

        var (anna, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Anna, "Anna", RaceServer.Car, 0);
        var (cleo, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Cleo, "Cleo", RaceServer.Car, 1);
        Assert.NotNull(anna);
        Assert.NotNull(cleo);

        // Anna drives two laps, Cleo one.
        await Task.Delay(3500); // the grid wait
        await anna.CompleteLapAsync(61_000);
        await cleo.CompleteLapAsync(63_000);
        await anna.CompleteLapAsync(60_500);

        var annasCar = await EventuallyAsync(() => LiveCar(server, car => car.GetProperty("laps").GetInt32() == 2) as object, "Anna's second lap in the live feed", server);
        var live = (JsonElement)annasCar;
        Assert.Equal("Anna", live.GetProperty("name").GetString());
        Assert.Equal(1, live.GetProperty("position").GetInt32());
        Assert.Equal(60_500, live.GetProperty("bestLapMs").GetInt32());
        Assert.Equal(180, live.GetProperty("speedKmh").GetInt32());
        Assert.Equal(4, live.GetProperty("gear").GetInt32());

        // The swap: Anna leaves in the pits, Ben joins the same slot and continues with her two laps.
        await anna.DisposeAsync();
        // The server frees Anna's slot a moment after she has left; Ben asks until he gets it, as a driver would.
        FakeDriver? ben = null;
        byte benAnswer = 0;
        for (var attempt = 0; attempt < 20 && ben == null; attempt++)
        {
            (ben, benAnswer) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Ben, "Ben", RaceServer.Car, 0);
            if (ben == null)
                await Task.Delay(250);
        }
        Assert.True(ben != null, $"Ben was refused with 0x{benAnswer:X2}. Server log:\n{server.Log}");

        var bensCar = (JsonElement)await EventuallyAsync(() => LiveCar(server, car => car.GetProperty("name").GetString() == "Ben") as object, "Ben in the live feed", server);
        Assert.Equal(2, bensCar.GetProperty("laps").GetInt32());
        Assert.Equal(60_500, bensCar.GetProperty("bestLapMs").GetInt32());

        // Ben finishes the race distance; Cleo is a lap short when the race is over.
        await ben.CompleteLapAsync(62_000);
        await ben.CompleteLapAsync(60_100);

        var result = (JsonElement)await EventuallyAsync(() => Messages(server, "session.completed").Select(m => (object)m).FirstOrDefault(), "the race result", server, seconds: 45);
        await ben.DisposeAsync();
        await cleo.DisposeAsync();

        // Every message is signed with the shared secret.
        Assert.All(server.Receiver.Requests, r => Assert.True(Signer.Verify(RaceServer.Secret, long.Parse(r.Timestamp!), r.Body, r.Signature!)));

        Assert.Equal("race", result.GetProperty("session").GetProperty("kind").GetString());
        Assert.Equal("ks_nurburgring", result.GetProperty("session").GetProperty("track").GetString());

        var classification = result.GetProperty("classification");
        Assert.Equal(2, classification.GetArrayLength());
        var winner = classification[0];
        Assert.Equal(RaceServer.Ben.ToString(), winner.GetProperty("steamId").GetString());
        Assert.Equal(4, winner.GetProperty("laps").GetInt32());
        Assert.Equal("classified", winner.GetProperty("status").GetString());
        Assert.Equal(60_100, winner.GetProperty("bestLapMs").GetInt32());
        var crew = winner.GetProperty("crew").EnumerateArray().Select(c => (c.GetProperty("name").GetString(), c.GetProperty("laps").GetInt32())).ToList();
        Assert.Equal([("Anna", 2), ("Ben", 2)], crew);

        var second = classification[1];
        Assert.Equal(RaceServer.Cleo.ToString(), second.GetProperty("steamId").GetString());
        Assert.Equal(1, second.GetProperty("laps").GetInt32());

        // Five laps, each by the driver who drove it. Lap numbers belong to the car: Ben's first lap is its third.
        var laps = result.GetProperty("laps").EnumerateArray()
            .GroupBy(l => l.GetProperty("steamId").GetString()!)
            .ToDictionary(g => g.Key, g => g.Select(l => l.GetProperty("lapNumber").GetInt32()).ToList());
        Assert.Equal([1, 2], laps[RaceServer.Anna.ToString()]);
        Assert.Equal([3, 4], laps[RaceServer.Ben.ToString()]);
        Assert.Equal([1], laps[RaceServer.Cleo.ToString()]);

        // The stewards see the swap in the connections.
        var connections = result.GetProperty("connections").EnumerateArray().Select(c => (c.GetProperty("name").GetString(), c.GetProperty("connected").GetBoolean())).ToList();
        Assert.Contains(("Anna", false), connections);
        Assert.Contains(("Ben", true), connections);

        Capture(server, result);
    }

    [Fact]
    public async Task A_driver_who_drops_out_and_comes_back_keeps_the_laps()
    {
        await using var server = await RaceServer.StartAsync(raceLaps: 3);
        // Anna stays on track the whole time: the server skips a race the moment nobody is connected.
        var (anna, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Anna, "Anna", RaceServer.Car, 0);
        var (cleo, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Cleo, "Cleo", RaceServer.Car, 1);
        Assert.NotNull(anna);
        Assert.NotNull(cleo);
        await Task.Delay(3500); // the grid wait
        await cleo.CompleteLapAsync(62_000);
        await cleo.CompleteLapAsync(61_000);

        // The game crashes; Cleo is back a moment later in her own slot.
        await cleo.DisposeAsync();
        FakeDriver? back = null;
        for (var attempt = 0; attempt < 20 && back == null; attempt++)
        {
            (back, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Cleo, "Cleo", RaceServer.Car, 1);
            if (back == null)
                await Task.Delay(250);
        }
        Assert.True(back != null, $"Cleo could not come back. Server log:\n{server.Log}");
        await back.CompleteLapAsync(60_000);

        var result = (JsonElement)await EventuallyAsync(() => Messages(server, "session.completed").Select(m => (object)m).FirstOrDefault(), "the race result", server, seconds: 45);
        await back.DisposeAsync();
        await anna.DisposeAsync();

        var winner = result.GetProperty("classification")[0];
        Assert.Equal(RaceServer.Cleo.ToString(), winner.GetProperty("steamId").GetString());
        Assert.Equal(3, winner.GetProperty("laps").GetInt32());
        Assert.Equal("classified", winner.GetProperty("status").GetString());
        Assert.Equal(60_000, winner.GetProperty("bestLapMs").GetInt32());
        // One driver, one crew member, all three laps hers.
        var crew = Assert.Single(winner.GetProperty("crew").EnumerateArray());
        Assert.Equal(3, crew.GetProperty("laps").GetInt32());
    }

    [Fact]
    public async Task The_server_ends_a_race_the_moment_nobody_is_connected()
    {
        // Worth knowing for driver swaps: with a single car on the server, the swap itself ends the race.
        await using var server = await RaceServer.StartAsync(raceLaps: 3);
        var (cleo, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Cleo, "Cleo", RaceServer.Car, 1);
        Assert.NotNull(cleo);
        await Task.Delay(3500); // the grid wait
        await cleo.CompleteLapAsync(62_000);
        await cleo.DisposeAsync();

        var result = (JsonElement)await EventuallyAsync(() => Messages(server, "session.completed").Select(m => (object)m).FirstOrDefault(), "the result of the abandoned race", server);
        var only = Assert.Single(result.GetProperty("classification").EnumerateArray());
        Assert.Equal(1, only.GetProperty("laps").GetInt32());
        Assert.Equal("notClassified", only.GetProperty("status").GetString());
        Assert.Contains("Skipping race session: no player connected", server.Log);
    }

    // With DDLINK_CAPTURE_DIR set, the real messages are written there: the platform uses them as fixtures.
    private static void Capture(RaceServer server, JsonElement result)
    {
        if (Environment.GetEnvironmentVariable("DDLINK_CAPTURE_DIR") is not { Length: > 0 } directory)
            return;
        Directory.CreateDirectory(directory);
        var pretty = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(directory, "real-session-completed.json"), JsonSerializer.Serialize(result, pretty));
        // Late in the race, before the session loops and everything is back to zero.
        var live = Messages(server, "live.state").Last(m => m.GetProperty("cars").GetArrayLength() == 2 && m.GetProperty("cars")[0].GetProperty("laps").GetInt32() == 4);
        File.WriteAllText(Path.Combine(directory, "real-live-state.json"), JsonSerializer.Serialize(live, pretty));
    }
}
