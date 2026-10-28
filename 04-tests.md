# 4. Tester

Objectif : prouver, **sans Internet**, que le connecteur analyse correctement les vraies pages, qu'il n'invente rien, qu'il
respecte la politique de domaines et que les pannes sont classées correctement.

| Niveau | Où | Ce qui est vérifié |
|---|---|---|
| Unitaire | `tests/TorrentSearch.Tests.Unit/Connectors/PublicDomainTorrentsConnectorTests.cs` | analyse de l'index et d'une page film réelles, réécriture HTTPS, domaines, tailles, page inattendue, configuration |
| Intégration | `tests/TorrentSearch.Tests.Integration/PublicDomainTorrentsIntegrationTests.cs` | recherche complète via l'API (MySQL réel, regroupement par film), contrôle de santé « Suspecte », réponse mal formée |

Résultats obtenus le 28/10/2026 avec le code de ce guide : **15 tests unitaires réussis** et **3 tests d'intégration réussis**
(exécutés dans une copie de travail du dépôt, MySQL 8.4 via Testcontainers).

## 4.1 Fixtures

Copier les deux pages réelles du dossier [`fixtures/`](fixtures/) de ce guide vers `tests/fixtures/sources/` du dépôt :

| Fichier | Contenu | Origine |
|---|---|---|
| `pdt-category-horror.html` | page catégorie réduite (5 films) | `https://www.publicdomaintorrents.info/nshowcat.html?category=horror`, 28/10/2026 (déjà présent dans le dépôt) |
| `pdt-movie-836.html` | page film réduite au tableau des `.torrent` | `https://www.publicdomaintorrents.info/nshowmovie.html?movieid=836`, 28/10/2026 |

Les projets de tests copient déjà `tests/fixtures/**` dans leur dossier de sortie (`fixtures/sources/...`).

Règles pour les fixtures : réponses **réelles**, réduites au strict nécessaire, sans donnée personnelle, datées dans la
documentation. Une fixture inventée ne prouve rien sur la vraie source.

## 4.2 Tests unitaires

```csharp
using TorrentSearch.Application.Connectors;
using TorrentSearch.Infrastructure.Connectors;

namespace TorrentSearch.Tests.Unit.Connectors;

/// <summary>Analyse des pages réelles de Public Domain Torrents (28/10/2026), sans accès réseau.</summary>
public class PublicDomainTorrentsConnectorTests
{
    private static readonly SourceContext Source = new(1, "pdt", "Public Domain Torrents", "https://www.publicdomaintorrents.info",
        ["www.publicdomaintorrents.info", "www.publicdomaintorrents.com"], TimeSpan.FromSeconds(20), 6, 50);

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "sources", name));

    [Fact]
    public void Index_lists_movies_with_ids_and_normalized_titles()
    {
        var index = PublicDomainTorrentsConnector.ParseIndex(Fixture("pdt-category-horror.html"));

        Assert.NotEmpty(index);
        var bucket = Assert.Single(index, m => m.Id == 836);
        Assert.Equal("A Bucket of Blood", bucket.Title);
        Assert.Equal("a bucket of blood", bucket.NormalizedTitle);
        Assert.Equal(index.Count, index.Select(m => m.Id).Distinct().Count());
    }

    [Fact]
    public void Movie_page_yields_one_release_per_variant_with_https_links_and_announced_sizes()
    {
        var releases = PublicDomainTorrentsConnector.ParseMoviePage(Fixture("pdt-movie-836.html"), 836, null, Source);

        Assert.Equal(3, releases.Count);
        Assert.All(releases, r =>
        {
            Assert.StartsWith("https://www.publicdomaintorrents.com/bt/btdownload.php?", r.TorrentUrl);
            Assert.Equal("https://www.publicdomaintorrents.info/nshowmovie.html?movieid=836", r.DetailsUrl);
            Assert.Equal("A Bucket of Blood", r.WorkTitle);
            Assert.Equal(PublicDomainTorrentsConnector.DeclaredLicence, r.Licence);
            // Jamais inventés :
            Assert.Null(r.Year);
            Assert.Null(r.Seeders);
            Assert.Null(r.Leechers);
            Assert.Null(r.InfoHash);
            Assert.Null(r.MagnetUri);
        });

        var avi = Assert.Single(releases, r => r.ExternalId == "836:A_Bucket_of_Blood.avi.torrent");
        Assert.Equal("A Bucket of Blood — Divx 716MB AVI", avi.ReleaseName);
        Assert.Equal(716L * 1024 * 1024, avi.SizeBytes);
        Assert.Equal("DivX", avi.Codec);
        Assert.Null(releases.Single(r => r.ExternalId.EndsWith(".mp4.torrent", StringComparison.Ordinal)).Codec);
    }

    [Theory]
    [InlineData("http://www.publicdomaintorrents.com/bt/btdownload.php?type=torrent&file=X.avi.torrent", true)]
    [InlineData("https://www.publicdomaintorrents.com/bt/btdownload.php?type=torrent&file=X.avi.torrent", true)]
    [InlineData("http://ads.publicdomaintorrents.com/bt/btdownload.php?file=X", false)]
    [InlineData("http://evil.example/bt/btdownload.php?file=X", false)]
    [InlineData("http://user:pass@www.publicdomaintorrents.com/bt/btdownload.php?file=X", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Torrent_links_are_upgraded_to_https_and_restricted_to_the_official_host(string href, bool accepted)
    {
        var uri = PublicDomainTorrentsConnector.ToHttpsTorrentUrl(href, Source);

        Assert.Equal(accepted, uri is not null);
        if (uri is not null)
        {
            Assert.Equal("https", uri.Scheme);
        }
    }

    [Theory]
    [InlineData("Divx 716MB AVI", 716L * 1024 * 1024)]
    [InlineData("IPOD MP4  182 MB", 182L * 1024 * 1024)]
    [InlineData("HD 1,5 GB", 1610612736L)]
    [InlineData("MP4", null)]
    [InlineData("0 MB", null)]
    public void Sizes_are_parsed_from_labels_or_left_unknown(string label, long? expected) =>
        Assert.Equal(expected, PublicDomainTorrentsConnector.ParseSize(label));

    [Fact]
    public void Unexpected_page_yields_nothing_instead_of_guessing()
    {
        var html = "<html><body>Checking your browser…</body></html>"u8.ToArray();

        Assert.Empty(PublicDomainTorrentsConnector.ParseIndex(html));
        Assert.Empty(PublicDomainTorrentsConnector.ParseMoviePage(html, 1, null, Source));
    }

    [Fact]
    public void Configuration_without_torrent_host_is_reported()
    {
        var connector = new PublicDomainTorrentsConnector(null!, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
        var incomplete = Source with { AllowedHosts = ["www.publicdomaintorrents.info"] };

        Assert.Contains(connector.ValidateConfiguration(incomplete), i => i.Contains("www.publicdomaintorrents.com"));
    }
}
```

## 4.3 Tests d'intégration

### Adapter le banc de test (une fois)

Dans `tests/TorrentSearch.Tests.Integration/Infrastructure/TestHost.cs` :

1. **Type de connecteur paramétrable** pour `AddSourceAsync` :

```csharp
public Task<long> AddSourceAsync(string scenario, int timeoutSeconds = 5, string? allowedHosts = null,
    string connectorType = InternetArchiveConnector.Type,
    SourceCapabilities capabilities = SourceCapabilities.Search | SourceCapabilities.TorrentFile) => WithDbAsync(async db =>
{
    // …
    ConnectorType = connectorType,
    // …
    Capabilities = capabilities,
    // …
});
```

2. **Routes simulant la source** dans `FakeSourceServer.Build()` (les liens `.torrent` de la vraie page sont redirigés vers le
   serveur local pour que la politique de domaines puisse être testée) :

```csharp
app.MapGet("/pdt/nshowcat.html", () => Results.Bytes(SourceFixture("pdt-category-horror.html"), "text/html; charset=utf-8"));
app.MapGet("/pdt/nshowmovie.html", (HttpContext c) =>
{
    var self = $"{c.Request.Scheme}://{c.Request.Host}";
    var html = System.Text.Encoding.UTF8.GetString(SourceFixture("pdt-movie-836.html"))
        .Replace("http://www.publicdomaintorrents.com", self);
    return Results.Text(html, "text/html; charset=utf-8");
});
app.MapGet("/pdt-broken/nshowcat.html", () => Results.Text("<html><body>Checking your browser…</body></html>", "text/html"));
```

```csharp
public static byte[] SourceFixture(string name) =>
    File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "sources", name));
```

> Le connecteur étant enregistré dans `Infrastructure/DependencyInjection.cs` (chapitre 3.2), l'application de test le connaît
> automatiquement : aucune configuration supplémentaire du `WebApplicationFactory`.

### Tests

```csharp
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
```

## 4.4 Scénarios couverts et scénarios hérités

| Scénario | Couvert par |
|---|---|
| Source disponible, variantes regroupées sous un film | `Search_returns_each_variant_grouped_under_the_film_…` |
| Structure de page changée / page de vérification | `Changed_page_structure_marks_the_source_as_suspect`, `Search_on_a_broken_page_is_reported_as_malformed_…`, `Unexpected_page_yields_nothing_…` |
| Lien hors domaine, sous-domaine publicitaire, identifiants dans l'URL, schéma dangereux | `Torrent_links_are_upgraded_to_https_and_restricted_…` |
| Pas de seeders, pas d'année, pas de magnet | assertions `Null` dans les deux niveaux |
| Hors ligne, HTTP 5xx/403/429, délai, réponse trop volumineuse, redirection interdite, SSRF | **hérités** : ces cas sont traités par `SourceHttpClient`/`ConnectorBase` et déjà testés pour tous les connecteurs (`SearchApiTests`, `SecurityIntegrationTests`). Pour les rejouer sur cette source, ajoutez des routes `/pdt-…` calquées sur `/error500`, `/slow`, `/huge`, `/redirect-evil` |
| Récupération et validation du `.torrent` | **héritée** : `TorrentRetrievalService` est générique (`TorrentRetrievalTests`) |

## 4.5 Lancer

```powershell
dotnet test tests\TorrentSearch.Tests.Unit -c Release --filter "FullyQualifiedName~PublicDomainTorrents"
dotnet test tests\TorrentSearch.Tests.Integration -c Release --filter "FullyQualifiedName~PublicDomainTorrents"   # Docker requis
dotnet test TorrentSearch.slnx -c Release                                                                         # non-régression complète
```

## 4.6 Vérification manuelle (après les tests, une seule fois)

1. Démarrer l'application, **Administration → Public Domain Torrents → Tester** : attendu « Saine » (ou « Dégradée » si lente).
2. Activer la source, rechercher « bucket of blood » : 3 variantes, taille annoncée, licence « déclaré par la source, non vérifié »,
   seeders « inconnu ».
3. « Récupérer le .torrent » sur la variante MP4 : statut « Réussi », info-hash affiché, fichier téléchargeable.
4. Vérifier dans les journaux (Administration → Journaux) le nombre de requêtes émises : 1 index + ≤ 8 pages par recherche « froide ».
