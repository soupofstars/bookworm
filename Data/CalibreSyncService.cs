using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Bookworm.Data;

public record CalibreSyncResult(
    bool Success,
    int Count,
    DateTime? Snapshot,
    string? Error,
    IReadOnlyList<int> NewBookIds,
    IReadOnlyList<int> RemovedBookIds);

public class CalibreSyncService
{
    private readonly CalibreRepository _source;
    private readonly CalibreMirrorRepository _mirror;
    private readonly UserSettingsStore _settings;
    private readonly CalibreCoverService _covers;
    private readonly ILogger<CalibreSyncService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CalibreSyncService(
        CalibreRepository source,
        CalibreMirrorRepository mirror,
        UserSettingsStore settings,
        CalibreCoverService covers,
        ILogger<CalibreSyncService> logger)
    {
        _source = source;
        _mirror = mirror;
        _settings = settings;
        _covers = covers;
        _logger = logger;
    }

    public async Task<CalibreSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        if (!_source.IsConfigured)
        {
            return new CalibreSyncResult(false, 0, null, "Calibre path not configured.", Array.Empty<int>(), Array.Empty<int>());
        }

        if (!TryGetLibraryRoot(out var libraryRoot, out var metadataPath, out var error))
        {
            return new CalibreSyncResult(false, 0, null, error ?? "Calibre metadata not found.", Array.Empty<int>(), Array.Empty<int>());
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var books = await _source.GetRecentBooksAsync(int.MaxValue, cancellationToken);
            if (!books.Any())
            {
                var emptySnapshot = DateTime.UtcNow;
                var emptyReplaceResult = await _mirror.ReplaceAllAsync(Array.Empty<CalibreMirrorBook>(), metadataPath, emptySnapshot, cancellationToken);
                return new CalibreSyncResult(true, 0, emptySnapshot, null, emptyReplaceResult.NewIds, emptyReplaceResult.RemovedIds);
            }

            var mirrored = new List<CalibreMirrorBook>(books.Count);
            foreach (var book in books)
            {
                string? coverUrl = null;
                if (book.HasCover && !string.IsNullOrWhiteSpace(book.Path))
                {
                    try
                    {
                        coverUrl = await _covers.EnsureCoverAsync(book.Id, libraryRoot!, book.Path, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to copy cover for Calibre book {BookId}", book.Id);
                    }
                }

                mirrored.Add(new CalibreMirrorBook(
                    book.Id,
                    book.Title,
                    book.AuthorNames,
                    book.Isbn,
                    book.Rating,
                    book.AddedAt,
                    book.PublishedAt,
                    book.Path,
                    book.HasCover,
                    book.Formats,
                    Array.Empty<string>(),
                    book.Publisher,
                    book.Series,
                    book.FileSizeMb,
                    book.Description,
                    coverUrl));
            }

            var snapshot = DateTime.UtcNow;
            var replaceResult = await _mirror.ReplaceAllAsync(mirrored, metadataPath, snapshot, cancellationToken);
            return new CalibreSyncResult(true, mirrored.Count, snapshot, null, replaceResult.NewIds, replaceResult.RemovedIds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Calibre sync failed.");
            return new CalibreSyncResult(false, 0, null, ex.Message, Array.Empty<int>(), Array.Empty<int>());
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetLibraryRoot(out string? libraryRoot, out string? metadataPath, out string? error)
    {
        metadataPath = _settings.CalibreDatabasePath;
        if (string.IsNullOrWhiteSpace(metadataPath))
        {
            libraryRoot = null;
            error = "Calibre metadata path not configured.";
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(metadataPath);
            if (!File.Exists(fullPath))
            {
                libraryRoot = null;
                error = $"Metadata file not found at {fullPath}.";
                return false;
            }

            libraryRoot = Path.GetDirectoryName(fullPath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            libraryRoot = null;
            error = ex.Message;
            return false;
        }
    }
}
