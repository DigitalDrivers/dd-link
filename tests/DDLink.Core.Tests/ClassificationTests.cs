using DDLink.Core;

namespace DDLink.Core.Tests;

public class ClassificationTests
{
    private static DriverResult Car(byte id, uint laps, uint totalMs, bool flag = true, uint best = 95_000, ulong steamId = 0)
        => new(id, steamId == 0 ? 76561190000000000UL + id : steamId, $"Driver {id}", "ks_mazda_mx5_cup", "skin", laps, totalMs, best, flag);

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
    public void Dead_heat_is_resolved_deterministically_by_car_id()
    {
        var a = Classification.Classify(SessionKind.Race, [Car(7, 3, 300_000), Car(4, 3, 300_000)]);
        var b = Classification.Classify(SessionKind.Race, [Car(4, 3, 300_000), Car(7, 3, 300_000)]);

        Assert.Equal(new byte[] { 4, 7 }, Order(a));
        Assert.Equal(Order(a), Order(b));
    }

    [Fact]
    public void Empty_entry_list_slots_are_dropped()
    {
        var empty = new DriverResult(9, 0, "", "ks_mazda_mx5_cup", "skin", 0, 0, Classification.NoLapTime, false);
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
