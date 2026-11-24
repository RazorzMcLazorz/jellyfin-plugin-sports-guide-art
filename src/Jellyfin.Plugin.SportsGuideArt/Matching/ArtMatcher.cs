using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SportsGuideArt.Matching;

/// <summary>The image slots the plugin fills.</summary>
public enum ArtKind
{
    /// <summary>The program's poster/card image.</summary>
    Primary,

    /// <summary>The landscape thumbnail.</summary>
    Thumb,

    /// <summary>The backdrop.</summary>
    Backdrop
}

/// <summary>Where a program's current primary art came from.</summary>
public enum ArtState
{
    /// <summary>No primary image.</summary>
    None,

    /// <summary>Art supplied by the guide provider.</summary>
    Original,

    /// <summary>Art this plugin copied from a matching program.</summary>
    Copied,

    /// <summary>A matchup image this plugin generated.</summary>
    Generated
}

/// <summary>Where an assignment's art comes from.</summary>
public enum MatchSource
{
    /// <summary>A program with the same title on the same channel.</summary>
    SameChannel,

    /// <summary>A program with the same title on another channel.</summary>
    Title,

    /// <summary>Art remembered from an earlier guide refresh.</summary>
    Remembered,

    /// <summary>A generated matchup image.</summary>
    Generated
}

/// <summary>A guide program as the matcher sees it.</summary>
/// <param name="Id">The program item id.</param>
/// <param name="ChannelId">The channel item id.</param>
/// <param name="Title">The guide title.</param>
/// <param name="StartUtc">The start time.</param>
/// <param name="Images">Current image paths or URLs by kind.</param>
/// <param name="State">Where the current primary art came from.</param>
public sealed record ProgramSnapshot(
    Guid Id,
    Guid ChannelId,
    string Title,
    DateTime StartUtc,
    IReadOnlyDictionary<ArtKind, string> Images,
    ArtState State);

/// <summary>What the plugin should put on one program.</summary>
/// <param name="ProgramId">The program to update.</param>
/// <param name="Source">Where the art comes from.</param>
/// <param name="Images">Source images by kind; null for <see cref="MatchSource.Generated"/>.</param>
/// <param name="Key">The program's match key.</param>
/// <param name="DonorId">The program the art was copied from, if any.</param>
public sealed record ArtAssignment(
    Guid ProgramId,
    MatchSource Source,
    IReadOnlyDictionary<ArtKind, string>? Images,
    string Key,
    Guid? DonorId);

/// <summary>Options that control matching.</summary>
public sealed class MatcherOptions
{
    /// <summary>Gets or sets a value indicating whether same-channel matches are used.</summary>
    public bool MatchSameChannel { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether title matches on other channels are used.</summary>
    public bool MatchTitleAcrossGuide { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether remembered art is used.</summary>
    public bool UseRemembered { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether matchup images may be generated.</summary>
    public bool Generate { get; set; } = true;
}

/// <summary>
/// Decides which programs need art and where it should come from.
/// Order: same channel and title, same title anywhere, remembered art, generated matchup image.
/// </summary>
public sealed class ArtMatcher
{
    private readonly TitleNormalizer _normalizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArtMatcher"/> class.
    /// </summary>
    /// <param name="normalizer">The title normalizer.</param>
    public ArtMatcher(TitleNormalizer normalizer)
    {
        _normalizer = normalizer;
    }

    /// <summary>
    /// Plans art for every program that has none, or only a generated image.
    /// </summary>
    /// <param name="programs">The guide programs.</param>
    /// <param name="remembered">Looks up remembered art by match key.</param>
    /// <param name="options">Matching options.</param>
    /// <returns>One assignment per program that should change.</returns>
    public IReadOnlyList<ArtAssignment> Plan(
        IReadOnlyList<ProgramSnapshot> programs,
        Func<string, IReadOnlyDictionary<ArtKind, string>?> remembered,
        MatcherOptions options)
    {
        var donorsByKey = programs
            .Where(p => p.State == ArtState.Original && p.Images.ContainsKey(ArtKind.Primary))
            .Select(p => (Program: p, Key: _normalizer.Key(p.Title)))
            .Where(x => x.Key.Length > 0)
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Program).ToList(), StringComparer.Ordinal);

        var result = new List<ArtAssignment>();
        foreach (var target in programs)
        {
            if (target.State is ArtState.Original or ArtState.Copied)
            {
                continue;
            }

            var key = _normalizer.Key(target.Title);
            if (key.Length == 0)
            {
                continue;
            }

            donorsByKey.TryGetValue(key, out var donors);
            ProgramSnapshot? donor = null;
            var source = MatchSource.SameChannel;

            if (donors is not null && options.MatchSameChannel)
            {
                donor = Nearest(donors.Where(d => d.ChannelId == target.ChannelId && d.Id != target.Id), target);
            }

            if (donor is null && donors is not null && options.MatchTitleAcrossGuide)
            {
                donor = Nearest(donors.Where(d => d.Id != target.Id), target);
                source = MatchSource.Title;
            }

            if (donor is not null)
            {
                result.Add(new ArtAssignment(target.Id, source, donor.Images, key, donor.Id));
                continue;
            }

            if (options.UseRemembered && remembered(key) is { Count: > 0 } saved && saved.ContainsKey(ArtKind.Primary))
            {
                result.Add(new ArtAssignment(target.Id, MatchSource.Remembered, saved, key, null));
                continue;
            }

            if (target.State == ArtState.None
                && options.Generate
                && _normalizer.TryParseMatchup(target.Title, out _, out _))
            {
                result.Add(new ArtAssignment(target.Id, MatchSource.Generated, null, key, null));
            }
        }

        return result;
    }

    private static ProgramSnapshot? Nearest(IEnumerable<ProgramSnapshot> candidates, ProgramSnapshot target)
        => candidates
            .OrderBy(d => Math.Abs((d.StartUtc - target.StartUtc).Ticks))
            .FirstOrDefault();
}
