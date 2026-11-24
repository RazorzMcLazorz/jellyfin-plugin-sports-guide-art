using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SportsGuideArt.Matching;

/// <summary>
/// Turns guide titles such as "Upcoming: Anaheim Ducks vs Utah Mammoth" into a stable match key
/// and splits matchup titles into their two sides.
/// </summary>
public sealed class TitleNormalizer
{
    private static readonly Regex MatchupSeparator = new(
        @"\s+(?:vs\.?|v\.?|versus|@|at)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TrailingBracket = new(
        @"\s*[\(\[][^\(\)\[\]]*[\)\]]\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly Regex? _prefix;
    private readonly bool _stripTrailingBrackets;

    /// <summary>
    /// Initializes a new instance of the <see cref="TitleNormalizer"/> class.
    /// </summary>
    /// <param name="prefixes">Labels to strip from the start of titles, without punctuation (e.g. "Upcoming").</param>
    /// <param name="stripTrailingBrackets">Whether to drop a trailing "(...)" or "[...]" such as a start time.</param>
    public TitleNormalizer(IEnumerable<string> prefixes, bool stripTrailingBrackets = true)
    {
        var cleaned = prefixes
            .Select(p => p?.Trim().TrimEnd(':', '-', '|').Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p!.Length)
            .Select(p => Regex.Escape(p!))
            .ToList();

        if (cleaned.Count > 0)
        {
            _prefix = new Regex(
                @"^\s*(?:" + string.Join('|', cleaned) + @")\s*(?::|\||-|–|—)\s*",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        _stripTrailingBrackets = stripTrailingBrackets;
    }

    /// <summary>
    /// Removes configured prefixes and trailing brackets, keeping the original casing.
    /// </summary>
    /// <param name="title">The guide title.</param>
    /// <returns>The cleaned display title.</returns>
    public string Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var result = title.Trim();
        if (_prefix is not null)
        {
            // Strip repeatedly: "NHL: Upcoming: A vs B".
            string previous;
            do
            {
                previous = result;
                result = _prefix.Replace(result, string.Empty, 1);
            }
            while (!string.Equals(previous, result, StringComparison.Ordinal) && result.Length > 0);
        }

        if (_stripTrailingBrackets)
        {
            result = TrailingBracket.Replace(result, string.Empty);
        }

        return Whitespace.Replace(result, " ").Trim();
    }

    /// <summary>
    /// Builds the key used to match programs that describe the same event.
    /// </summary>
    /// <param name="title">The guide title.</param>
    /// <returns>A lower-case key, or an empty string when the title has no usable text.</returns>
    public string Key(string? title)
    {
        var cleaned = MatchupSeparator.Replace(Clean(title), " vs ");
        var normalized = cleaned.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return Whitespace.Replace(builder.ToString(), " ").Trim();
    }

    /// <summary>
    /// Splits a matchup title into its two teams.
    /// </summary>
    /// <param name="title">The guide title.</param>
    /// <param name="first">The team before the separator.</param>
    /// <param name="second">The team after the separator.</param>
    /// <returns>True when the title is a two-sided matchup.</returns>
    public bool TryParseMatchup(string? title, out string first, out string second)
    {
        first = string.Empty;
        second = string.Empty;
        var cleaned = Clean(title);
        var parts = MatchupSeparator.Split(cleaned);
        if (parts.Length != 2)
        {
            return false;
        }

        first = parts[0].Trim();
        second = parts[1].Trim();
        return first.Length > 1 && second.Length > 1;
    }
}
