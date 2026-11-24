using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SportsGuideArt.Logos;

/// <summary>A team found by a logo source.</summary>
/// <param name="Name">The team's full name.</param>
/// <param name="Abbreviation">A short code such as "ANA".</param>
/// <param name="LogoUrl">The logo URL, if any.</param>
/// <param name="Color">Primary color as hex without '#', if known.</param>
/// <param name="AltColor">Secondary color as hex without '#', if known.</param>
/// <param name="League">The league label, if known.</param>
/// <param name="Source">The source name.</param>
public sealed record TeamInfo(
    string Name,
    string Abbreviation,
    string? LogoUrl,
    string? Color,
    string? AltColor,
    string? League,
    string Source);

/// <summary>Finds a team by the name used in a guide title.</summary>
public interface ITeamLookup
{
    /// <summary>Gets the source name.</summary>
    string SourceName { get; }

    /// <summary>Gets a value indicating whether the source is enabled in settings.</summary>
    bool Enabled { get; }

    /// <summary>Finds a team.</summary>
    /// <param name="teamName">The name from the guide title.</param>
    /// <param name="leagueHints">League labels from the program, e.g. "NHL".</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The team, or null when not found.</returns>
    Task<TeamInfo?> FindAsync(string teamName, IReadOnlyCollection<string> leagueHints, CancellationToken cancellationToken);
}

/// <summary>Helpers for comparing team names.</summary>
public static class TeamNames
{
    /// <summary>Reduces a name to lower-case letters and digits separated by single spaces.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The comparison key.</returns>
    public static string Key(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        var lastSpace = true;
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c == '.' || c == '\'')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastSpace = false;
            }
            else if (!lastSpace)
            {
                builder.Append(' ');
                lastSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Picks the best candidate for a guide name: full name, then "location name", then a unique
    /// short name, location or abbreviation.
    /// </summary>
    /// <typeparam name="T">Candidate type.</typeparam>
    /// <param name="wanted">The name from the guide.</param>
    /// <param name="candidates">Candidates in preference order.</param>
    /// <param name="fullNames">Full-name variants for a candidate.</param>
    /// <param name="shortNames">Short variants (nickname, location, abbreviation) for a candidate.</param>
    /// <returns>The best candidate, or default.</returns>
    public static T? Pick<T>(
        string wanted,
        IReadOnlyList<T> candidates,
        Func<T, IEnumerable<string?>> fullNames,
        Func<T, IEnumerable<string?>> shortNames)
        where T : class
    {
        var key = Key(wanted);
        if (key.Length == 0)
        {
            return null;
        }

        var full = candidates.FirstOrDefault(c => fullNames(c).Any(n => Key(n) == key));
        if (full is not null)
        {
            return full;
        }

        var shortMatches = candidates.Where(c => shortNames(c).Any(n => Key(n) == key)).ToList();
        return shortMatches.Count == 1 ? shortMatches[0] : null;
    }
}
