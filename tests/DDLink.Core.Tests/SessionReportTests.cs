using System.Text;
using System.Text.Json;
using DDLink.Core;

namespace DDLink.Core.Tests;

public class SessionReportTests
{
    private static readonly SessionInfo Race = new(SessionKind.Race, "Race", "ks_nurburgring", "layout_gp_a", 8, 0, 912_345);

    private static DriverResult Driver(byte id, ulong steamId, uint laps, uint total, uint best, bool flag)
        => new(id, steamId, $"Driver {id}", "ks_porsche_911_gt3_r_2016", "racing_17", laps, total, best, flag);

    [Fact]
    public void Returns_null_when_nobody_took_part()
    {
        var empty = new DriverResult(0, 0, "", "car", "skin", 0, 0, Classification.NoLapTime, false);
        Assert.Null(SessionReport.Create("evt", "race-1", Race, [empty], [], [], []));
    }

    [Fact]
    public void Json_contract_uses_camel_case_string_enums_and_string_steam_ids()
    {
        var message = SessionReport.Create("evt_123", "race-1", Race,
            [
                Driver(1, 76561198000000001, 8, 900_100, 110_500, flag: true),
                Driver(2, 76561198000000002, 3, 340_000, Classification.NoLapTime, flag: false),
            ],
            [new LapEntry("76561198000000001", 1, 112_300, 0, 118_000)],
            [new CollisionEntry("76561198000000001", null, 42.5f, 1f, 2f, 3f, 60_000)],
            [new ConnectionEntry("76561198000000002", "Driver 2", false, 400_000)])!;

        using var json = JsonDocument.Parse(MessageJson.Serialize(message));
        var root = json.RootElement;

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

        var second = root.GetProperty("classification")[1];
        Assert.Equal("notClassified", second.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("bestLapMs").ValueKind);

        Assert.Equal(112_300u, root.GetProperty("laps")[0].GetProperty("lapTimeMs").GetUInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("collisions")[0].GetProperty("otherSteamId").ValueKind);
        Assert.False(root.GetProperty("connections")[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public void Serialized_message_is_utf8_json_with_the_message_id_inside()
    {
        var message = SessionReport.Create("evt", "race-1", Race, [Driver(1, 76561198000000001, 8, 900_100, 110_500, true)], [], [], [])!;
        var text = Encoding.UTF8.GetString(MessageJson.Serialize(message));
        Assert.Contains(message.Id.ToString(), text);
    }
}
