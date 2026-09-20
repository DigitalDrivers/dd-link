using System.Text;
using System.Text.Json;
using DDLink.Core;

namespace DDLink.Core.Tests;

public class SessionReportTests
{
    private static readonly SessionInfo Race = new(SessionKind.Race, "Race", "ks_nurburgring", "layout_gp_a", 8, 0, 912_345);

    private static DriverResult Driver(byte id, ulong steamId, uint laps, uint total, uint best, bool flag, int grid = 0)
        => new(id, steamId, $"Driver {id}", "ks_porsche_911_gt3_r_2016", "racing_17", laps, total, best, flag, grid);

    [Fact]
    public void Returns_null_when_nobody_took_part()
    {
        var empty = new DriverResult(0, 0, "", "car", "skin", 0, 0, Classification.NoLapTime, false, 0);
        Assert.Null(SessionReport.Create("evt", "race-1", Race, [empty], [], [], []));
    }

    [Fact]
    public void Json_contract_uses_camel_case_string_enums_and_string_steam_ids()
    {
        var message = SessionReport.Create("evt_123", "race-1", Race,
            [
                // Grid indexes count empty slots too (5 and 11 here); the message reports the rank among drivers.
                Driver(1, 76561198000000001, 8, 900_100, 110_500, flag: true, grid: 11),
                Driver(2, 76561198000000002, 3, 340_000, Classification.NoLapTime, flag: false, grid: 5),
            ],
            [new LapEntry("76561198000000001", 1, 112_300, 0, 118_000)],
            [new CollisionEntry("76561198000000001", null, 42.5f, 1f, 2f, 3f, 0.1f, 0.4f, 1.9f, 60_000)],
            [new ConnectionEntry("76561198000000002", "Driver 2", false, 400_000)])!;

        using var json = JsonDocument.Parse(MessageJson.Serialize(message));
        var root = json.RootElement;

        var collision = root.GetProperty("collisions")[0];
        // Where the impact hit the car travels with the contact: the platform tells the two reports apart by it.
        Assert.Equal(42.5f, collision.GetProperty("speedKmh").GetSingle());
        Assert.Equal(0.1f, collision.GetProperty("relX").GetSingle());
        Assert.Equal(0.4f, collision.GetProperty("relY").GetSingle());
        Assert.Equal(1.9f, collision.GetProperty("relZ").GetSingle());

        Assert.Equal("session.completed", root.GetProperty("type").GetString());
        Assert.Equal("evt_123", root.GetProperty("eventId").GetString());
        Assert.Equal("race-1", root.GetProperty("serverId").GetString());
        Assert.True(Guid.TryParse(root.GetProperty("id").GetString(), out _));

        var session = root.GetProperty("session");
        Assert.Equal("race", session.GetProperty("kind").GetString());
        Assert.Equal("ks_nurburgring", session.GetProperty("track").GetString());
        Assert.Equal("layout_gp_a", session.GetProperty("trackLayout").GetString());
        Assert.Equal(912_345, session.GetProperty("durationMs").GetInt64());

        var first = root.GetProperty("classification")[0];
        Assert.Equal(1, first.GetProperty("position").GetInt32());
        Assert.Equal("classified", first.GetProperty("status").GetString());
        Assert.Equal("76561198000000001", first.GetProperty("steamId").GetString());
        Assert.Equal(110_500u, first.GetProperty("bestLapMs").GetUInt32());
        Assert.Equal(2, first.GetProperty("gridPosition").GetInt32());

        var second = root.GetProperty("classification")[1];
        Assert.Equal("notClassified", second.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("bestLapMs").ValueKind);
        Assert.Equal(1, second.GetProperty("gridPosition").GetInt32());

        // Without a swap the crew is the driver alone.
        var crew = first.GetProperty("crew");
        Assert.Equal(1, crew.GetArrayLength());
        Assert.Equal("76561198000000001", crew[0].GetProperty("steamId").GetString());
        Assert.Equal("Driver 1", crew[0].GetProperty("name").GetString());
        Assert.Equal(8u, crew[0].GetProperty("laps").GetUInt32());

        Assert.Equal(112_300u, root.GetProperty("laps")[0].GetProperty("lapTimeMs").GetUInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("collisions")[0].GetProperty("otherSteamId").ValueKind);
        Assert.False(root.GetProperty("connections")[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public void Grid_position_is_only_reported_for_races()
    {
        var qualifying = new SessionInfo(SessionKind.Qualifying, "Qualify", "ks_nurburgring", "layout_gp_a", 0, 10, 600_000);
        var message = SessionReport.Create("evt", "race-1", qualifying, [Driver(1, 76561198000000001, 5, 0, 110_500, false)], [], [], [])!;

        Assert.Null(message.Classification[0].GridPosition);
    }

    [Fact]
    public void Serialized_message_is_utf8_json_with_the_message_id_inside()
    {
        var message = SessionReport.Create("evt", "race-1", Race, [Driver(1, 76561198000000001, 8, 900_100, 110_500, true)], [], [], [])!;
        var text = Encoding.UTF8.GetString(MessageJson.Serialize(message));
        Assert.Contains(message.Id.ToString(), text);
    }

    [Fact]
    public void A_car_with_a_driver_swap_reports_its_whole_crew()
    {
        var swapped = Driver(1, 76561198000000002, 8, 900_100, 110_500, flag: true) with
        {
            Crew = [new CrewMember("76561198000000001", "Anna", 5), new CrewMember("76561198000000002", "Ben", 3)],
        };
        var message = SessionReport.Create("evt", "race-1", Race, [swapped], [], [], [])!;

        var entry = Assert.Single(message.Classification);
        // The driver who had the car last stands for it; the crew names everyone.
        Assert.Equal("76561198000000002", entry.SteamId);
        Assert.Equal(8u, entry.Laps);
        Assert.Equal(["Anna", "Ben"], entry.Crew.Select(c => c.Name));
        Assert.Equal([5u, 3u], entry.Crew.Select(c => c.Laps));
    }
}
