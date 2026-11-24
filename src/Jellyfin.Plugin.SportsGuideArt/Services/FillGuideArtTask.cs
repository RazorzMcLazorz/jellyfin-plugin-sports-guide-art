using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.SportsGuideArt.Services;

/// <summary>Dashboard task that fills missing guide art.</summary>
public sealed class FillGuideArtTask : IScheduledTask
{
    /// <summary>The task key.</summary>
    public const string TaskKey = "SportsGuideArtFill";

    private readonly GuideArtFiller _filler;

    /// <summary>
    /// Initializes a new instance of the <see cref="FillGuideArtTask"/> class.
    /// </summary>
    /// <param name="filler">The filler.</param>
    public FillGuideArtTask(GuideArtFiller filler)
    {
        _filler = filler;
    }

    /// <inheritdoc />
    public string Name => "Fill sports guide art";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "Fills Live TV programs that have no art from matching programs, remembered art, or generated matchup images.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _filler.RunAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(6).Ticks };
    }
}
