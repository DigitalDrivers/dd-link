using DDLink.Core;

namespace DDLink.Core.Tests;

public class ClassificationTests
{
    private static DriverResult Car(byte id, uint laps, uint totalMs, bool flag = true, uint best = 95_000, ulong steamId = 0, int? grid = null)
        => new(id, steamId == 0 ? 76561190000000000UL + id : steamId, $"Driver {id}", "ks_mazda_mx5_cup", "skin", laps, totalMs, best, flag, grid ?? id);

    private static byte[] Order(IEnumerable<ClassifiedDriver> c) => c.Select(d => d.Driver.CarId).ToArray();

    [Fact]
    public void Race_orders_by_laps_then_total_time()
    {
        var result = Classification.Classify(SessionKind.Race,
        [
            Car(1, laps: 3, totalMs: 300_500),
            Car(2, laps: 3, totalMs: 299_900),
            Car(3, laps: 3, totalMs: 301_000),
        ]);

        Assert.Equal(new byte[] { 2, 1, 3 }, Order(result));
        Assert.Equal([1, 2, 3], result.Select(d => d.Position));
        Assert.All(result, d => Assert.Equal(DriverStatus.Classified, d.Status));
    }

    [Fact]
    public void Lapped_car_that_took_the_flag_is_classified_behind_cars_on_the_lead_lap()
    {
        // Car 3 crossed the line before car 2 in wall-clock terms but is one lap down.
        var result = Classification.Classify(SessionKind.Race,
        [
            Car(1, laps: 5, totalMs: 500_000),
            Car(2, laps: 5, totalMs: 520_000),
            Car(3, laps: 4, totalMs: 505_000),
        ]);

        Assert.Equal(new byte[] { 1, 2, 3 }, Order(result));
        Assert.Equal(DriverStatus.Classified, result[2].Status);
    }

    [Fact]
    public void Retired_cars_come_after_all_finishers_even_with_more_laps()
    {
        var result = Classification.Classify(SessionKind.Race,
        [
            Car(1, laps: 5, totalMs: 500_000),
            Car(2, laps: 4, totalMs: 410_000, flag: false), // disconnected while second
            Car(3, laps: 3, totalMs: 505_000),              // lapped twice, took the flag
            Car(4, laps: 0, totalMs: 0, flag: false),       // crashed on lap one
        ]);

        Assert.Equal(new byte[] { 1, 3, 2, 4 }, Order(result));
        Assert.Equal(DriverStatus.Classified, result[1].Status);
        Assert.Equal(DriverStatus.NotClassified, result[2].Status);
        Assert.Equal(DriverStatus.NotClassified, result[3].Status);
    }

    [Fact]
    public void Dead_heat_goes_to_the_driver_who_started_further_ahead()
    {
        // Same laps, same total time: car 7 started from pole (grid index 0), car 4 from fourth.
        var a = Classification.Classify(SessionKind.Race, [Car(4, 3, 300_000, grid: 3), Car(7, 3, 300_000, grid: 0)]);
        var b = Classification.Classify(SessionKind.Race, [Car(7, 3, 300_000, grid: 0), Car(4, 3, 300_000, grid: 3)]);

        Assert.Equal(new byte[] { 7, 4 }, Order(a));
        Assert.Equal(Order(a), Order(b));
    }

    [Fact]
    public void First_lap_retirements_are_ordered_by_grid_position()
    {
        // Nobody completed a lap: laps and total time are 0 for all three, a very common tie.
        var result = Classification.Classify(SessionKind.Race,
        [
            Car(1, 0, 0, flag: false, grid: 2),
            Car(2, 0, 0, flag: false, grid: 0),
            Car(3, 0, 0, flag: false, grid: 1),
        ]);

        Assert.Equal(new byte[] { 2, 3, 1 }, Order(result));
    }

    [Fact]
    public void Qualifying_tie_follows_entry_list_order_like_the_grid_the_server_builds()
    {
        // The server sorts the race grid by best lap with a stable sort, so equal times keep entry list order.
        var result = Classification.Classify(SessionKind.Qualifying,
        [
            Car(5, 4, 0, flag: false, best: 95_800),
            Car(2, 4, 0, flag: false, best: 95_800),
        ]);

        Assert.Equal(new byte[] { 2, 5 }, Order(result));
    }

    [Fact]
    public void Empty_entry_list_slots_are_dropped()
    {
        var empty = new DriverResult(9, 0, "", "ks_mazda_mx5_cup", "skin", 0, 0, Classification.NoLapTime, false, 9);
        var result = Classification.Classify(SessionKind.Race, [Car(1, 3, 300_000), empty]);

        Assert.Single(result);
        Assert.Equal(1, result[0].Driver.CarId);
    }

    [Fact]
    public void Qualifying_orders_by_best_lap_and_puts_drivers_without_a_time_last()
    {
        var result = Classification.Classify(SessionKind.Qualifying,
        [
            Car(1, laps: 4, totalMs: 0, flag: false, best: 96_200),
            Car(2, laps: 0, totalMs: 0, flag: false, best: Classification.NoLapTime),
            Car(3, laps: 6, totalMs: 0, flag: false, best: 95_800),
        ]);

        Assert.Equal(new byte[] { 3, 1, 2 }, Order(result));
        Assert.Equal(DriverStatus.Classified, result[0].Status);
        Assert.Equal(DriverStatus.NotClassified, result[2].Status);
    }
}
