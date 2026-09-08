#if NET10_0_OR_GREATER
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Scryer.Services;

namespace Jellyfin.Plugin.Scryer.Native;

internal static class ScryerNativeIds
{
    internal static string? ProviderName(string source, BaseItemKind kind) => source.ToLowerInvariant() switch
    {
        "imdb" => "Imdb",
        "tmdb" => "Tmdb",
        "tvdb" => "Tvdb",
        "tmdb_movie" when kind == BaseItemKind.Movie => "Tmdb",
        "tvdb_movie" when kind == BaseItemKind.Movie => "Tvdb",
        "tmdb_series" or "tmdb_show" or "tmdb_tv" when kind == BaseItemKind.Series => "Tmdb",
        "tvdb_series" or "tvdb_show" when kind == BaseItemKind.Series => "Tvdb",
        _ => null
    };

    internal static Dictionary<string, string[]> Collect(IEnumerable<ScryerTvExternalId> ids, BaseItemKind kind) => ids
        .Select(id => (Name: ProviderName(id.Source, kind), Value: id.Value.Trim()))
        .Where(id => id.Name is not null && id.Value.Length is > 0 and <= 256)
        .GroupBy(id => id.Name!, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Select(id => id.Value).Distinct(StringComparer.Ordinal).ToArray());

    internal static IEnumerable<ScryerTvExternalId> SearchIds(JsonElement item)
    {
        foreach (var (field, source) in new[] { ("imdbId", "imdb"), ("tmdbId", "tmdb"), ("tvdbId", "tvdb") })
        {
            if (item.TryGetProperty(field, out var value) && Text(value) is { Length: > 0 } id)
                yield return new ScryerTvExternalId(source, id);
        }

        if (!item.TryGetProperty("externalIds", out var ids) || ids.ValueKind != JsonValueKind.Array) yield break;
        foreach (var id in ids.EnumerateArray().Take(16))
        {
            if (id.ValueKind == JsonValueKind.Object && id.TryGetProperty("source", out var source) &&
                id.TryGetProperty("value", out var value) && Text(source) is { } name && Text(value) is { } text)
                yield return new ScryerTvExternalId(name, text);
        }
    }

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };
}
#endif
