# Annexe B — Gabarit minimal (source JSON avec API de recherche)

Point de départ pour une source qui expose une API JSON de recherche. Remplacer `Exemple`/`exemple.org` et adapter
`ParseSearch`. Même démarche que le chapitre 3 : enregistrement DI, semis désactivé, fixtures, tests.

```csharp
using System.Text.Json;
using TorrentSearch.Application.Connectors;
using TorrentSearch.Application.Torrents;
using TorrentSearch.Domain.Sources;
using TorrentSearch.Infrastructure.Http;

namespace TorrentSearch.Infrastructure.Connectors;

/// <summary>Exemple — API JSON de recherche (documenter ici l'URL, les conditions et les limites).</summary>
public sealed class ExempleConnector(SourceHttpClient http) : ConnectorBase(http)
{
    public const string Type = "exemple";

    public override string ConnectorType => Type;

    public override ConnectorDescriptor Descriptor { get; } = new(
        "Exemple",
        SourceCapabilities.Search | SourceCapabilities.Pagination | SourceCapabilities.TorrentFile | SourceCapabilities.InfoHash,
        ["Décrire ici chaque limite réelle observée (champs absents, quotas, couverture)."]);

    protected override async Task<ConnectorSearchResult> SearchCoreAsync(SourceContext source, ConnectorSearchRequest request, CancellationToken cancellationToken)
    {
        // Toujours encoder les termes ; ne jamais construire de syntaxe de requête à partir de l'entrée brute.
        var url = new Uri(BaseUri(source), $"api/search?q={Uri.EscapeDataString(request.Text)}&page={request.Page}&limit={source.MaxResults}");
        var response = await Http.GetAsync(source, url, MaxSearchResponseBytes, cancellationToken, "application/json");
        var (releases, hasMore) = ParseSearch(response.Content, source);
        return ConnectorSearchResult.Ok(releases, hasMore);
    }

    protected override async Task<(SourceHttpResponse Response, string? Problem)> ProbeAsync(SourceContext source, CancellationToken cancellationToken)
    {
        var response = await Http.GetAsync(source, new Uri(BaseUri(source), "api/search?q=test&limit=1"), MaxSearchResponseBytes, cancellationToken, "application/json");
        try
        {
            ParseSearch(response.Content, source);
            return (response, null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (response, "La réponse ne correspond pas au format JSON attendu.");
        }
    }

    internal static (IReadOnlyList<ReleaseCandidate> Releases, bool HasMore) ParseSearch(byte[] content, SourceContext source)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var releases = new List<ReleaseCandidate>();
        foreach (var item in root.GetProperty("items").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString();
            var name = item.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var hash = InfoHash.Normalize(item.TryGetProperty("info_hash", out var h) ? h.GetString() : null);
            releases.Add(new ReleaseCandidate
            {
                ExternalId = id,
                ReleaseName = name,
                InfoHash = hash,
                MagnetUri = hash is null ? null : InfoHash.BuildMagnet(hash, name),
                TorrentUrl = item.TryGetProperty("torrent", out var t) ? t.GetString() : null,  // filtré ensuite par les hôtes autorisés
                SizeBytes = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) && size > 0 ? size : null,
                // Seeders : uniquement si la source les fournit, avec l'heure de mesure si elle est connue.
                Seeders = item.TryGetProperty("seeders", out var se) && se.TryGetInt32(out var seeders) && seeders >= 0 ? seeders : null,
            });
        }

        return (releases, root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True);
    }
}
```

## Variante flux RSS/Atom

S'inspirer de `AcademicTorrentsConnector` : `XmlReader` avec `DtdProcessing = Prohibit` et `XmlResolver = null` (pas de XXE),
`MaxCharactersInDocument` borné, filtrage local par `TitleNormalizer.Relevance` si le flux ne permet pas la recherche, avertissement
« recherche limitée aux ajouts récents ».

## Variante catalogue fixe (fichiers publiés par les ayants droit)

S'inspirer de `StaticCatalogConnector` : liste versionnée dans `config/sources/*.json` (copiée à côté des binaires), info-hash
publiés vérifiés par le contrôle de santé.
