using System.Text;
using System.Text.Json;

namespace DDLink.Core;

/// <summary>
/// The platform's ban list inside a server's blacklist file. AssettoServer reads one SteamID per line and
/// skips every line that is no number, reloads the file when it changes, turns banned drivers away and kicks
/// those who are connected. The plugin owns only the lines between its two markers: what an admin banned on
/// the server itself stays in the file.
/// </summary>
public static class BanList
{
    public const string Begin = "# dd-link: bans of the platform, rewritten by the plugin (do not edit between the markers)";
    public const string End = "# dd-link: end";

    /// <summary>The SteamIDs of the platform's answer <c>{"steamIds":["7656..."]}</c>, or null when it is something else.</summary>
    public static IReadOnlyList<ulong>? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("steamIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                return null;
            var list = new SortedSet<ulong>();
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String || !ulong.TryParse(id.GetString(), out var steamId))
                    return null;
                list.Add(steamId);
            }
            return list.ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The blacklist file with the platform's bans between the markers and everything else as it was.</summary>
    public static string Merge(string file, IReadOnlyList<ulong> bans)
    {
        var kept = new List<string>();
        var inside = false;
        foreach (var line in file.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line == Begin) inside = true;
            else if (line == End) inside = false;
            else if (!inside && line.Length > 0) kept.Add(line);
        }

        var text = new StringBuilder();
        foreach (var line in kept) text.Append(line).Append('\n');
        text.Append(Begin).Append('\n');
        foreach (var steamId in bans) text.Append(steamId).Append('\n');
        text.Append(End).Append('\n');
        return text.ToString();
    }
}
