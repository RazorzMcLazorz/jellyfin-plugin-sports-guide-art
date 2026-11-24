using Jellyfin.Plugin.SportsGuideArt.Caching;
using Jellyfin.Plugin.SportsGuideArt.Imaging;
using Jellyfin.Plugin.SportsGuideArt.Logos;
using SkiaSharp;
using Xunit;

namespace Jellyfin.Plugin.SportsGuideArt.Tests;

public class TeamNameTests
{
    private const string EspnJson = """
    {"sports":[{"leagues":[{"abbreviation":"NHL","teams":[
      {"team":{"displayName":"Anaheim Ducks","shortDisplayName":"Ducks","name":"Ducks","location":"Anaheim","abbreviation":"ANA","color":"fc4c02","alternateColor":"000000","logos":[{"href":"https://a.espncdn.com/i/teamlogos/nhl/500/ana.png"}]}},
      {"team":{"displayName":"Utah Mammoth","shortDisplayName":"Mammoth","name":"Mammoth","location":"Utah","abbreviation":"UTA","color":"71afe5","logos":[{"href":"https://a.espncdn.com/i/teamlogos/nhl/500/utah.png"}]}},
      {"team":{"displayName":"New York Rangers","shortDisplayName":"Rangers","name":"Rangers","location":"New York","abbreviation":"NYR","logos":[]}},
      {"team":{"displayName":"New York Islanders","shortDisplayName":"Islanders","name":"Islanders","location":"New York","abbreviation":"NYI","logos":[]}}
    ]}]}]}
    """;

    private static readonly IReadOnlyList<EspnTeamLookup.EspnTeam> Teams = EspnTeamLookup.Parse(EspnJson, "hockey/nhl");

    private static EspnTeamLookup.EspnTeam? Pick(string name) => TeamNames.Pick(
        name,
        Teams,
        t => [t.DisplayName, t.Location + " " + t.Name],
        t => [t.ShortDisplayName, t.Name, t.Location, t.Abbreviation]);

    [Fact]
    public void Parses_espn_teams()
    {
        Assert.Equal(4, Teams.Count);
        Assert.Equal("https://a.espncdn.com/i/teamlogos/nhl/500/ana.png", Teams[0].LogoUrl);
        Assert.Equal("NHL", Teams[0].League);
    }

    [Theory]
    [InlineData("Anaheim Ducks", "ANA")]
    [InlineData("anaheim ducks", "ANA")]
    [InlineData("Ducks", "ANA")]
    [InlineData("Utah", "UTA")]
    [InlineData("NYR", "NYR")]
    public void Picks_by_full_or_unique_short_name(string name, string abbreviation)
    {
        Assert.Equal(abbreviation, Pick(name)?.Abbreviation);
    }

    [Fact]
    public void Ambiguous_short_name_is_not_guessed()
    {
        Assert.Null(Pick("New York"));
    }

    [Fact]
    public void Parses_thesportsdb_teams()
    {
        const string json = """{"teams":[{"strTeam":"Anaheim Ducks","strTeamShort":"ANA","strBadge":"https://r2.thesportsdb.com/badge.png","strColour1":"#cf4520","strLeague":"NHL"}]}""";
        var team = Assert.Single(TheSportsDbTeamLookup.Parse(json));
        Assert.Equal("cf4520", team.Color1);
        Assert.Equal("https://r2.thesportsdb.com/badge.png", team.Badge);
        Assert.Empty(TheSportsDbTeamLookup.Parse("""{"teams":null}"""));
    }
}

public class ImageTests
{
    [Fact]
    public void Detects_image_types()
    {
        Assert.Equal(".png", ImageCache.DetectExtension([0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(".jpg", ImageCache.DetectExtension([0xFF, 0xD8, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Null(ImageCache.DetectExtension("<html></html>"u8));
    }

    [Theory]
    [InlineData("000000", "71afe5", "71afe5")]
    [InlineData("fc4c02", "000000", "fc4c02")]
    [InlineData("ffffff", null, "ffffff")]
    [InlineData(null, "123456", "123456")]
    public void Picks_a_usable_team_color(string? color, string? alt, string? expected)
    {
        Assert.Equal(expected, MatchupImageRenderer.PickColor(color, alt));
    }

    [Fact]
    public void Path_containment_is_folder_aware()
    {
        Assert.True(ImageCache.IsIn("/data/sga/generated/a.png", "/data/sga/generated"));
        Assert.False(ImageCache.IsIn("/data/sga/generated-old/a.png", "/data/sga/generated"));
        Assert.False(ImageCache.IsIn(null, "/data/sga"));
    }

    [Fact]
    public void Renders_poster_and_landscape_with_and_without_logos()
    {
        var dir = Directory.CreateTempSubdirectory("sga-test").FullName;
        var logo = Path.Combine(dir, "logo.png");
        using (var bitmap = new SKBitmap(64, 64))
        {
            bitmap.Erase(SKColors.Orange);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(logo, data.ToArray());
        }

        var renderer = new MatchupImageRenderer();
        var first = new MatchupSide("Anaheim Ducks", "ANA", logo, "fc4c02");
        var second = new MatchupSide("Utah Mammoth", "UTA", null, null);

        using var poster = SKBitmap.Decode(renderer.RenderPoster(first, second, "NHL"));
        using var landscape = SKBitmap.Decode(renderer.RenderLandscape(first, second, null));

        Assert.Equal((600, 900), (poster.Width, poster.Height));
        Assert.Equal((1280, 720), (landscape.Width, landscape.Height));

        var outDir = Environment.GetEnvironmentVariable("SGA_RENDER_OUT");
        if (!string.IsNullOrEmpty(outDir))
        {
            File.WriteAllBytes(Path.Combine(outDir, "poster.png"), renderer.RenderPoster(first, second, "NHL"));
            File.WriteAllBytes(Path.Combine(outDir, "landscape.png"), renderer.RenderLandscape(first, second, "NHL"));
        }
    }
}
