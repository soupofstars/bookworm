using Bookworm.Data;

namespace Bookworm.Sync;

public record CalibreSyncStateSnapshot(
    DateTime? LastSnapshot,
    string? SourcePath,
    string StoragePath,
    int BookCount,
    DateTime? BookshelfLastUpdated);

public class CalibreSyncStateService
{
    private readonly CalibreMirrorRepository _mirror;
    private readonly BookshelfRepository _bookshelf;

    public CalibreSyncStateService(
        CalibreMirrorRepository mirror,
        BookshelfRepository bookshelf)
    {
        _mirror = mirror;
        _bookshelf = bookshelf;
    }

    public async Task<CalibreSyncStateSnapshot> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var mirrorState = await _mirror.GetSyncStateAsync(cancellationToken);
        var bookshelfStats = await _bookshelf.GetStatsAsync(cancellationToken);

        return new CalibreSyncStateSnapshot(
            mirrorState.LastSnapshot,
            mirrorState.CalibrePath,
            _mirror.DatabasePath,
            bookshelfStats.Count,
            bookshelfStats.LastUpdatedAt);
    }
}
