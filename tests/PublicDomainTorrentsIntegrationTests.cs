using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TorrentSearch.Application.Sources;
using TorrentSearch.Domain.Search;
using TorrentSearch.Domain.Sources;
using TorrentSearch.Infrastructure.Connectors;
using TorrentSearch.Tests.Integration.Infrastructure;
using TorrentSearch.Web.Api;

namespace TorrentSearch.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class PublicDomainTorrentsIntegrationTests(TestHost host)
{
    // 127.0.0.1 = serveur simulé ; l'hôte officiel des .torrent doit aussi figurer (exigé par ValidateConfiguration).
    private const string TestHosts = "127.0.0.1,www.publicdomaintorrents.com";

    private const SourceCapabilities Capabilities =
        SourceCapabilities.Search | SourceCapabilities.TorrentFile | SourceCapabilities.Size | SourceCapabilities.Licence;

    [Fact]
    public async Task Search_returns_each_variant_grouped_under_the_film_with_unknown_values_left_empty()
    {
        await host.DisableAllSourcesAsync();
        var id = await host.AddSourceAsync("pdt", allowedHosts: TestHosts, connectorType: PublicDomainTorrentsConnector.Type, capabilities: Capabilities);

        var response = await host.Factory.CreateClient().PostAsJsonAsync("/api/search", new { query = "A Bucket of Blood", forceRefresh = true });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<SearchApiResponse>(SearchApiTests.Json))!;

        Assert.Equal(SearchExecutionStatus.Completed, result.Search.Status);
        Assert.Equal(3, result.Search.Sources.Single(s => s.SourceId == id).ResultCount);
        var film = Assert.Single(result.Results.Items, g => g.Movie.Title == "A Bucket of Blood");
        Assert.Equal(3, film.Releases.Count);
        Assert.All(film.Releases.SelectMany(r => r.Listings), l =>
        {
            Assert.True(l.HasTorrentFile);
            Assert.Null(l.Seeders);
            Assert.Null(l.Year);
            Assert.NotNull(l.SizeBytes);
        });
    }

    [Fact]
    public async Task Changed_page_structure_marks_the_source_as_suspect()
    {
        await host.DisableAllSourcesAsync();
        var id = await host.AddSourceAsync("pdt-broken", allowedHosts: TestHosts, connectorType: PublicDomainTorrentsConnector.Type, capabilities: Capabilities);

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var check = await scope.ServiceProvider.GetRequiredService<SourceHealthService>().CheckAsync(id, HealthCheckTrigger.Manual, CancellationToken.None);

        Assert.Equal(SourceStatus.Suspect, check.Value!.Status);
        Assert.Equal(SourceErrorCode.MalformedResponse, check.Value.ErrorCode);
    }

    [Fact]
    public async Task Search_on_a_broken_page_is_reported_as_malformed_not_as_zero_results()
    {
        await host.DisableAllSourcesAsync();
        var id = await host.AddSourceAsync("pdt-broken", allowedHosts: TestHosts, connectorType: PublicDomainTorrentsConnector.Type, capabilities: Capabilities);

        var response = await host.Factory.CreateClient().PostAsJsonAsync("/api/search", new { query = "A Bucket of Blood broken", forceRefresh = true });
        var result = (await response.Content.ReadFromJsonAsync<SearchApiResponse>(SearchApiTests.Json))!;

        Assert.Equal(SourceErrorCode.MalformedResponse, result.Search.Sources.Single(s => s.SourceId == id).ErrorCode);
    }
}
