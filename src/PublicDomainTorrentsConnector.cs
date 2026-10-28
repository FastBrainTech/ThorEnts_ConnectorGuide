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
