using System.Net.Http.Json;
using System.Text.Json;
using Bookworm.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bookworm.Sync;

public class HardcoverWantSyncSchedule : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly HardcoverWantCacheRepository _cacheRepo;
    private readonly ActivityLogService _activityLog;
    private readonly ILogger<HardcoverWantSyncSchedule> _logger;
    private readonly TimeSpan _interval;
    private readonly bool _enabled;

    private static readonly string[] WantQueries =
    {
        @"
            query MeWantToRead {
              me {
                user_book(
                  where: { status_id: { _eq: 1 } }
                  order_by: { date_added: desc }
                ) {
                  status_id
                  book {
                    id
                    title
                    slug
                    rating
                    ratings_count
                    users_count
                    editions_count
                    default_physical_edition {
                      isbn_13
                      isbn_10
                    }
                    default_ebook_edition {
                      isbn_13
                      isbn_10
                    }
                    cached_contributors
                    image {
                      url
                    }
                  }
                }
              }
            }",
        @"
            query MeWantToRead {
              me {
                user_books(
                  where: { status_id: { _eq: 1 } }
                  order_by: { date_added: desc }
                ) {
                  status_id
                  book {
                    id
                    title
                    slug
                    rating
                    ratings_count
                    users_count
                    editions_count
                    default_physical_edition {
                      isbn_13
                      isbn_10
                    }
                    default_ebook_edition {
                      isbn_13
                      isbn_10
                    }
                    cached_contributors
                    image {
                      url
                    }
                  }
                }
              }
            }"
    };

    public HardcoverWantSyncSchedule(
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        HardcoverWantCacheRepository cacheRepo,
        ActivityLogService activityLog,
        ILogger<HardcoverWantSyncSchedule> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _cacheRepo = cacheRepo;
        _activityLog = activityLog;
        _logger = logger;

        var minutes = config.GetValue<int?>("Hardcover:SyncIntervalMinutes");
        _enabled = minutes.GetValueOrDefault(30) > 0;
        _interval = TimeSpan.FromMinutes(minutes.GetValueOrDefault(30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogInformation("Hardcover want-to-read sync disabled (Hardcover:SyncIntervalMinutes <= 0).");
            await _activityLog.InfoAsync("Hardcover want sync", "Hardcover want-to-read sync is disabled via configuration.");
            return;
        }

        if (!IsHardcoverConfigured())
        {
            _logger.LogInformation("Hardcover want-to-read sync disabled: Hardcover API key not configured.");
            await _activityLog.InfoAsync("Hardcover want sync", "Skipped Hardcover want-to-read sync: API key not configured.");
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
        try
        {
            var existing = await _cacheRepo.GetStatsAsync(cancellationToken);
            var books = await FetchWantToReadAsync(cancellationToken);
            if (books.Count == 0)
            {
                if (existing.Count > 0)
                {
                    _logger.LogWarning("Hardcover want-to-read sync returned 0 books; preserving existing cache of {Count}.", existing.Count);
                    await _activityLog.WarnAsync("Hardcover want sync", "Hardcover want-to-read returned 0 books; keeping existing cache.", new { existing = existing.Count });
                    return;
                }

                _logger.LogInformation("Hardcover want-to-read sync: no books returned and no existing cache.");
            }
            var replace = await _cacheRepo.ReplaceAllAsync(books, cancellationToken);
            _logger.LogInformation("Hardcover want-to-read sync completed. Cached {Cached} book(s). Removed {Removed}.", replace.Cached, replace.Removed);
            await _activityLog.SuccessAsync("Hardcover want sync", "Hardcover want-to-read sync completed.", new { cached = replace.Cached, removed = replace.Removed });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // cancellation requested, exit quietly
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hardcover want-to-read sync failed.");
            await _activityLog.ErrorAsync("Hardcover want sync", "Hardcover want-to-read sync failed.", new { error = ex.Message });
        }
    }

    private async Task<IReadOnlyList<JsonElement>> FetchWantToReadAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient("hardcover");
        HttpResponseMessage? lastResponse = null;
        foreach (var query in WantQueries)
        {
            var response = await client.PostAsJsonAsync("", new { query }, cancellationToken);
            lastResponse = response;
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            using var doc = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken);
            if (doc is null || HasGraphQlErrors(doc.RootElement))
            {
                continue;
            }

            var books = ExtractBooks(doc.RootElement);
            return books;
        }

        var status = lastResponse?.StatusCode;
        _logger.LogWarning("Hardcover want-to-read sync could not fetch data. Last status: {Status}", status);
        return Array.Empty<JsonElement>();
    }

    private static IReadOnlyList<JsonElement> ExtractBooks(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data)) return Array.Empty<JsonElement>();
        if (!data.TryGetProperty("me", out var meEl)) return Array.Empty<JsonElement>();

        var users = meEl.ValueKind switch
        {
            JsonValueKind.Array => meEl.EnumerateArray().ToArray(),
            JsonValueKind.Object => new[] { meEl },
            _ => Array.Empty<JsonElement>()
        };

        var books = new List<JsonElement>();
        foreach (var user in users)
        {
            if (user.ValueKind != JsonValueKind.Object) continue;
            if (user.TryGetProperty("user_books", out var userBooks) && userBooks.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in userBooks.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object &&
                        entry.TryGetProperty("book", out var book) &&
                        book.ValueKind == JsonValueKind.Object)
                    {
                        books.Add(book.Clone());
                    }
                }
            }
            else if (user.TryGetProperty("user_book", out var userBook) && userBook.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in userBook.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object &&
                        entry.TryGetProperty("book", out var book) &&
                        book.ValueKind == JsonValueKind.Object)
                    {
                        books.Add(book.Clone());
                    }
                }
            }
        }

        return books;
    }

    private static bool HasGraphQlErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors)) return false;
        return errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0;
    }

    private bool IsHardcoverConfigured()
    {
        var apiKey = _config["Hardcover:ApiKey"];
        return !string.IsNullOrWhiteSpace(apiKey);
    }
}
