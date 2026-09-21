using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DDLink.Core;

/// <summary>What a notice is about; the game shows each kind in its own colour.</summary>
public enum NoticeKind : byte
{
    RaceControl = 0,
    Info = 1,
    Result = 2,
    Warning = 3,
}

/// <summary>
/// A notice for the drivers in the game: a message of race control, or something the platform has to tell
/// one driver. <see cref="SteamId"/> null means everybody on the server.
/// </summary>
public sealed record Notice(long Id, NoticeKind Kind, string Title, string Text, ulong? SteamId, DateTimeOffset CreatedAt)
{
    /// <summary>The game receives both in fields of a fixed size in bytes (UTF-8).</summary>
    public const int TitleBytes = 31;
    public const int TextBytes = 159;

    public string ChatText => $"{Title}: {Text}";

    /// <summary>The notice with title and text cut to what the game's fields hold, a cut marked with an ellipsis.</summary>
    public Notice Fitted() => this with { Title = Fit(Title, TitleBytes), Text = Fit(Text, TextBytes) };

    private static string Fit(string value, int bytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= bytes)
            return value;
        const string ellipsis = "…";
        var room = bytes - Encoding.UTF8.GetByteCount(ellipsis);
        var cut = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (Encoding.UTF8.GetByteCount(cut.ToString()) + rune.Utf8SequenceLength > room)
                break;
            cut.Append(rune.ToString());
        }
        return cut.ToString().TrimEnd() + ellipsis;
    }
}

public static class NoticeList
{
    private static readonly Dictionary<string, NoticeKind> Kinds = new()
    {
        ["race-control"] = NoticeKind.RaceControl,
        ["info"] = NoticeKind.Info,
        ["result"] = NoticeKind.Result,
        ["warning"] = NoticeKind.Warning,
    };

    /// <summary>The notices of the platform's answer <c>{"notices":[...]}</c>, or null when it is something else.</summary>
    public static IReadOnlyList<Notice>? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("notices", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;
            var notices = new List<Notice>();
            foreach (var item in items.EnumerateArray())
            {
                if (!Kinds.TryGetValue(item.GetProperty("kind").GetString() ?? "", out var kind))
                    return null;
                ulong? steamId = null;
                var target = item.GetProperty("steamId");
                if (target.ValueKind == JsonValueKind.String)
                {
                    if (!ulong.TryParse(target.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                        return null;
                    steamId = id;
                }
                else if (target.ValueKind != JsonValueKind.Null)
                    return null;
                notices.Add(new Notice(
                    item.GetProperty("id").GetInt64(),
                    kind,
                    item.GetProperty("title").GetString() ?? "",
                    item.GetProperty("text").GetString() ?? "",
                    steamId,
                    item.GetProperty("createdAt").GetDateTimeOffset()));
            }
            return notices;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// Which notices are new for this server: those after the last one it delivered, and none from before it
/// started. A server that is started again for the same event does not repeat old race control messages.
/// </summary>
public sealed class NoticeCursor(DateTimeOffset startedAt)
{
    /// <summary>The id of the last notice seen; the platform is asked for what came after it.</summary>
    public long After { get; private set; }

    public IReadOnlyList<Notice> Take(IReadOnlyList<Notice> notices)
    {
        var fresh = notices.Where(n => n.Id > After && n.CreatedAt >= startedAt).OrderBy(n => n.Id).ToList();
        if (notices.Count > 0)
            After = Math.Max(After, notices.Max(n => n.Id));
        return fresh;
    }
}
