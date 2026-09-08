#if NET10_0_OR_GREATER
using System.Reflection;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Scryer.Configuration;
using Jellyfin.Plugin.Scryer.Native;
using Jellyfin.Plugin.Scryer.OAuth;
using Jellyfin.Plugin.Scryer.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;

internal static class NativeIntegrationTests
{
    internal static async Task SearchAsync()
    {
        var user = new User("native-user", "password", "salt") { Id = Guid.NewGuid() };
        var parent = Guid.NewGuid();
        var local = new Movie { Id = Guid.NewGuid() };
        var remoteMatch = new Movie { Id = Guid.NewGuid() };
        var nativeCalls = 0;
        var remoteCalls = 0;
        var failRemote = false;
        var internalProvider = NativeProxy.Create<IInternalSearchProvider>((method, _) => method.Name switch
        {
            "CanSearch" => true,
            "SearchAsync" => Baseline(),
            _ => ProxyDefaults.Value(method.ReturnType)
        });
        Task<IReadOnlyList<SearchResult>> Baseline()
        {
            nativeCalls++;
            return Task.FromResult<IReadOnlyList<SearchResult>>([new(local.Id, 1f)]);
        }
        var search = NativeProxy.Create<ISearchManager>((method, _) => method.Name == "GetProviders"
            ? new ISearchProvider[] { internalProvider } : throw new InvalidOperationException("Search recursion"));
        var users = UserManagerHarness.Create(user).Contract;
        var library = NativeProxy.Create<ILibraryManager>((method, args) =>
        {
            Assert.Equal("GetItemList", method.Name);
            var query = (InternalItemsQuery)args![0]!;
            Assert.Equal(user.Id, query.User!.Id);
            Assert.Equal(parent, query.AncestorIds.Single());
            Assert.Equal(BaseItemKind.Movie, query.IncludeItemTypes.Single());
            Assert.Equal(MediaType.Video, query.MediaTypes.Single());
            Assert.Equal("42", query.HasAnyProviderIds!["Tmdb"].Single());
            return new BaseItem[] { local, remoteMatch, remoteMatch };
        });
        var graphql = NativeProxy.Create<IScryerGraphqlService>((method, args) =>
        {
            Assert.Equal("SearchMetadataMultiAsync", method.Name);
            Assert.Equal(user.Id.ToString("N"), (string)args![0]!);
            remoteCalls++;
            return Task.FromResult(failRemote ? ScryerResult<JsonElement>.Fail(ScryerFailure.NotConnected) :
                ScryerResult<JsonElement>.Success(JsonSerializer.SerializeToElement(new
                {
                    movies = new[] { new { tmdbId = 42 } }, series = Array.Empty<object>(), anime = Array.Empty<object>()
                })));
        });
        var config = new PluginConfiguration();
        var provider = new ScryerSearchProvider(graphql, library, users, search, () => config);
        var query = new SearchProviderQuery { SearchTerm = "a title", UserId = user.Id, ParentId = parent,
            IncludeItemTypes = [BaseItemKind.Movie], ExcludeItemTypes = [BaseItemKind.Movie], MediaTypes = [MediaType.Video], Limit = 10 };
        var results = await Collect(provider.SearchAsync(query, CancellationToken.None));
        Assert.Equal(2, results.Count);
        Assert.Equal(local.Id, results[0].ItemId);
        Assert.Equal(remoteMatch.Id, results[1].ItemId);
        Assert.Equal(1, nativeCalls);
        Assert.Equal(1, remoteCalls);
        failRemote = true;
        Assert.Equal(1, (await Collect(provider.SearchAsync(query, CancellationToken.None))).Count);
        Assert.False(provider.CanSearch(new SearchProviderQuery { SearchTerm = "title" }));
        Assert.False(provider.CanSearch(new SearchProviderQuery { SearchTerm = "title", UserId = user.Id, Limit = 0 }));
        Assert.False(provider.CanSearch(new SearchProviderQuery { SearchTerm = "title", UserId = user.Id, IncludeItemTypes = [BaseItemKind.Audio] }));
        Assert.False(provider.CanSearch(new SearchProviderQuery { SearchTerm = new string('a', 257), UserId = user.Id }));
        config.EnableNativeSearch = false;
        Assert.Equal(0, (await Collect(provider.SearchAsync(query, CancellationToken.None))).Count);
        Assert.Equal(2, remoteCalls);
    }

    internal static async Task RecommendationsAsync()
    {
        var alice = new User("alice", "password", "salt") { Id = Guid.NewGuid() };
        var bob = new User("bob", "password", "salt") { Id = Guid.NewGuid() };
        var calls = new List<string>();
        var graphql = NativeProxy.Create<IScryerGraphqlService>((method, args) =>
        {
            Assert.Equal("GetRecommendationGroupsAsync", method.Name);
            var userId = (string)args![0]!;
            calls.Add(userId);
            return Task.FromResult(ScryerResult<IReadOnlyList<ScryerRecommendationGroup>>.Success([
                new("Related", [new("tmdb:movie:42", "MOVIE", "A title", 2026, null, null,
                    [new("tmdb", userId == alice.Id.ToString("N") ? "42" : "43"), new("tmdb_series", "wrong-type")]),
                    new("tmdb:series:44", "SERIES", "Wrong kind", 2026, null, null, [new("tmdb", "44")])])
            ]));
        });
        var config = new PluginConfiguration();
        var provider = new ScryerSimilarItemsProvider(graphql, () => config);
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Seed", ProviderIds = new() { ["Tmdb"] = "1" } };
        Assert.True(provider.CacheDuration is null);
        Assert.True(provider.Supports(typeof(Movie)));
        Assert.False(provider.Supports(typeof(MediaBrowser.Controller.Entities.Audio.Audio)));
        var first = await Collect(provider.GetSimilarItemsAsync(movie, new SimilarItemsQuery { User = alice }, CancellationToken.None));
        var second = await Collect(provider.GetSimilarItemsAsync(movie, new SimilarItemsQuery { User = bob }, CancellationToken.None));
        Assert.Equal("42", first.Single().ProviderId);
        Assert.Equal("43", second.Single().ProviderId);
        Assert.Equal(2, calls.Distinct().Count());
        Assert.Equal(0, (await Collect(provider.GetSimilarItemsAsync(movie, new SimilarItemsQuery(), CancellationToken.None))).Count);
        config.EnableNativeRecommendations = false;
        Assert.Equal(0, (await Collect(provider.GetSimilarItemsAsync(movie, new SimilarItemsQuery { User = alice }, CancellationToken.None))).Count);
        Assert.Equal(2, calls.Count);
    }

    private static async Task<List<T>> Collect<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source) result.Add(item);
        return result;
    }
}

public class NativeProxy : DispatchProxy
{
    private Func<MethodInfo, object?[]?, object?> _handler = null!;
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var contract = Create<T, NativeProxy>();
        ((NativeProxy)(object)contract)._handler = handler;
        return contract;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => _handler(method!, args);
}
#endif
