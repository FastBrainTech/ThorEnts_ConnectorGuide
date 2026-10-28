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
