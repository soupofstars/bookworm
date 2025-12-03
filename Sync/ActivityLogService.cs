using System.Text.Json;
using Bookworm.Data;
using Microsoft.Extensions.Logging;

namespace Bookworm.Sync;

public class ActivityLogService
{
    private readonly ActivityLogRepository _repository;
    private readonly ILogger<ActivityLogService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public ActivityLogService(ActivityLogRepository repository, ILogger<ActivityLogService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public Task InfoAsync(string source, string message, object? details = null, CancellationToken cancellationToken = default)
        => AppendAsync("info", source, message, details, cancellationToken);

    public Task SuccessAsync(string source, string message, object? details = null, CancellationToken cancellationToken = default)
        => AppendAsync("success", source, message, details, cancellationToken);

    public Task WarnAsync(string source, string message, object? details = null, CancellationToken cancellationToken = default)
        => AppendAsync("warning", source, message, details, cancellationToken);

    public Task ErrorAsync(string source, string message, object? details = null, CancellationToken cancellationToken = default)
        => AppendAsync("error", source, message, details, cancellationToken);

    private async Task AppendAsync(string level, string source, string message, object? details, CancellationToken cancellationToken)
    {
        var normalizedLevel = string.IsNullOrWhiteSpace(level) ? "info" : level.Trim().ToLowerInvariant();
        var normalizedSource = string.IsNullOrWhiteSpace(source) ? "system" : source.Trim();
        var safeMessage = string.IsNullOrWhiteSpace(message) ? normalizedLevel : message.Trim();

        string? detailsJson = null;
        if (details is not null)
        {
            try
            {
                detailsJson = JsonSerializer.Serialize(details, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to serialize details for activity log {Source}", normalizedSource);
            }
        }

        try
        {
            await _repository.AppendAsync(normalizedSource, normalizedLevel, safeMessage, detailsJson, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist activity log entry for {Source}", normalizedSource);
        }
    }
}
