#if NET10_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Scryer.Configuration;
using Jellyfin.Plugin.Scryer.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Plugin.Scryer.Native;

/// <summary>Supplements Jellyfin library search with Scryer metadata matches without creating library rows.</summary>
public sealed class ScryerSearchProvider : IExternalSearchProvider
{
    private readonly IScryerGraphqlService _scryer;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ISearchManager _search;
    private readonly Func<PluginConfiguration?> _configuration;

    public ScryerSearchProvider(IScryerGraphqlService scryer, ILibraryManager library, IUserManager users, ISearchManager search)
        : this(scryer, library, users, search, static () => Plugin.Instance?.Configuration) { }

    internal ScryerSearchProvider(IScryerGraphqlService scryer, ILibraryManager library, IUserManager users,
        ISearchManager search, Func<PluginConfiguration?> configuration)
    {
        _scryer = scryer;
        _library = library;
        _users = users;
        _search = search;
        _configuration = configuration;
    }

    public string Name => "Scryer";
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;
    public int Priority => 100;

    public bool CanSearch(SearchProviderQuery query) =>
        _configuration() is { EnableDiscovery: true, EnableNativeSearch: true } &&
        query.UserId is { } id && id != Guid.Empty && query.Limit is not <= 0 &&
        !string.IsNullOrWhiteSpace(query.SearchTerm) && query.SearchTerm.Trim().Length <= 256 &&
        (Allows(query, BaseItemKind.Movie) || Allows(query, BaseItemKind.Series));

    private static bool Allows(SearchProviderQuery query, BaseItemKind kind) =>
        query.IncludeItemTypes.Length > 0 ? query.IncludeItemTypes.Contains(kind) : !query.ExcludeItemTypes.Contains(kind);

    public async IAsyncEnumerable<SearchResult> SearchAsync(SearchProviderQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!CanSearch(query) || _users.GetUserById(query.UserId!.Value) is not { } user) yield break;
        var limit = Math.Clamp(query.Limit ?? 50, 1, 100);
        var seen = new HashSet<Guid>();

        // Jellyfin replaces its internal results when any external provider answers.
        // Include those results directly, never recurse through ISearchManager.Search.
        foreach (var provider in _search.GetProviders().OfType<IInternalSearchProvider>().Where(p => p.CanSearch(query)))
        {
            foreach (var result in await provider.SearchAsync(query, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seen.Add(result.ItemId)) yield return result;
                if (seen.Count >= limit) yield break;
            }
        }

        var response = await _scryer.SearchMetadataMultiAsync(user.Id.ToString("N"), query.SearchTerm, 50, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || response.Value.ValueKind != JsonValueKind.Object) yield break;
        foreach (var (field, kind) in new[] { ("movies", BaseItemKind.Movie), ("series", BaseItemKind.Series), ("anime", BaseItemKind.Series) })
        {
            if (!Allows(query, kind) || !response.Value.TryGetProperty(field, out var items) || items.ValueKind != JsonValueKind.Array) continue;
            var ids = ScryerNativeIds.Collect(items.EnumerateArray().Take(50).Where(item => item.ValueKind == JsonValueKind.Object)
                .SelectMany(ScryerNativeIds.SearchIds), kind);
            if (ids.Count == 0) continue;
            var matches = _library.GetItemList(new InternalItemsQuery(user)
            {
                HasAnyProviderIds = ids,
                IncludeItemTypes = [kind],
                MediaTypes = query.MediaTypes,
                AncestorIds = query.ParentId is { } parent && parent != Guid.Empty ? [parent] : [],
                Recursive = true,
                Limit = limit,
                EnableTotalRecordCount = false
            });
            foreach (var item in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.ChannelId != Guid.Empty || !seen.Add(item.Id)) continue;
                yield return new SearchResult(item.Id, 0.5f);
                if (seen.Count >= limit) yield break;
            }
        }
    }

    async Task<IReadOnlyList<SearchResult>> ISearchProvider.SearchAsync(SearchProviderQuery query, CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>();
        await foreach (var result in SearchAsync(query, cancellationToken).ConfigureAwait(false)) results.Add(result);
        return results;
    }
}
#endif
