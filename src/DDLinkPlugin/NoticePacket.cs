using AssettoServer.Network.ClientMessages;

namespace DDLinkPlugin;

/// <summary>
/// A notice for one driver's game: lua/notices.lua shows it as a banner at the top of the screen. Title and
/// text are fixed fields of UTF-8 bytes; <see cref="DDLink.Core.Notice.Fitted"/> cuts them to size first.
/// </summary>
[OnlineEvent(Key = "DD_Notice")]
public class NoticePacket : OnlineEvent<NoticePacket>
{
    [OnlineEventField(Name = "kind")]
    public byte Kind;

    [OnlineEventField(Name = "title", Size = 32)]
    public string Title = "";

    [OnlineEventField(Name = "text", Size = 160)]
    public string Text = "";
}

/// <summary>What lua/notices.lua sends once it runs in a driver's game: from now on the game shows notices.</summary>
[OnlineEvent(Key = "DD_NoticesReady")]
public class NoticesReadyPacket : OnlineEvent<NoticesReadyPacket>
{
    [OnlineEventField(Name = "dummy")]
    public byte Dummy;
}
