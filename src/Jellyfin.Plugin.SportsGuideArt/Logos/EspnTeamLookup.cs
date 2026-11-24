using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SportsGuideArt.Caching;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsGuideArt.Logos;

/// <summary>
/// Looks teams up in ESPN's public team lists, one list per league, cached on disk for a week.
/// </summary>
public sealed class EspnTeamLookup : ITeamLookup, IDisposable
{
    private static readonly TimeSpan ListTtl = TimeSpan.FromDays(7);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EspnTeamLookup> _logger;
    private readonly string _folder;
    private readonly ConcurrentDictionary<string, IReadOnlyList<EspnTeam>> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="EspnTeamLookup"/> class.
    /// </summary>
    /// <param name="cache">Image cache (its root holds the team lists).</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public EspnTeamLookup(ImageCache cache, IHttpClientFactory httpClientFactory, ILogger<EspnTeamLookup> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _folder = Path.Combine(cache.Root, "teams", "espn");
        Directory.CreateDirectory(_folder);
    }

    /// <inheritdoc />
    public string SourceName => "ESPN";

    /// <inheritdoc />
    public bool Enabled => Plugin.Instance?.Configuration.UseEspn ?? true;

    /// <inheritdoc />
    public async Task<TeamInfo?> FindAsync(string teamName, IReadOnlyCollection<string> leagueHints, CancellationToken cancellationToken)
    {
        var leagues = Plugin.Instance?.Configuration.EspnLeagues ?? Configuration.PluginConfiguration.DefaultEspnLeagues;

        // Leagues named by the program's categories go first, e.g. "NHL" -> hockey/nhl.
        var ordered = leagues
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim().Trim('/'))
            .OrderByDescending(l => leagueHints.Any(h => LeagueMatches(l, h)))
            .ToList();

        var hinted = ordered.Where(l => leagueHints.Any(h => LeagueMatches(l, h))).ToList();
        foreach (var group in hinted.Count > 0 ? new[] { hinted, ordered } : new[] { ordered })
        {
            var candidates = new List<EspnTeam>();
            foreach (var league in group)
            {
                candidates.AddRange(await GetLeagueAsync(league, cancellationToken).ConfigureAwait(false));
            }

            var team = TeamNames.Pick(
                teamName,
                candidates,
                t => [t.DisplayName, t.Location + " " + t.Name],
                t => [t.ShortDisplayName, t.Name, t.Location, t.Abbreviation]);
            if (team is not null)
            {
                return new TeamInfo(team.DisplayName, team.Abbreviation, team.LogoUrl, team.Color, team.AltColor, team.League, SourceName);
            }
        }

        return null;
    }

    private static bool LeagueMatches(string leaguePath, string hint)
    {
        var league = leaguePath.Split('/').Last();
        var h = hint.Trim().ToLowerInvariant();
        return league.Equals(h, StringComparison.OrdinalIgnoreCase)
               || (h == "ncaab" && league.Contains("college-basketball", StringComparison.Ordinal))
               || (h is "ncaaf" or "cfb" && league == "college-football")
               || (h == "mls" && league == "usa.1");
    }

    private async Task<IReadOnlyList<EspnTeam>> GetLeagueAsync(string league, CancellationToken cancellationToken)
    {
        if (_memory.TryGetValue(league, out var cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_memory.TryGetValue(league, out cached))
            {
                return cached;
            }

            var file = Path.Combine(_folder, league.Replace('/', '_') + ".json");
            string? json = null;
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < ListTtl)
            {
                json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    var client = _httpClientFactory.CreateClient(NamedClient.Default);
                    var url = $"https://site.api.espn.com/apis/site/v2/sports/{league}/teams?limit=1000";
                    json = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(file, json, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "ESPN team list for {League} unavailable", league);
                    if (File.Exists(file))
                    {
                        json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var teams = json is null ? [] : Parse(json, league);
            _memory[league] = teams;
            return teams;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Parses an ESPN teams response.</summary>
    /// <param name="json">The response body.</param>
    /// <param name="league">The league path.</param>
    /// <returns>The teams.</returns>
    internal static IReadOnlyList<EspnTeam> Parse(string json, string league)
    {
        var result = new List<EspnTeam>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var sport in Array(doc.RootElement, "sports"))
            {
                foreach (var lg in Array(sport, "leagues"))
                {
                    var leagueName = Str(lg, "abbreviation") ?? league;
                    foreach (var entry in Array(lg, "teams"))
                    {
                        if (!entry.TryGetProperty("team", out var t))
                        {
                            continue;
                        }

                        var logo = Array(t, "logos").Select(l => Str(l, "href")).FirstOrDefault(h => h is not null);
                        var display = Str(t, "displayName");
                        if (display is null)
                        {
                            continue;
                        }

                        result.Add(new EspnTeam(
                            display,
                            Str(t, "shortDisplayName"),
                            Str(t, "name"),
                            Str(t, "location"),
                            Str(t, "abbreviation") ?? display[..Math.Min(3, display.Length)].ToUpperInvariant(),
                            logo,
                            Str(t, "color"),
                            Str(t, "alternateColor"),
                            leagueName));
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : [];

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>An ESPN team entry.</summary>
    internal sealed record EspnTeam(
        string DisplayName,
        string? ShortDisplayName,
        string? Name,
        string? Location,
        string Abbreviation,
        string? LogoUrl,
        string? Color,
        string? AltColor,
        string League);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
