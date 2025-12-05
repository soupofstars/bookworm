using Bookworm.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bookworm.Sync;

public class SuggestedDedupSchedule : BackgroundService
{
    private readonly SuggestedRepository _suggestedRepository;
    private readonly ActivityLogService _activityLog;
    private readonly ILogger<SuggestedDedupSchedule> _logger;
    private static readonly int[] RunMinutes = { 8, 38 };

    public SuggestedDedupSchedule(
        SuggestedRepository suggestedRepository,
        ActivityLogService activityLog,
        ILogger<SuggestedDedupSchedule> logger)
    {
        _suggestedRepository = suggestedRepository;
        _activityLog = activityLog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Run once immediately, then align to the next scheduled slot (8 / 38 past the hour).
            await RunOnceAsync(stoppingToken);

            while (true)
            {
                var next = GetNextRunTime(DateTime.UtcNow);
                var delay = next - DateTime.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                await Task.Delay(delay, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // graceful shutdown
        }
    }

    private static DateTime GetNextRunTime(DateTime from)
    {
        var hour = from.Hour;
        var minute = from.Minute;
        foreach (var targetMinute in RunMinutes)
        {
            if (minute < targetMinute)
            {
                return new DateTime(from.Year, from.Month, from.Day, hour, targetMinute, 0, DateTimeKind.Utc);
            }
        }

        // Next hour at the first run minute
        var nextHour = from.AddHours(1);
        return new DateTime(nextHour.Year, nextHour.Month, nextHour.Day, nextHour.Hour, RunMinutes[0], 0, DateTimeKind.Utc);
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var hidden = await _suggestedRepository.HideDuplicateByHardcoverKeyAsync(cancellationToken);
            if (hidden > 0)
            {
                _logger.LogInformation("Suggested dedup: hid {Hidden} duplicate suggestion(s).", hidden);
                await _activityLog.InfoAsync("Suggested", "Hidden duplicate suggestions (by hardcover id / ISBN / author+title).", new { hidden });
            }
            else
            {
                _logger.LogInformation("Suggested dedup: scan completed; no duplicates to hide.");
                await _activityLog.InfoAsync("Suggested", "Checked for duplicate suggestions; none found.", new { criteria = "hardcover id / ISBN / author+title" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Suggested dedup run failed.");
            await _activityLog.WarnAsync("Suggested", "Suggested dedup run failed.", new { error = ex.Message });
        }
    }
}
