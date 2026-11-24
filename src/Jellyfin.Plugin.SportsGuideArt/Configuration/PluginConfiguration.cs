using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SportsGuideArt.Configuration;

/// <summary>Which logo source is tried first.</summary>
public enum LogoSourceOrder
{
    /// <summary>ESPN first, then TheSportsDB.</summary>
    EspnFirst,

    /// <summary>TheSportsDB first, then ESPN.</summary>
    TheSportsDbFirst
}

/// <summary>Plugin settings.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets the default title prefixes.</summary>
    public static string[] DefaultPrefixes =>
    [
        "Upcoming", "Coming Up", "Next", "Pregame", "Pre-game", "Pre Game", "Postgame", "Post-game", "Post Game",
        "Live", "Replay", "Encore", "Starting Soon", "Ended",
        "NHL", "NBA", "NFL", "MLB", "MLS", "WNBA", "NCAAB", "NCAAF", "NCAA", "CFB", "CBB"
    ];

    /// <summary>Gets the default ESPN leagues (sport/league paths).</summary>
    public static string[] DefaultEspnLeagues =>
    [
        "hockey/nhl", "basketball/nba", "football/nfl", "baseball/mlb", "basketball/wnba", "soccer/usa.1",
        "football/college-football", "basketball/mens-college-basketball", "basketball/womens-college-basketball"
    ];

    /// <summary>Gets or sets labels stripped from the start of titles before matching.</summary>
    public string[] TitlePrefixes { get; set; } = DefaultPrefixes;

    /// <summary>Gets or sets a value indicating whether a trailing "(...)" or "[...]" is ignored.</summary>
    public bool StripTrailingBrackets { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether art is copied from the same title on the same channel.</summary>
    public bool MatchSameChannel { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether art is copied from the same title on any channel.</summary>
    public bool MatchTitleAcrossGuide { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether art seen in earlier refreshes is reused.</summary>
    public bool UseRememberedArt { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether matchup images are generated when nothing matches.</summary>
    public bool GenerateMatchupImages { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether ESPN is used for team logos.</summary>
    public bool UseEspn { get; set; } = true;

    /// <summary>Gets or sets the ESPN leagues searched for teams.</summary>
    public string[] EspnLeagues { get; set; } = DefaultEspnLeagues;

    /// <summary>Gets or sets a value indicating whether TheSportsDB is used for team logos.</summary>
    public bool UseTheSportsDb { get; set; }

    /// <summary>Gets or sets the TheSportsDB API key ("123" is the free key).</summary>
    public string TheSportsDbApiKey { get; set; } = "123";

    /// <summary>Gets or sets which logo source is tried first.</summary>
    public LogoSourceOrder LogoOrder { get; set; } = LogoSourceOrder.EspnFirst;

    /// <summary>Gets or sets channel name filters; empty means every channel.</summary>
    public string[] ChannelFilters { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether the plugin runs right after each guide refresh.</summary>
    public bool RunAfterGuideRefresh { get; set; } = true;

    /// <summary>Gets or sets how many days unused cached images are kept.</summary>
    public int CacheRetentionDays { get; set; } = 30;
}
