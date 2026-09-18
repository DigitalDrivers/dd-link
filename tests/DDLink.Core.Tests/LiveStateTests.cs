using System.Text.Json;
using DDLink.Core;

namespace DDLink.Core.Tests;

public class LiveStateTests
{
    private static LiveCarState Car(byte id, uint laps, float spline, uint total = 0, bool finished = false, uint best = Classification.NoLapTime)
        => new(id, laps, total, best, finished, spline);

    [Fact]
    public void In_a_race_the_car_further_along_is_ahead()
    {
        var rank = LiveOrder.Rank(SessionKind.Race, [Car(1, 3, 0.20f), Car(2, 3, 0.85f), Car(3, 4, 0.05f)]);
        Assert.Equal(1, rank[3]);
        Assert.Equal(2, rank[2]);
        Assert.Equal(3, rank[1]);
    }

    [Fact]
    public void Finished_cars_keep_the_order_in_which_they_took_the_flag()
    {
        // Car 1 finished first and rolls on slowly; car 2 finished later and is further round the lap.
        var rank = LiveOrder.Rank(SessionKind.Race, [Car(2, 8, 0.60f, total: 905_000, finished: true), Car(1, 8, 0.10f, total: 900_000, finished: true), Car(3, 8, 0.90f)]);
        Assert.Equal(1, rank[1]);
        Assert.Equal(2, rank[2]);
        Assert.Equal(3, rank[3]);
    }

    [Fact]
    public void In_practice_and_qualifying_the_best_lap_decides_and_cars_without_a_time_come_last()
    {
        var rank = LiveOrder.Rank(SessionKind.Qualifying, [Car(1, 2, 0.5f), Car(2, 1, 0.1f, best: 110_500), Car(3, 4, 0.9f, best: 109_900)]);
        Assert.Equal(1, rank[3]);
        Assert.Equal(2, rank[2]);
        Assert.Equal(3, rank[1]);
    }

    [Fact]
    public void Json_contract_is_camel_case_with_string_steam_ids_and_null_for_missing_lap_times()
    {
        var message = new LiveStateMessage(LiveStateMessage.MessageType, "evt", "race-1", DateTimeOffset.UnixEpoch,
            new LiveSession(SessionKind.Race, "Race", "ks_nurburgring", "layout_gp_a", 8, 0, 61_000, 0),
            [new LiveCar(0, "76561198000000001", "Anna", "ks_porsche_911_gt3_cup_2017", 1, 2, 229_000, 109_000, 111_000, false, 0.43f, 12.5f, -40.25f, 182, 4, 7200, 87, [35_100])]);

        using var json = JsonDocument.Parse(LiveStateMessage.Serialize(message));
        var root = json.RootElement;
        Assert.Equal("live.state", root.GetProperty("type").GetString());
        Assert.Equal("race", root.GetProperty("session").GetProperty("kind").GetString());
        Assert.Equal(61_000, root.GetProperty("session").GetProperty("elapsedMs").GetInt64());

        var car = root.GetProperty("cars")[0];
        Assert.Equal("76561198000000001", car.GetProperty("steamId").GetString());
        Assert.Equal(1, car.GetProperty("position").GetInt32());
        Assert.Equal(229_000u, car.GetProperty("totalTimeMs").GetUInt32());
        Assert.Equal(182, car.GetProperty("speedKmh").GetInt32());
        Assert.Equal(4, car.GetProperty("gear").GetInt32());
        Assert.Equal(87, car.GetProperty("gas").GetInt32());
        Assert.Equal(35_100u, car.GetProperty("sectors")[0].GetUInt32());
        Assert.Equal(0.43f, car.GetProperty("spline").GetSingle(), 3);
    }
}
