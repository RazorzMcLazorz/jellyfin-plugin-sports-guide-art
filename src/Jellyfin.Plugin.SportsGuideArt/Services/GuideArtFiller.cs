using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SportsGuideArt.Caching;
using Jellyfin.Plugin.SportsGuideArt.Configuration;
using Jellyfin.Plugin.SportsGuideArt.Imaging;
using Jellyfin.Plugin.SportsGuideArt.Logos;
using Jellyfin.Plugin.SportsGuideArt.Matching;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsGuideArt.Services;

/// <summary>Counts from one fill run.</summary>
/// <param name="Scanned">Programs looked at.</param>
/// <param name="SameChannel">Filled from the same channel.</param>
/// <param name="Title">Filled from a title match on another channel.</param>
/// <param name="Remembered">Filled from remembered art.</param>
/// <param name="Generated">Filled with a generated matchup image.</param>
/// <param name="Failed">Assignments that could not be applied.</param>
public sealed record FillReport(int Scanned, int SameChannel, int Title, int Remembered, int Generated, int Failed);

/// <summary>Finds guide programs without art and fills them.</summary>
public sealed class GuideArtFiller : IDisposable
{
    private static readonly ArtKind[] Kinds = [ArtKind.Primary, ArtKind.Thumb, ArtKind.Backdrop];

    private readonly ILibraryManager _libraryManager;
    private readonly ImageCache _cache;
    private readonly ArtIndex _index;
    private readonly TeamResolver _teams;
    private readonly MatchupImageRenderer _renderer;
    private readonly ILogger<GuideArtFiller> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="GuideArtFiller"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="cache">Image cache.</param>
    /// <param name="index">Art index.</param>
    /// <param name="teams">Team resolver.</param>
    /// <param name="renderer">Matchup renderer.</param>
    /// <param name="logger">Logger.</param>
    public GuideArtFiller(
        ILibraryManager libraryManager,
        ImageCache cache,
        ArtIndex index,
        TeamResolver teams,
        MatchupImageRenderer renderer,
        ILogger<GuideArtFiller> logger)
    {
        _libraryManager = libraryManager;
        _cache = cache;
        _index = index;
        _teams = teams;
        _renderer = renderer;
        _logger = logger;
    }

    /// <summary>Runs one fill pass. Concurrent calls wait for the running pass.</summary>
    /// <param name="progress">Progress reporter (0-100).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run's counts.</returns>
    public async Task<FillReport> RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<FillReport> RunCoreAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var normalizer = new TitleNormalizer(config.TitlePrefixes ?? [], config.StripTrailingBrackets);
        var matcher = new ArtMatcher(normalizer);

        var programs = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvProgram],
            MinEndDate = DateTime.UtcNow.AddHours(-1),
            DtoOptions = new DtoOptions(true)
        }).OfType<LiveTvProgram>().ToList();

        var channels = new Dictionary<Guid, BaseItem?>();
        BaseItem? Channel(Guid id)
        {
            if (!channels.TryGetValue(id, out var channel))
            {
                channel = id == Guid.Empty ? null : _libraryManager.GetItemById(id);
                channels[id] = channel;
            }

            return channel;
        }

        var filters = (config.ChannelFilters ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f.Trim()).ToList();
        if (filters.Count > 0)
        {
            programs = programs
                .Where(p => Channel(p.ChannelId)?.Name is { } name && filters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        var byId = programs.ToDictionary(p => p.Id);
        var snapshots = programs.Select(Snapshot).ToList();
        progress?.Report(10);

        // Remember provider art for matchup titles so later slots keep it after the game leaves the guide.
        if (config.UseRememberedArt)
        {
            foreach (var donor in snapshots
                         .Where(s => s.State == ArtState.Original && normalizer.TryParseMatchup(s.Title, out _, out _))
                         .GroupBy(s => normalizer.Key(s.Title))
                         .Where(g => g.Key.Length > 0)
                         .Select(g => g.OrderBy(s => s.StartUtc).First()))
            {
                var local = await CacheImagesAsync(donor.Images, cancellationToken).ConfigureAwait(false);
                if (local.ContainsKey(ArtKind.Primary))
                {
                    _index.Set(normalizer.Key(donor.Title), local);
                }
            }
        }

        progress?.Report(30);

        var plan = matcher.Plan(
            snapshots,
            _index.Get,
            new MatcherOptions
            {
                MatchSameChannel = config.MatchSameChannel,
                MatchTitleAcrossGuide = config.MatchTitleAcrossGuide,
                UseRemembered = config.UseRememberedArt,
                Generate = config.GenerateMatchupImages
            });

        int same = 0, title = 0, remembered = 0, generated = 0, failed = 0;
        for (var i = 0; i < plan.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assignment = plan[i];
            var program = byId[assignment.ProgramId];
            try
            {
                IReadOnlyDictionary<ArtKind, string> files = assignment.Source == MatchSource.Generated
                    ? await GenerateAsync(program, normalizer, cancellationToken).ConfigureAwait(false)
                    : await CacheImagesAsync(assignment.Images!, cancellationToken).ConfigureAwait(false);

                if (!files.ContainsKey(ArtKind.Primary))
                {
                    failed++;
                    continue;
                }

                Apply(program, files, assignment.Source == MatchSource.Generated);
                await _libraryManager.UpdateItemAsync(program, Channel(program.ChannelId) ?? program, ItemUpdateType.ImageUpdate, cancellationToken)
                    .ConfigureAwait(false);

                switch (assignment.Source)
                {
                    case MatchSource.SameChannel: same++; break;
                    case MatchSource.Title: title++; break;
                    case MatchSource.Remembered: remembered++; break;
                    default: generated++; break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning(ex, "Could not fill art for {Title}", program.Name);
            }

            progress?.Report(30 + (65.0 * (i + 1) / plan.Count));
        }

        _index.Save(config.CacheRetentionDays);
        var pruned = _cache.Prune(config.CacheRetentionDays);
        progress?.Report(100);

        var report = new FillReport(programs.Count, same, title, remembered, generated, failed);
        _logger.LogInformation(
            "Sports Guide Art: scanned {Scanned} programs; filled {Same} same-channel, {Title} by title, {Remembered} remembered, {Generated} generated; {Failed} failed; pruned {Pruned} cached files",
            report.Scanned,
            report.SameChannel,
            report.Title,
            report.Remembered,
            report.Generated,
            report.Failed,
            pruned);
        return report;
    }

    private ProgramSnapshot Snapshot(LiveTvProgram program)
    {
        var images = new Dictionary<ArtKind, string>();
        foreach (var kind in Kinds)
        {
            var path = program.GetImages(ToImageType(kind)).FirstOrDefault()?.Path;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (ImageCache.IsIn(path, _cache.Root))
            {
                // Our file vanished (pruned or deleted): treat the slot as empty so it is filled again.
                if (!File.Exists(path))
                {
                    continue;
                }

                // Keep files that programs still point at out of the prune window.
                ImageCache.Touch(path);
            }

            images[kind] = path;
        }

        images.TryGetValue(ArtKind.Primary, out var primary);
        var state = primary is null
            ? ArtState.None
            : ImageCache.IsIn(primary, _cache.GeneratedDir)
                ? ArtState.Generated
                : ImageCache.IsIn(primary, _cache.CopiedDir) ? ArtState.Copied : ArtState.Original;

        return new ProgramSnapshot(program.Id, program.ChannelId, program.Name ?? string.Empty, program.StartDate, images, state);
    }

    private async Task<IReadOnlyDictionary<ArtKind, string>> CacheImagesAsync(IReadOnlyDictionary<ArtKind, string> sources, CancellationToken cancellationToken)
    {
        var result = new Dictionary<ArtKind, string>();
        foreach (var (kind, source) in sources)
        {
            var local = await _cache.EnsureLocalAsync(source, _cache.CopiedDir, cancellationToken).ConfigureAwait(false);
            if (local is not null)
            {
                result[kind] = local;
            }
        }

        return result;
    }

    private async Task<IReadOnlyDictionary<ArtKind, string>> GenerateAsync(LiveTvProgram program, TitleNormalizer normalizer, CancellationToken cancellationToken)
    {
        if (!normalizer.TryParseMatchup(program.Name, out var firstName, out var secondName))
        {
            return new Dictionary<ArtKind, string>();
        }

        var hints = (program.Genres ?? []).Concat(program.Tags ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        var first = await ResolveSideAsync(firstName, hints, cancellationToken).ConfigureAwait(false);
        var second = await ResolveSideAsync(secondName, hints, cancellationToken).ConfigureAwait(false);
        var caption = first.League ?? second.League;

        var id = ImageCache.Hash(string.Join(
            '|',
            MatchupImageRenderer.Version,
            first.Side.Name,
            first.Side.LogoPath,
            first.Side.Color,
            second.Side.Name,
            second.Side.LogoPath,
            second.Side.Color,
            caption));

        var poster = Path.Combine(_cache.GeneratedDir, id + ".poster.png");
        var landscape = Path.Combine(_cache.GeneratedDir, id + ".landscape.png");
        if (!File.Exists(poster))
        {
            await ImageCache.WriteAsync(_cache.GeneratedDir, Path.GetFileName(poster), _renderer.RenderPoster(first.Side, second.Side, caption), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            ImageCache.Touch(poster);
        }

        if (!File.Exists(landscape))
        {
            await ImageCache.WriteAsync(_cache.GeneratedDir, Path.GetFileName(landscape), _renderer.RenderLandscape(first.Side, second.Side, caption), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            ImageCache.Touch(landscape);
        }

        return new Dictionary<ArtKind, string>
        {
            [ArtKind.Primary] = poster,
            [ArtKind.Thumb] = landscape,
            [ArtKind.Backdrop] = landscape
        };
    }

    private async Task<(MatchupSide Side, string? League)> ResolveSideAsync(string name, IReadOnlyCollection<string> hints, CancellationToken cancellationToken)
    {
        var team = await _teams.FindAsync(name, hints, cancellationToken).ConfigureAwait(false);
        if (team is null)
        {
            return (new MatchupSide(name, Abbreviate(name), null, null), null);
        }

        string? logo = null;
        if (!string.IsNullOrEmpty(team.LogoUrl))
        {
            logo = await _cache.EnsureLocalAsync(team.LogoUrl, _cache.LogosDir, cancellationToken).ConfigureAwait(false);
        }

        return (new MatchupSide(team.Name, team.Abbreviation, logo, MatchupImageRenderer.PickColor(team.Color, team.AltColor)), team.League);
    }

    private static string Abbreviate(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 1
            ? string.Concat(words.Take(3).Select(w => char.ToUpperInvariant(w[0])))
            : name[..Math.Min(3, name.Length)].ToUpperInvariant();
    }

    private void Apply(LiveTvProgram program, IReadOnlyDictionary<ArtKind, string> files, bool generated)
    {
        foreach (var kind in Kinds)
        {
            if (!files.TryGetValue(kind, out var file))
            {
                continue;
            }

            var type = ToImageType(kind);
            var current = program.GetImages(type).FirstOrDefault()?.Path;
            var ours = ImageCache.IsIn(current, _cache.Root);

            // Never replace provider art; a generated image only fills empty slots or other generated ones.
            if (current is not null && !ours)
            {
                continue;
            }

            if (generated && current is not null && !ImageCache.IsIn(current, _cache.GeneratedDir))
            {
                continue;
            }

            program.SetImage(
                new ItemImageInfo
                {
                    Path = file,
                    Type = type,
                    DateModified = File.GetLastWriteTimeUtc(file)
                },
                0);
        }
    }

    private static ImageType ToImageType(ArtKind kind) => kind switch
    {
        ArtKind.Thumb => ImageType.Thumb,
        ArtKind.Backdrop => ImageType.Backdrop,
        _ => ImageType.Primary
    };

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
