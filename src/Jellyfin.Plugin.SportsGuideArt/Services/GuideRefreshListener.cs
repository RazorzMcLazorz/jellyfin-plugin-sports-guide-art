using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsGuideArt.Services;

/// <summary>
/// Queues the fill task whenever Jellyfin's "Refresh Guide" task finishes, because a guide
/// refresh removes art the provider did not send.
/// </summary>
public sealed class GuideRefreshListener : IHostedService
{
    private const string RefreshGuideKey = "RefreshGuide";

    private readonly ITaskManager _taskManager;
    private readonly ILogger<GuideRefreshListener> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuideRefreshListener"/> class.
    /// </summary>
    /// <param name="taskManager">Task manager.</param>
    /// <param name="logger">Logger.</param>
    public GuideRefreshListener(ITaskManager taskManager, ILogger<GuideRefreshListener> logger)
    {
        _taskManager = taskManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted += OnTaskCompleted;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted -= OnTaskCompleted;
        return Task.CompletedTask;
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (!string.Equals(e.Result?.Key, RefreshGuideKey, StringComparison.Ordinal)
            || e.Result?.Status != TaskCompletionStatus.Completed
            || Plugin.Instance?.Configuration.RunAfterGuideRefresh == false)
        {
            return;
        }

        _logger.LogInformation("Guide refresh finished; queueing Sports Guide Art fill");
        _taskManager.QueueScheduledTask<FillGuideArtTask>();
    }
}
