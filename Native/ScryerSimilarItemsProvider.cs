#if NET10_0_OR_GREATER
using System.Runtime.CompilerServices;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Scryer.Configuration;
using Jellyfin.Plugin.Scryer.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.Scryer.Native;

/// <summary>Feeds Scryer recommendations into Jellyfin's native similar-items and recommendation manager.</summary>
public sealed class ScryerSimilarItemsProvider : IRemoteSimilarItemsProvider
{
    private readonly IScryerGraphqlService _scryer;
    private readonly Func<PluginConfiguration?> _configuration;

    public ScryerSimilarItemsProvider(IScryerGraphqlService scryer)
        : this(scryer, static () => Plugin.Instance?.Configuration) { }

    internal ScryerSimilarItemsProvider(IScryerGraphqlService scryer, Func<PluginConfiguration?> configuration)
    {
        _scryer = scryer;
        _configuration = configuration;
    }

    public string Name => "Scryer";
    public MetadataPluginType Type => MetadataPluginType.SimilarityProvider;
    // Jellyfin's remote cache is keyed by provider and item, not user. Scryer grants
    // are per-user, so responses must never enter that shared persistent cache.
    public TimeSpan? CacheDuration => null;
    public bool Supports(Type itemType) => _configuration() is { EnableDiscovery: true, EnableNativeRecommendations: true } &&
        (typeof(Movie).IsAssignableFrom(itemType) || typeof(Series).IsAssignableFrom(itemType));

    public async IAsyncEnumerable<SimilarItemReference> GetSimilarItemsAsync(BaseItem item, SimilarItemsQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Supports(item.GetType()) || query.User is not { } user || user.Id == Guid.Empty ||
            query.Limit is <= 0 || item.ChannelId != Guid.Empty || string.IsNullOrWhiteSpace(item.Name)) yield break;
        var kind = item is Movie ? BaseItemKind.Movie : BaseItemKind.Series;
        var facet = kind == BaseItemKind.Movie ? "MOVIE" : "SERIES";
        var response = await _scryer.GetRecommendationGroupsAsync(user.Id.ToString("N"),
            [new ScryerRecommendationSeed(item.Name, facet, item.ProviderIds)],
            30, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in response.Value!.SelectMany(group => group.Items).Take(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((candidate.TargetKind == "MOVIE") != (kind == BaseItemKind.Movie)) continue;
            foreach (var (provider, ids) in ScryerNativeIds.Collect(candidate.ExternalIds ?? [], kind))
            {
                foreach (var id in ids)
                {
                    if (item.ProviderIds.TryGetValue(provider, out var ownId) && string.Equals(id, ownId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (seen.Add(provider + ":" + id)) yield return new SimilarItemReference { ProviderName = provider, ProviderId = id };
                }
            }
        }
    }
}
#endif
