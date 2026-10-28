# 3. Implémenter

Trois fichiers à toucher dans le dépôt `ThorEnts`, dans cet ordre :

| Étape | Fichier | Action |
|---|---|---|
| 3.1 | `src/TorrentSearch.Infrastructure/Connectors/PublicDomainTorrentsConnector.cs` | créer le connecteur |
| 3.2 | `src/TorrentSearch.Infrastructure/DependencyInjection.cs` | l'enregistrer |
| 3.3 | `src/TorrentSearch.Infrastructure/Persistence/SourceSeeder.cs` | déclarer la source (désactivée) |

Aucune migration n'est nécessaire : une source est une **ligne** de la table `sources`, pas un schéma. Aucune modification de
l'interface n'est nécessaire : l'administration, la recherche et la récupération des `.torrent` sont génériques.

> Le code ci-dessous a été compilé contre le dépôt au 28/10/2026 et exécuté sur les fixtures de ce guide
> (`fixtures/pdt-category-horror.html`, `fixtures/pdt-movie-836.html`). Résultat obtenu :
>
> ```
> index: 5 films; first: 836 A Bucket of Blood / a bucket of blood
> 836:A_Bucket_of_Blood.avi.torrent | A Bucket of Blood — Divx 716MB AVI | 750780416 | DivX | https://www.publicdomaintorrents.com/bt/btdownload.php?type=torrent&file=A_Bucket_of_Blood.avi.torrent
> 836:A_Bucket_of_Blood.mp4.torrent | A Bucket of Blood — IPOD MP4 182 MB | 190840832 |  | https://www.publicdomaintorrents.com/bt/…&file=A_Bucket_of_Blood.mp4.torrent
> 836:A_Bucket_of_Blood_PSP.MP4.torrent | A Bucket of Blood — PSP MP4 336 MB | 352321536 |  | https://www.publicdomaintorrents.com/bt/…&file=A_Bucket_of_Blood_PSP.MP4.torrent
> ads rejected            ← lien vers ads.publicdomaintorrents.com refusé
> ```

## 3.1 Le connecteur

Points clés, repris dans les commentaires du code :

- hérite de `ConnectorBase` : les `SourceRequestException` deviennent des `ConnectorSearchResult.Fail(code)`, et les
  `FormatException`/`InvalidOperationException` deviennent `MalformedResponse` ; le contrôle de santé générique classe les erreurs
  (suspecte, dégradée, indisponible) ;
- `SearchCoreAsync` : index en cache 12 h → filtre local → au plus 8 pages film → variantes ; un quota atteint en cours de route
  donne un **résultat partiel avec avertissement** ;
- `ProbeAsync` : page légère + vérification de structure (≥ 5 liens film) ; sinon `Problem` → source « Suspecte » ;
- `ValidateConfiguration` : exige l'hôte des `.torrent` dans la liste autorisée (sinon la récupération échouerait toujours) ;
- regex compilées (`[GeneratedRegex]`) **avec délai d'exécution** (1 s) contre les retours arrière catastrophiques ;
- `ToHttpsTorrentUrl` : schéma réécrit en HTTPS **puis** hôte vérifié, identifiants dans l'URL refusés ;
- méthodes d'analyse `internal static` : testables sans réseau (le projet de tests a déjà `InternalsVisibleTo`).

```csharp
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using TorrentSearch.Application;
using TorrentSearch.Application.Connectors;
using TorrentSearch.Application.Text;
using TorrentSearch.Domain.Sources;
using TorrentSearch.Infrastructure.Http;

namespace TorrentSearch.Infrastructure.Connectors;

/// <summary>
/// Public Domain Torrents — films classiques présentés comme du domaine public (pages HTML statiques).
/// Index des titres : nshowcat.html?category=ALL (mis en cache) ; variantes .torrent : nshowmovie.html?movieid={n}.
/// Les liens .torrent sont publiés en http:// et réécrits en https:// avant tout contrôle de domaine.
/// Aucune licence par œuvre, aucune année, aucun seeder : ces champs restent inconnus.
/// </summary>
public sealed partial class PublicDomainTorrentsConnector(SourceHttpClient http, IMemoryCache cache) : ConnectorBase(http)
{
    public const string Type = "public-domain-torrents";
    internal const string TorrentHost = "www.publicdomaintorrents.com";
    internal const string DeclaredLicence = "Domaine public (déclaré par la source, non vérifié)";
    private const int MaxMoviePagesPerSearch = 8;
    private const double MinimumRelevance = 0.5;
    private static readonly TimeSpan IndexCacheDuration = TimeSpan.FromHours(12);

    // <a href=nshowmovie.html?movieid=836>A Bucket of Blood</a>  (attribut non guillemeté sur ce site)
    [GeneratedRegex("""<a\s+href=["']?nshowmovie\.html\?movieid=(?<id>\d{1,7})["']?\s*>(?<title>[^<]{1,300})</a>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MovieLinkRegex();

    // <a href=http://www.publicdomaintorrents.com/bt/btdownload.php?type=torrent&file=X.avi.torrent>Click for Divx 716MB AVI</a>
    [GeneratedRegex("""<a\s+href=["']?(?<url>https?://[^\s"'>]+/bt/btdownload\.php\?[^\s"'>]+)["']?\s*>(?<label>[^<]{1,200})</a>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TorrentLinkRegex();

    [GeneratedRegex(@"<title>(?<title>[^<]{1,300})</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"(?<size>\d{1,6}(?:[.,]\d{1,2})?)\s*(?<unit>MB|GB|Mo|Go)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SizeRegex();

    public override string ConnectorType => Type;

    public override ConnectorDescriptor Descriptor { get; } = new(
        "Public Domain Torrents",
        SourceCapabilities.Search | SourceCapabilities.TorrentFile | SourceCapabilities.Size | SourceCapabilities.Licence,
        [
            "Aucune licence par œuvre : le statut « domaine public » est déclaré globalement par le site et n'est pas vérifié.",
            "Pas d'année, de langue, de résolution ni de seeders dans les pages : ces informations restent inconnues.",
            "Tailles annoncées par le site (libellé des liens), non vérifiées avant récupération du .torrent.",
            "Extraction HTML : un changement de mise en page du site rend la source « Suspecte » jusqu'à mise à jour du connecteur.",
            "Au plus 8 pages film consultées par recherche ; l'index des titres est conservé 12 h en mémoire.",
        ]);

    public override IReadOnlyList<string> ValidateConfiguration(SourceContext source)
    {
        var issues = base.ValidateConfiguration(source).ToList();
        if (!HostPolicy.IsAllowed(TorrentHost, source.AllowedHosts))
        {
            issues.Add($"L'hôte des fichiers .torrent « {TorrentHost} » doit figurer dans les hôtes autorisés.");
        }

        return issues;
    }

    protected override async Task<ConnectorSearchResult> SearchCoreAsync(SourceContext source, ConnectorSearchRequest request, CancellationToken cancellationToken)
    {
        var queries = new[] { request.Text }.Concat(request.AlternativeTitles)
            .Select(TitleNormalizer.Normalize).Where(q => q.Length > 0).Distinct().ToList();
        if (queries.Count == 0)
        {
            return ConnectorSearchResult.Ok([]);
        }

        var index = await GetIndexAsync(source, cancellationToken);
        var candidates = index
            .Select(m => (Movie: m, Score: queries.Max(q => TitleNormalizer.Relevance(q, m.NormalizedTitle))))
            .Where(x => x.Score >= MinimumRelevance)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Movie.Title, StringComparer.Ordinal)
            .Take(Math.Min(MaxMoviePagesPerSearch, source.MaxResults))
            .Select(x => x.Movie)
            .ToList();

        var releases = new List<ReleaseCandidate>();
        var warnings = new List<string>();
        foreach (var movie in candidates)
        {
            try
            {
                var page = await Http.GetAsync(source, MoviePageUri(source, movie.Id), MaxSearchResponseBytes, cancellationToken, "text/html");
                releases.AddRange(ParseMoviePage(page.Content, movie.Id, movie.Title, source));
            }
            catch (SourceRequestException ex) when (ex.Code is SourceErrorCode.RateLimited or SourceErrorCode.Timeout or SourceErrorCode.SourceUnavailable)
            {
                // Résultat partiel plutôt qu'un échec complet : les variantes déjà collectées restent valables.
                warnings.Add($"Recherche partielle sur la source : {ex.Message}");
                break;
            }
        }

        return ConnectorSearchResult.Ok(releases.Take(source.MaxResults).ToList(), warnings: warnings);
    }

    protected override async Task<(SourceHttpResponse Response, string? Problem)> ProbeAsync(SourceContext source, CancellationToken cancellationToken)
    {
        var response = await Http.GetAsync(source, new Uri(BaseUri(source), "nshowcat.html?category=horror"), MaxSearchResponseBytes, cancellationToken, "text/html");
        var movies = ParseIndex(response.Content);
        return (response, movies.Count >= 5
            ? null
            : $"Seulement {movies.Count} lien(s) de film trouvé(s) : la structure de la page a probablement changé.");
    }

    private async Task<IReadOnlyList<IndexedMovie>> GetIndexAsync(SourceContext source, CancellationToken cancellationToken)
    {
        var key = $"pdt-index:{source.Id}:{source.BaseUrl}";
        if (cache.TryGetValue(key, out IReadOnlyList<IndexedMovie>? cached) && cached is not null)
        {
            return cached;
        }

        var response = await Http.GetAsync(source, new Uri(BaseUri(source), "nshowcat.html?category=ALL"), MaxSearchResponseBytes, cancellationToken, "text/html");
        var index = ParseIndex(response.Content);
        if (index.Count == 0)
        {
            // Converti en MalformedResponse par ConnectorBase : jamais de cache pour une page inattendue.
            throw new FormatException("Index des films vide ou illisible.");
        }

        cache.Set(key, index, IndexCacheDuration);
        return index;
    }

    internal sealed record IndexedMovie(int Id, string Title, string NormalizedTitle);

    internal static IReadOnlyList<IndexedMovie> ParseIndex(byte[] html)
    {
        var text = Decode(html);
        return MovieLinkRegex().Matches(text)
            .Select(m => (Id: int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture), Title: CleanText(m.Groups["title"].Value)))
            .Where(m => m.Title.Length > 0)
            .GroupBy(m => m.Id)
            .Select(g => new IndexedMovie(g.Key, g.First().Title, TitleNormalizer.Normalize(g.First().Title)))
            .ToList();
    }

    internal static IReadOnlyList<ReleaseCandidate> ParseMoviePage(byte[] html, int movieId, string? fallbackTitle, SourceContext source)
    {
        var text = Decode(html);
        var title = TitleRegex().Match(text) is { Success: true } t ? CleanText(t.Groups["title"].Value) : fallbackTitle ?? "";
        if (title.Length == 0)
        {
            return [];
        }

        var details = MoviePageUri(source, movieId).AbsoluteUri;
        var releases = new List<ReleaseCandidate>();
        foreach (Match match in TorrentLinkRegex().Matches(text))
        {
            var torrentUrl = ToHttpsTorrentUrl(WebUtility.HtmlDecode(match.Groups["url"].Value), source);
            if (torrentUrl is null)
            {
                continue; // lien hors domaine autorisé : ignoré, jamais suivi
            }

            var file = Uri.UnescapeDataString(torrentUrl.Query.Split('&').FirstOrDefault(p => p.StartsWith("file=", StringComparison.OrdinalIgnoreCase))?[5..] ?? "");
            if (file.Length == 0 || file.Length > 250)
            {
                continue;
            }

            var label = CleanText(match.Groups["label"].Value);
            label = label.StartsWith("Click for", StringComparison.OrdinalIgnoreCase) ? label[9..].Trim() : label;

            releases.Add(new ReleaseCandidate
            {
                ExternalId = $"{movieId}:{file}",
                ReleaseName = label.Length > 0 ? $"{title} — {label}" : title,
                WorkTitle = title,
                SizeBytes = ParseSize(label),
                Codec = label.Contains("divx", StringComparison.OrdinalIgnoreCase) ? "DivX"
                    : label.Contains("xvid", StringComparison.OrdinalIgnoreCase) ? "XviD"
                    : null,
                TorrentUrl = torrentUrl.AbsoluteUri,
                DetailsUrl = details,
                Licence = DeclaredLicence,
            });
        }

        return releases;
    }

    /// <summary>Réécrit http:// en https:// puis vérifie l'hôte contre la liste autorisée de la source.</summary>
    internal static Uri? ToHttpsTorrentUrl(string href, SourceContext source)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var https = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
        // Seuls les hôtes autorisés de la source sont acceptés (en production : www.publicdomaintorrents.com ;
        // ads.publicdomaintorrents.com n'y figure pas). Les tests y placent leur serveur local.
        return HostPolicy.IsAllowed(https.Host, source.AllowedHosts) ? https : null;
    }

    internal static long? ParseSize(string label)
    {
        var match = SizeRegex().Match(label);
        if (!match.Success
            || !double.TryParse(match.Groups["size"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            || value <= 0)
        {
            return null;
        }

        var unit = match.Groups["unit"].Value.ToUpperInvariant();
        var multiplier = unit is "GB" or "GO" ? 1024d * 1024 * 1024 : 1024d * 1024;
        return (long)Math.Round(value * multiplier);
    }

    private static Uri MoviePageUri(SourceContext source, int movieId) =>
        new(BaseUri(source), $"nshowmovie.html?movieid={movieId.ToString(CultureInfo.InvariantCulture)}");

    private static string Decode(byte[] html)
    {
        // Pages servies en UTF-8 (en-tête observé) ; BOM éventuel ignoré.
        var text = Encoding.UTF8.GetString(html);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static string CleanText(string value) =>
        Regex.Replace(WebUtility.HtmlDecode(value), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
}
```

### Pourquoi ces choix

| Choix | Alternative écartée | Raison |
|---|---|---|
| Index complet mis en cache | une requête « ALL » par recherche | sobriété : le site n'a pas d'API de recherche |
| `break` au premier `RateLimited` | continuer en boucle | `SourceHttpClient` a déjà attendu jusqu'au délai de la source ; insister dégraderait la source |
| `throw new FormatException` si index vide | `Ok([])` | une page vide est une anomalie (structure changée, page de vérification) : elle doit apparaître comme `MalformedResponse`, pas comme « 0 résultat » |
| `Licence` constante explicite | `null` | l'information « déclaré par la source, non vérifié » est utile et honnête ; `null` laisserait croire à une absence totale |
| `ExternalId = movieid:fichier` | `movieid` seul | chaque variante (AVI, MP4, PSP) est une release distincte avec son propre `.torrent` |
| Pas de `Magnet`/`InfoHash` | magnet construit à partir du nom de fichier | impossible sans le `.torrent` ; l'application calcule le hash et construit le magnet **après** validation du fichier |

## 3.2 Enregistrement (injection de dépendances)

Dans `src/TorrentSearch.Infrastructure/DependencyInjection.cs`, à côté des autres connecteurs :

```csharp
services.AddSingleton<ITorrentSourceConnector, InternetArchiveConnector>();
services.AddSingleton<ITorrentSourceConnector, AcademicTorrentsConnector>();
services.AddSingleton<ITorrentSourceConnector, PublicDomainTorrentsConnector>();   // ← ajout
```

`IMemoryCache` est déjà enregistré (`services.AddMemoryCache()`), `SourceHttpClient` aussi. Le `ConnectorRegistry` découvre le
connecteur par son `ConnectorType` (`"public-domain-torrents"`). Le connecteur est **singleton** : il ne doit garder aucun état
mutable autre que le cache (thread-safe).

## 3.3 Déclaration de la source (semis)

Dans `src/TorrentSearch.Infrastructure/Persistence/SourceSeeder.cs`, ajouter une entrée à `Seeds` :

```csharp
new("public-domain-torrents", "Public Domain Torrents", PublicDomainTorrentsConnector.Type,
    "https://www.publicdomaintorrents.info", "www.publicdomaintorrents.info,www.publicdomaintorrents.com",
    "Site de diffusion de films classiques (pages HTML)",
    "Films anciens présentés comme du domaine public. Licence non fournie par œuvre : statut déclaré par le site, non vérifié.",
    null, // aucune page de conditions trouvée le 28/10/2026
    "Extraction HTML (index nshowcat.html?category=ALL en cache 12 h, ≤ 8 pages film par recherche). Liens .torrent publiés en http:// et réécrits en https://. Désactivée par défaut : activer après lecture des limites.",
    EnabledByDefault: false, Priority: 30, MaxRequestsPerMinute: 6, HealthCheckIntervalMinutes: 1440),
```

Le semis est idempotent : au prochain démarrage, la source est **créée désactivée** ; si elle existe déjà, seuls les champs
descriptifs (nom, URL, hôtes, capacités, notes) sont mis à jour, les réglages modifiés depuis l'administration sont conservés.

Réglages à ajuster ensuite depuis l'administration (pas dans le code) : délai 20 s, cache 24 h (`CacheTtlMinutes = 1440`),
50 résultats max.

> **Hôtes autorisés** : uniquement les deux hôtes exacts. Ne pas utiliser `*.publicdomaintorrents.com` (le sous-domaine `ads.` sert
> des publicités) ni ajouter d'hôtes tiers (IMDb, Google…) : le connecteur ne doit jamais les contacter.

## 3.4 Vérifier la compilation

```powershell
dotnet build TorrentSearch.slnx -c Release
```

Puis passer aux tests (chapitre 4) **avant** toute activation.
