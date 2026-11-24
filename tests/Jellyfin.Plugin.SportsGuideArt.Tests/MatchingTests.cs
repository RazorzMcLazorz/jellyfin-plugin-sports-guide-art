using Jellyfin.Plugin.SportsGuideArt.Configuration;
using Jellyfin.Plugin.SportsGuideArt.Matching;
using Xunit;

namespace Jellyfin.Plugin.SportsGuideArt.Tests;

public class TitleNormalizerTests
{
    private readonly TitleNormalizer _normalizer = new(PluginConfiguration.DefaultPrefixes);

    [Theory]
    [InlineData("Upcoming: Vegas Golden Knights vs Edmonton Oilers")]
    [InlineData("Pregame: Vegas Golden Knights vs Edmonton Oilers")]
    [InlineData("Postgame - Vegas Golden Knights vs. Edmonton Oilers")]
    [InlineData("NHL: Upcoming: Vegas Golden Knights VS Edmonton Oilers (7:00 PM)")]
    [InlineData("  Vegas Golden Knights  vs  Edmonton Oilers ")]
    public void Slots_share_the_game_key(string title)
    {
        Assert.Equal("vegas golden knights vs edmonton oilers", _normalizer.Key(title));
    }

    [Fact]
    public void Prefix_needs_a_separator()
    {
        Assert.Equal("next gen stats", _normalizer.Key("Next Gen Stats"));
    }

    [Theory]
    [InlineData("Upcoming: Anaheim Ducks vs Utah Mammoth", "Anaheim Ducks", "Utah Mammoth")]
    [InlineData("Boston Celtics @ New York Knicks", "Boston Celtics", "New York Knicks")]
    [InlineData("Duke at North Carolina", "Duke", "North Carolina")]
    [InlineData("Arsenal v Chelsea", "Arsenal", "Chelsea")]
    public void Parses_matchups(string title, string first, string second)
    {
        Assert.True(_normalizer.TryParseMatchup(title, out var a, out var b));
        Assert.Equal(first, a);
        Assert.Equal(second, b);
    }

    [Theory]
    [InlineData("SportsCenter")]
    [InlineData("Upcoming: NHL Tonight")]
    public void Rejects_non_matchups(string title)
    {
        Assert.False(_normalizer.TryParseMatchup(title, out _, out _));
    }
}

public class ArtMatcherTests
{
    private static readonly Guid ChannelA = Guid.NewGuid();
    private static readonly Guid ChannelB = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 11, 25, 1, 0, 0, DateTimeKind.Utc);

    private readonly ArtMatcher _matcher = new(new TitleNormalizer(PluginConfiguration.DefaultPrefixes));

    private static ProgramSnapshot P(Guid channel, string title, int hour, string? primary = null, ArtState? state = null)
    {
        var images = new Dictionary<ArtKind, string>();
        if (primary is not null)
        {
            images[ArtKind.Primary] = primary;
        }

        return new ProgramSnapshot(Guid.NewGuid(), channel, title, T0.AddHours(hour), images, state ?? (primary is null ? ArtState.None : ArtState.Original));
    }

    private static IReadOnlyDictionary<ArtKind, string>? None(string key) => null;

    [Fact]
    public void Fills_rebel_slots_from_the_game_on_the_same_channel()
    {
        var game = P(ChannelA, "Vegas Golden Knights vs Edmonton Oilers", 0, "https://img/vgk.poster.png");
        var upcoming = P(ChannelA, "Upcoming: Vegas Golden Knights vs Edmonton Oilers", -18);
        var pregame = P(ChannelA, "Pregame: Vegas Golden Knights vs Edmonton Oilers", -1);
        var postgame = P(ChannelA, "Postgame: Vegas Golden Knights vs Edmonton Oilers", 3);

        var plan = _matcher.Plan([game, upcoming, pregame, postgame], None, new MatcherOptions());

        Assert.Equal(3, plan.Count);
        Assert.All(plan, a =>
        {
            Assert.Equal(MatchSource.SameChannel, a.Source);
            Assert.Equal(game.Id, a.DonorId);
        });
    }

    [Fact]
    public void Prefers_same_channel_over_closer_title_match()
    {
        var sameChannel = P(ChannelA, "A vs B", 5, "same.png");
        var otherChannel = P(ChannelB, "A vs B", 0, "other.png");
        var target = P(ChannelA, "Upcoming: A vs B", 0);

        var plan = _matcher.Plan([sameChannel, otherChannel, target], None, new MatcherOptions());

        Assert.Equal(sameChannel.Id, Assert.Single(plan).DonorId);
    }

    [Fact]
    public void Falls_back_to_title_then_remembered_then_generated()
    {
        var otherChannel = P(ChannelB, "A vs B", 0, "other.png");
        var titleTarget = P(ChannelA, "Pregame: A vs B", 0);
        var rememberedTarget = P(ChannelA, "Postgame: C vs D", 0);
        var generatedTarget = P(ChannelA, "Upcoming: Eagles vs Falcons", 0);
        var nothing = P(ChannelA, "Off Air", 0);

        IReadOnlyDictionary<ArtKind, string>? Remembered(string key)
            => key == "c vs d" ? new Dictionary<ArtKind, string> { [ArtKind.Primary] = "/cache/cd.png" } : null;

        var plan = _matcher.Plan([otherChannel, titleTarget, rememberedTarget, generatedTarget, nothing], Remembered, new MatcherOptions())
            .ToDictionary(a => a.ProgramId);

        Assert.Equal(3, plan.Count);
        Assert.Equal(MatchSource.Title, plan[titleTarget.Id].Source);
        Assert.Equal(MatchSource.Remembered, plan[rememberedTarget.Id].Source);
        Assert.Equal(MatchSource.Generated, plan[generatedTarget.Id].Source);
    }

    [Fact]
    public void Upgrades_generated_art_when_real_art_appears_but_never_regenerates()
    {
        var generatedWithDonor = P(ChannelA, "Upcoming: A vs B", 0, "/data/generated/x.png", ArtState.Generated);
        var donor = P(ChannelA, "A vs B", 3, "real.png");
        var generatedAlone = P(ChannelB, "Upcoming: C vs D", 0, "/data/generated/y.png", ArtState.Generated);

        var plan = _matcher.Plan([generatedWithDonor, donor, generatedAlone], None, new MatcherOptions());

        var only = Assert.Single(plan);
        Assert.Equal(generatedWithDonor.Id, only.ProgramId);
        Assert.Equal(MatchSource.SameChannel, only.Source);
    }

    [Fact]
    public void Leaves_provider_and_copied_art_alone()
    {
        var original = P(ChannelA, "Upcoming: A vs B", 0, "provider.png");
        var copied = P(ChannelA, "Pregame: A vs B", 1, "/data/copied/z.png", ArtState.Copied);
        var donor = P(ChannelA, "A vs B", 2, "real.png");

        Assert.Empty(_matcher.Plan([original, copied, donor], None, new MatcherOptions()));
    }

    [Fact]
    public void Respects_disabled_options()
    {
        var donor = P(ChannelB, "A vs B", 0, "other.png");
        var target = P(ChannelA, "Upcoming: A vs B", 0);

        var plan = _matcher.Plan(
            [donor, target],
            None,
            new MatcherOptions { MatchTitleAcrossGuide = false, Generate = false });

        Assert.Empty(plan);
    }
}
