using System.Diagnostics;

namespace DDLink.ServerTests;

/// <summary>
/// How much a full grid costs the server: 24 simulated cars sending positions at 20 Hz, with the plugin's
/// live feed running. A measurement rather than a check, so it only runs on request:
///   DDLINK_LOAD_TEST=1 scripts/check.sh   (or dotnet test --filter FullGrid)
/// </summary>
public class FullGridLoadTests
{
    [Fact]
    public async Task Measures_the_server_with_a_full_grid()
    {
        if (Environment.GetEnvironmentVariable("DDLINK_LOAD_TEST") != "1")
            return;

        const int cars = 24;
        await using var server = await RaceServer.StartAsync(raceLaps: 50, extraCars: cars - 2);
        var drivers = new List<FakeDriver>();
        for (var slot = 2; slot < cars; slot++)
        {
            var (driver, answer) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.GridDriver(slot), $"Driver {slot}", RaceServer.Car, (byte)slot);
            Assert.True(driver != null, $"Driver {slot} was refused with 0x{answer:X2}");
            drivers.Add(driver);
        }
        var (anna, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Anna, "Anna", RaceServer.Car, 0);
        var (cleo, _) = await FakeDriver.JoinAsync(server.GamePort, RaceServer.Cleo, "Cleo", RaceServer.Car, 1);
        drivers.Add(anna!);
        drivers.Add(cleo!);

        await Task.Delay(TimeSpan.FromSeconds(5)); // let the joins settle
        server.Process.Refresh();
        var cpuBefore = server.Process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(30));
        server.Process.Refresh();
        var cpu = (server.Process.TotalProcessorTime - cpuBefore).TotalSeconds / clock.Elapsed.TotalSeconds;
        var memory = server.Process.WorkingSet64 / 1024 / 1024;

        var liveStates = server.Receiver.Requests.Count;
        Console.WriteLine($"FULL GRID: {drivers.Count} cars at 20 Hz for 30 s -> server CPU {cpu:P1} of one core, memory {memory} MB, {liveStates} live states delivered");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "dd-link-full-grid.txt"), $"{drivers.Count} cars: cpu {cpu:P1} of one core, memory {memory} MB, live states {liveStates}\n");

        foreach (var driver in drivers)
            await driver.DisposeAsync();
        Assert.Equal(cars, drivers.Count);
    }
}
