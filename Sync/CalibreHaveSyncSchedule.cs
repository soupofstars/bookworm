using System;
using System.Threading;
using System.Threading.Tasks;
using Bookworm.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bookworm.Sync;

public class CalibreHaveSyncSchedule : BackgroundService
{
    private readonly CalibreSyncService _syncService;
    private readonly UserSettingsStore _settings;
    private readonly ActivityLogService _activityLog;
    private readonly ILogger<CalibreHaveSyncSchedule> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;

    public CalibreHaveSyncSchedule(
        CalibreSyncService syncService,
        UserSettingsStore settings,
        IConfiguration configuration,
        ActivityLogService activityLog,
        ILogger<CalibreHaveSyncSchedule> logger)
    {
        _syncService = syncService;
        _settings = settings;
        _activityLog = activityLog;
        _logger = logger;

        var minutes = configuration.GetValue<int?>("Calibre:SyncIntervalMinutes");
        _enabled = minutes.GetValueOrDefault(30) > 0;
        _interval = TimeSpan.FromMinutes(minutes.GetValueOrDefault(30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Periodic Calibre sync disabled (Calibre:SyncIntervalMinutes <= 0).");
            await _activityLog.InfoAsync("Calibre sync", "Periodic Calibre sync disabled via configuration.");
            return;
        }

        await RunOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.CalibreDatabasePath))
        {
            _logger.LogDebug("Skipping Calibre sync: Calibre database path is not configured.");
            await _activityLog.InfoAsync("Calibre sync", "Skipped Calibre sync: metadata.db path not configured.");
            return;
        }

        try
        {
            var result = await _syncService.SyncAsync(cancellationToken);
            if (!result.Success)
            {
                _logger.LogWarning("Periodic Calibre sync failed: {Error}", result.Error ?? "unknown error");
                await _activityLog.WarnAsync("Calibre sync", "Periodic Calibre sync failed.", new { error = result.Error });
                return;
            }

            _logger.LogInformation(
                "Periodic Calibre sync completed. Total: {Count}, New: {NewCount}, Removed: {RemovedCount}",
                result.Count,
                result.NewBookIds?.Count ?? 0,
                result.RemovedBookIds?.Count ?? 0);
            await _activityLog.SuccessAsync("Calibre sync", "Periodic Calibre sync completed.", new
            {
                total = result.Count,
                newCount = result.NewBookIds?.Count ?? 0,
                removedCount = result.RemovedBookIds?.Count ?? 0
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // cancellation requested, exit quietly
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Periodic Calibre sync threw an exception.");
            await _activityLog.ErrorAsync("Calibre sync", "Periodic Calibre sync threw an exception.", new { error = ex.Message });
        }
    }
}
