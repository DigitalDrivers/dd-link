using DDLink.Core;

namespace DDLink.Core.Tests;

public class StintLedgerTests
{
    private const ulong Anna = 76561198000000001;
    private const ulong Ben = 76561198000000002;

    private static CarSnapshot After(uint laps, uint total, uint best = 110_000, bool flag = false)
        => new(laps, total, best, LastLapMs: best + 500, flag, RacePos: 0);

    [Fact]
    public void Nothing_is_restored_for_the_first_driver_of_a_car()
    {
        var ledger = new StintLedger();
        Assert.Null(ledger.DriverJoined(3, Anna, "Anna"));
    }

    [Fact]
    public void A_crew_mate_continues_with_what_the_car_had_achieved()
    {
        var ledger = new StintLedger();
        ledger.DriverJoined(3, Anna, "Anna");
        ledger.LapCompleted(3, Anna, "Anna", After(1, 118_000));
        ledger.LapCompleted(3, Anna, "Anna", After(2, 229_000, best: 109_000));
        // Anna pits and leaves; the last completed lap is what the car has.
        ledger.DriverLeft(3, After(2, 229_000, best: 109_000));

        var restored = ledger.DriverJoined(3, Ben, "Ben");

        Assert.Equal(After(2, 229_000, best: 109_000), restored);
    }

    [Fact]
    public void A_lap_is_remembered_even_when_the_driver_vanishes_without_a_goodbye()
    {
        var ledger = new StintLedger();
        ledger.DriverJoined(3, Anna, "Anna");
        ledger.LapCompleted(3, Anna, "Anna", After(1, 118_000));
        // No DriverLeft: the connection simply dropped.
        Assert.Equal(1u, ledger.DriverJoined(3, Ben, "Ben")!.Laps);
    }

    [Fact]
    public void Cars_do_not_mix()
    {
        var ledger = new StintLedger();
        ledger.DriverJoined(3, Anna, "Anna");
        ledger.LapCompleted(3, Anna, "Anna", After(5, 600_000));
        Assert.Null(ledger.DriverJoined(4, Ben, "Ben"));
    }

    [Fact]
    public void The_crew_lists_every_driver_once_with_their_own_laps_in_the_order_of_their_first_stint()
    {
        var ledger = new StintLedger();
        ledger.DriverJoined(3, Anna, "Anna");
        ledger.LapCompleted(3, Anna, "Anna", After(1, 118_000));
        ledger.LapCompleted(3, Anna, "Anna", After(2, 229_000));
        ledger.DriverLeft(3, After(2, 229_000));
        ledger.DriverJoined(3, Ben, "Ben");
        ledger.LapCompleted(3, Ben, "Ben", After(3, 420_000));
        ledger.DriverLeft(3, After(3, 420_000));
        // Anna comes back under a new name for her second stint.
        ledger.DriverJoined(3, Anna, "Anna A.");
        ledger.LapCompleted(3, Anna, "Anna A.", After(4, 540_000));

        Assert.Equal(
            [new CrewMember(Anna.ToString(), "Anna A.", 3), new CrewMember(Ben.ToString(), "Ben", 1)],
            ledger.CrewOf(3));
        Assert.Empty(ledger.CrewOf(9));
    }
}
