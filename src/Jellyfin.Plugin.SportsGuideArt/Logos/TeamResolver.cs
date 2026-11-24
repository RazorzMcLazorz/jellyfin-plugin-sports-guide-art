using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SportsGuideArt.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsGuideArt.Logos;

/// <summary>Tries the enabled logo sources in the configured order.</summary>
public sealed class TeamResolver
{
    private readonly EspnTeamLookup _espn;
    private readonly TheSportsDbTeamLookup _sportsDb;
    private readonly ILogger<TeamResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TeamResolver"/> class.
    /// </summary>
    /// <param name="espn">ESPN lookup.</param>
    /// <param name="sportsDb">TheSportsDB lookup.</param>
    /// <param name="logger">Logger.</param>
    public TeamResolver(EspnTeamLookup espn, TheSportsDbTeamLookup sportsDb, ILogger<TeamResolver> logger)
    {
        _espn = espn;
        _sportsDb = sportsDb;
        _logger = logger;
    }

    /// <summary>Finds a team in the first source that knows it.</summary>
    /// <param name="teamName">The name from the guide title.</param>
    /// <param name="leagueHints">League labels from the program.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The team, or null.</returns>
    public async Task<TeamInfo?> FindAsync(string teamName, IReadOnlyCollection<string> leagueHints, CancellationToken cancellationToken)
    {
        var order = Plugin.Instance?.Configuration.LogoOrder ?? LogoSourceOrder.EspnFirst;
        IEnumerable<ITeamLookup> sources = order == LogoSourceOrder.TheSportsDbFirst
            ? [_sportsDb, _espn]
            : [_espn, _sportsDb];

        foreach (var source in sources.Where(s => s.Enabled))
        {
            try
            {
                var team = await source.FindAsync(teamName, leagueHints, cancellationToken).ConfigureAwait(false);
                if (team is not null)
                {
                    return team;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Source} lookup failed for {Team}", source.SourceName, teamName);
            }
        }

        return null;
    }
}
