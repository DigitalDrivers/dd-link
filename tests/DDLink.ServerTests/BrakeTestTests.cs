using System.Numerics;
using System.Text.Json;

namespace DDLink.ServerTests;

/// <summary>
/// A brake test on a real server: Anna brakes hard on the open road with Cleo right behind her, both games report
/// the contact, and the result tells the platform who braked for no reason.
/// </summary>
public class BrakeTestTests
{
    private static readonly float Lap = MathF.Tau * 500f;

    private static async Task<JsonElement> RaceResultAsync(RaceServer server, int seconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var result = server.Receiver.Requests
                .Select(r => JsonDocument.Parse(r.Body).RootElement)
                .FirstOrDefault(m => m.GetProperty("type").GetString() == "session.completed");
            if (result.ValueKind == JsonValueKind.Object)
                return result;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out after {seconds} s waiting for the race result.\nServer log:\n{server.Log}");
    }

    [Fact]
    public async Task A_car_that_brakes_hard_for_no_reason_is_named_in_both_reports_of_the_contact()
    {
        await using var server = await RaceServer.StartAsync(raceLaps: 1, extraCars: 5);

        // The field goes first, 30 to 150 m ahead of Anna: by the time she brakes, it has shown how fast this part
        // of the lap is driven.
        var field = new List<FakeDriver>();
        for (var slot = 2; slot < 7; slot++)
        {
            var (driver, answer) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.GridDriver(slot), $"Driver {slot}", RaceServer.Car, (byte)slot,
                spline: (slot - 1) * 30f / Lap);
            Assert.True(driver != null, $"Driver {slot} was refused with 0x{answer:X2}");
            field.Add(driver);
        }
        var (anna, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Anna, "Anna", RaceServer.Car, 0);
        var (cleo, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Cleo, "Cleo", RaceServer.Car, 1, spline: 1f - 24f / Lap);
        Assert.NotNull(anna);
        Assert.NotNull(cleo);

        // Four seconds at 180 km/h, then two of braking at 1.2 g where nobody brakes.
        await Task.Delay(4000);
        anna.Braking = 12f;
        await Task.Delay(2000);
        // Cleo runs into the back of her: both games report the contact, each from its own car (z forward).
        await cleo.CollideAsync(0, 45f, new Vector3(0f, 0.3f, 2.5f));
        await anna.CollideAsync(1, 45f, new Vector3(0f, 0.3f, -2f));
        anna.Braking = 0f;
        anna.Speed = 50f;

        // Two cars of the field touch at speed, nobody braking.
        await field[0].CollideAsync(3, 20f, new Vector3(0.9f, 0.3f, 0.5f));

        await anna.CompleteLapAsync(60_000);
        var result = await RaceResultAsync(server);
        await anna.DisposeAsync();
        await cleo.DisposeAsync();
        foreach (var driver in field)
            await driver.DisposeAsync();

        var reports = result.GetProperty("collisions").EnumerateArray()
            .Select(c => (c.GetProperty("steamId").GetString(), c.GetProperty("brakeTest").GetBoolean(), c.GetProperty("otherBrakeTest").GetBoolean()))
            .ToList();
        // Anna's own report names her, Cleo's names the car she ran into.
        Assert.Contains((RaceServer.Anna.ToString(), true, false), reports);
        Assert.Contains((RaceServer.Cleo.ToString(), false, true), reports);
        Assert.Contains((RaceServer.GridDriver(2).ToString(), false, false), reports);
        Assert.Equal(3, reports.Count);
        Assert.Contains("Anna braked hard for no reason before the contact between Cleo and Anna", server.Log);
    }
}
