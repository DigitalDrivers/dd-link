using System.Text;
using DDLink.Core;
using Xunit;

namespace DDLink.Core.Tests;

public class NoticesTests
{
    private const string Answer = """
        {"notices":[
          {"id":7,"kind":"race-control","title":"RACE CONTROL","text":"Track limits at turn 1 are enforced.","steamId":null,"createdAt":"2026-09-22T19:05:00.000Z"},
          {"id":8,"kind":"result","title":"P3 OF 12","text":"Provisional until Thursday 19:10.","steamId":"76561198000000001","createdAt":"2026-09-22T19:40:00.000Z"}
        ]}
        """;

    [Fact]
    public void Reads_the_notices_of_the_platform()
    {
        var notices = NoticeList.Parse(Answer)!;
        Assert.Equal(2, notices.Count);
        Assert.Equal(new Notice(7, NoticeKind.RaceControl, "RACE CONTROL", "Track limits at turn 1 are enforced.", null, DateTimeOffset.Parse("2026-09-22T19:05:00Z")), notices[0]);
        Assert.Equal(NoticeKind.Result, notices[1].Kind);
        Assert.Equal(76561198000000001UL, notices[1].SteamId);
    }

    [Fact]
    public void Refuses_what_is_no_notice_list()
    {
        Assert.Null(NoticeList.Parse("<html>"));
        Assert.Null(NoticeList.Parse("""{"steamIds":[]}"""));
        Assert.Null(NoticeList.Parse("""{"notices":[{"id":1,"kind":"party","title":"x","text":"y","steamId":null,"createdAt":"2026-09-22T19:05:00Z"}]}"""));
        Assert.Null(NoticeList.Parse("""{"notices":[{"id":1,"kind":"info","title":"x","text":"y","steamId":"not a number","createdAt":"2026-09-22T19:05:00Z"}]}"""));
    }

    [Fact]
    public void Cuts_title_and_text_to_the_bytes_the_game_takes_without_splitting_a_letter()
    {
        var long_ = new string('ä', 200);
        var notice = new Notice(1, NoticeKind.Info, long_, long_, null, DateTimeOffset.UnixEpoch).Fitted();
        Assert.True(Encoding.UTF8.GetByteCount(notice.Title) <= Notice.TitleBytes);
        Assert.True(Encoding.UTF8.GetByteCount(notice.Text) <= Notice.TextBytes);
        // Two bytes a letter and three for the ellipsis: whole letters only, never half of one.
        Assert.Equal(new string('ä', (Notice.TitleBytes - 3) / 2) + "…", notice.Title);
        Assert.Equal("Short", new Notice(1, NoticeKind.Info, "Short", "Short", null, DateTimeOffset.UnixEpoch).Fitted().Title);
    }

    [Fact]
    public void Reads_as_one_chat_line()
    {
        Assert.Equal("RACE CONTROL: Safety first.", new Notice(1, NoticeKind.RaceControl, "RACE CONTROL", "Safety first.", null, DateTimeOffset.UnixEpoch).ChatText);
    }

    [Fact]
    public void Leaves_out_what_was_sent_before_the_server_started_and_what_it_has_delivered()
    {
        var started = DateTimeOffset.Parse("2026-09-22T19:30:00Z");
        var cursor = new NoticeCursor(started);
        // The race-control notice is from before the server started (a restart): not again.
        var fresh = cursor.Take(NoticeList.Parse(Answer)!);
        Assert.Equal([8L], fresh.Select(n => n.Id));
        Assert.Equal(8, cursor.After);
        Assert.Empty(cursor.Take(NoticeList.Parse(Answer)!));
    }
}
