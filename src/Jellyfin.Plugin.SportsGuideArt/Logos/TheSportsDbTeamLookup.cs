using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SportsGuideArt.Caching;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsGuideArt.Logos;

/// <summary>
/// Looks teams up on TheSportsDB by name. Results (including misses) are cached on disk,
/// and calls are spaced out to respect the free key's rate limit.
/// </summary>
public sealed class TheSportsDbTeamLookup : ITeamLookup, IDisposable
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(14);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(1);
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(2.1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TheSportsDbTeamLookup> _logger;
    private readonly string _folder;
    private readonly ConcurrentDictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastCallUtc = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="TheSportsDbTeamLookup"/> class.
    /// </summary>
    /// <param name="cache">Image cache (its root holds lookup results).</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="logger">Logger.</param>
    public TheSportsDbTeamLookup(ImageCache cache, IHttpClientFactory httpClientFactory, ILogger<TheSportsDbTeamLookup> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _folder = Path.Combine(cache.Root, "teams", "thesportsdb");
        Directory.CreateDirectory(_folder);
    }

    /// <inheritdoc />
    public string SourceName => "TheSportsDB";

    /// <inheritdoc />
    public bool Enabled => Plugin.Instance?.Configuration.UseTheSportsDb ?? false;

    /// <inheritdoc />
    public async Task<TeamInfo?> FindAsync(string teamName, IReadOnlyCollection<string> leagueHints, CancellationToken cancellationToken)
    {
        var key = TeamNames.Key(teamName);
        if (key.Length == 0)
        {
            return null;
        }

        var json = await GetSearchAsync(key, teamName, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        var teams = Parse(json);
        var hinted = teams.Where(t => leagueHints.Any(h => t.League?.Contains(h, StringComparison.OrdinalIgnoreCase) == true)).ToList();
        var team = TeamNames.Pick(teamName, hinted.Count > 0 ? hinted : teams, t => [t.Name, t.AlternateName], t => [t.Short]);
        return team is null
            ? null
            : new TeamInfo(team.Name, team.Short ?? Abbreviate(team.Name), team.Badge, team.Color1, team.Color2, team.League, SourceName);
    }

    private static string Abbreviate(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1
            ? string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])))
            : name[..Math.Min(3, name.Length)].ToUpperInvariant();
    }

    private async Task<string?> GetSearchAsync(string key, string teamName, CancellationToken cancellationToken)
    {
        if (_memory.TryGetValue(key, out var mem))
        {
            return mem;
        }

        var file = Path.Combine(_folder, ImageCache.Hash(key) + ".json");
        if (File.Exists(file))
        {
            var text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
            var isMiss = Parse(text).Count == 0;
            if (age < (isMiss ? MissTtl : HitTtl))
            {
                _memory[key] = text;
                return text;
            }
        }

        var apiKey = Plugin.Instance?.Configuration.TheSportsDbApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = "123";
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _lastCallUtc + MinSpacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _lastCallUtc = DateTime.UtcNow;
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var url = $"https://www.thesportsdb.com/api/v1/json/{Uri.EscapeDataString(apiKey.Trim())}/searchteams.php?t={Uri.EscapeDataString(teamName.Trim().Replace(' ', '_'))}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogInformation("TheSportsDB rate limit reached; will retry on a later run");
                return null;
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(file, json, cancellationToken).ConfigureAwait(false);
            _memory[key] = json;
            return json;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "TheSportsDB lookup failed for {Team}", teamName);
            return File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Parses a TheSportsDB search response.</summary>
    /// <param name="json">The response body.</param>
    /// <returns>The teams.</returns>
    internal static IReadOnlyList<SportsDbTeam> Parse(string json)
    {
        var result = new List<SportsDbTeam>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var t in teams.EnumerateArray())
            {
                var name = Str(t, "strTeam");
                if (name is null)
                {
                    continue;
                }

                result.Add(new SportsDbTeam(
                    name,
                    Str(t, "strTeamAlternate"),
                    Str(t, "strTeamShort"),
                    Str(t, "strBadge") ?? Str(t, "strTeamBadge"),
                    Str(t, "strColour1")?.TrimStart('#'),
                    Str(t, "strColour2")?.TrimStart('#'),
                    Str(t, "strLeague")));
            }
        }
        catch (JsonException)
        {
        }

        return result;
    }

    private static string? Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>A TheSportsDB team entry.</summary>
    internal sealed record SportsDbTeam(
        string Name,
        string? AlternateName,
        string? Short,
        string? Badge,
        string? Color1,
        string? Color2,
        string? League);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
