using Bookworm.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bookworm.Sync;

public class HardcoverBookshelfSyncSchedule : BackgroundService
{
    private readonly HardcoverBookshelfResolver _resolver;
    private readonly IConfiguration _config;
    private readonly ActivityLogService _activityLog;
    private readonly ILogger<HardcoverBookshelfSyncSchedule> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;

    public HardcoverBookshelfSyncSchedule(
        HardcoverBookshelfResolver resolver,
        IConfiguration config,
        ActivityLogService activityLog,
        ILogger<HardcoverBookshelfSyncSchedule> logger)
    {
        _resolver = resolver;
        _config = config;
        _activityLog = activityLog;
        _logger = logger;

        var minutes = config.GetValue<int?>("Hardcover:BookshelfSyncMinutes");
        _enabled = minutes.GetValueOrDefault(30) > 0;
        _interval = TimeSpan.FromMinutes(minutes.GetValueOrDefault(30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Hardcover bookshelf sync disabled (Hardcover:BookshelfSyncMinutes <= 0).");
            await _activityLog.InfoAsync("Hardcover bookshelf", "Hardcover bookshelf sync is disabled via configuration.");
            return;
        }

        await ResolveOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ResolveOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }

    private async Task ResolveOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _resolver.ResolveAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hardcover bookshelf sync failed.");
            await _activityLog.ErrorAsync("Hardcover bookshelf", "Hardcover bookshelf sync failed.", new { error = ex.Message });
        }
    }
}
