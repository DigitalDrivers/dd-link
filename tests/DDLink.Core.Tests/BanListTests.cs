namespace DDLink.Core.Tests;

public class BanListTests
{
    [Fact]
    public void Reads_the_steam_ids_of_the_platform()
    {
        Assert.Equal(new ulong[] { 76561198000000002, 76561198000000009 }, BanList.Parse("""{"steamIds":["76561198000000009","76561198000000002","76561198000000009"]}"""));
        Assert.Empty(BanList.Parse("""{"steamIds":[]}""")!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>502 Bad Gateway</html>")]
    [InlineData("[]")]
    [InlineData("""{"steamIds":"76561198000000009"}""")]
    [InlineData("""{"steamIds":["not a number"]}""")]
    [InlineData("""{"steamIds":[76561198000000009]}""")]
    [InlineData("""{"error":true}""")]
    public void Takes_nothing_but_a_list_of_steam_ids_for_a_ban_list(string answer)
    {
        // An answer that is not a ban list must never empty the blacklist.
        Assert.Null(BanList.Parse(answer));
    }

    [Fact]
    public void Writes_the_bans_between_its_markers_and_keeps_what_an_admin_banned_on_the_server()
    {
        var merged = BanList.Merge("76561198000000500\r\n", [76561198000000002, 76561198000000009]);
        Assert.Equal($"76561198000000500\n{BanList.Begin}\n76561198000000002\n76561198000000009\n{BanList.End}\n", merged);

        // The next list replaces the last one; the admin's line is still there, also when it was added below.
        var next = BanList.Merge(merged + "76561198000000600\n", [76561198000000009]);
        Assert.Equal($"76561198000000500\n76561198000000600\n{BanList.Begin}\n76561198000000009\n{BanList.End}\n", next);

        // Nothing to do: the same text comes out, so the file is left alone.
        Assert.Equal(next, BanList.Merge(next, [76561198000000009]));
        Assert.Equal($"{BanList.Begin}\n{BanList.End}\n", BanList.Merge("", []));
    }

    [Fact]
    public void Every_line_it_writes_is_a_steam_id_or_no_number_at_all()
    {
        // AssettoServer skips lines that are no number: the markers must never parse as one.
        Assert.False(ulong.TryParse(BanList.Begin, out _));
        Assert.False(ulong.TryParse(BanList.End, out _));
    }
}
