using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record HardcoverListCacheEntry(
    int CalibreId,
    string CalibreTitle,
    string? HardcoverId,
    string? HardcoverTitle,
    int ListCount,
    int RecommendationCount,
    DateTime LastCheckedUtc,
    string? Status,
    string? BaseGenresJson,
    string? ListsJson,
    string? RecommendationsJson);

public record HardcoverListCacheSyncResult(int Added, int Updated, int Deleted);

public class HardcoverListCacheRepository
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public HardcoverListCacheRepository(string connectionString)
    {
        _connectionString = NormalizeConnectionString(connectionString);
        EnsureSchema();
    }

    private static string NormalizeConnectionString(string raw)
    {
        var builder = new SqliteConnectionStringBuilder(raw);
        var dataSource = builder.DataSource;
        if (!Path.IsPathRooted(dataSource))
        {
            dataSource = Path.Combine(AppContext.BaseDirectory, dataSource);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dataSource)!);
        builder.DataSource = dataSource;
        return builder.ToString();
    }

    private void EnsureSchema()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS hardcover_list_cache (
                calibre_id INTEGER PRIMARY KEY,
                calibre_title TEXT NOT NULL,
                hardcover_id TEXT,
                hardcover_title TEXT,
                list_count INTEGER NOT NULL DEFAULT 0,
                recommendation_count INTEGER NOT NULL DEFAULT 0,
                last_checked_utc TEXT NOT NULL,
                status TEXT,
                base_genres_json TEXT,
                lists_json TEXT,
                recommendations_json TEXT
            );
            """;
        cmd.ExecuteNonQuery();
        EnsureColumn(conn, "hardcover_list_cache", "base_genres_json", "TEXT");
        RemoveLegacyColumn(conn, "hardcover_genres_json");
    }

    public async Task UpsertAsync(HardcoverListCacheEntry entry, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO hardcover_list_cache (
                calibre_id, calibre_title, hardcover_id, hardcover_title,
                list_count, recommendation_count, last_checked_utc, status,
                base_genres_json, lists_json, recommendations_json
            ) VALUES (
                @cid, @ctitle, @hid, @htitle,
                @lcount, @rcount, @checked, @status,
                @baseGenres, @lists, @recs
            )
            ON CONFLICT(calibre_id) DO UPDATE SET
                calibre_title = excluded.calibre_title,
                hardcover_id = excluded.hardcover_id,
                hardcover_title = excluded.hardcover_title,
                list_count = excluded.list_count,
                recommendation_count = excluded.recommendation_count,
                last_checked_utc = excluded.last_checked_utc,
                status = excluded.status,
                base_genres_json = excluded.base_genres_json,
                lists_json = excluded.lists_json,
                recommendations_json = excluded.recommendations_json;
            """;

        cmd.Parameters.AddWithValue("@cid", entry.CalibreId);
        cmd.Parameters.AddWithValue("@ctitle", entry.CalibreTitle ?? string.Empty);
        cmd.Parameters.AddWithValue("@hid", (object?)entry.HardcoverId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@htitle", (object?)entry.HardcoverTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lcount", entry.ListCount);
        cmd.Parameters.AddWithValue("@rcount", entry.RecommendationCount);
        cmd.Parameters.AddWithValue("@checked", entry.LastCheckedUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@status", (object?)entry.Status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@baseGenres", (object?)entry.BaseGenresJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lists", (object?)entry.ListsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@recs", (object?)entry.RecommendationsJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HardcoverListCacheEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<HardcoverListCacheEntry>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT calibre_id, calibre_title, hardcover_id, hardcover_title,
                   list_count, recommendation_count, last_checked_utc, status,
                   base_genres_json, lists_json, recommendations_json
            FROM hardcover_list_cache
            ORDER BY last_checked_utc DESC;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new HardcoverListCacheEntry(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                DateTime.TryParse(reader.GetString(6), out var dt) ? dt : DateTime.MinValue,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)
            ));
        }

        return results;
    }

    public async Task<HardcoverListCacheEntry?> GetAsync(int calibreId, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT calibre_id, calibre_title, hardcover_id, hardcover_title,
                   list_count, recommendation_count, last_checked_utc, status,
                   base_genres_json, lists_json, recommendations_json
            FROM hardcover_list_cache
            WHERE calibre_id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", calibreId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return new HardcoverListCacheEntry(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                DateTime.TryParse(reader.GetString(6), out var dt) ? dt : DateTime.MinValue,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)
            );
        }

        return null;
    }

    public async Task<HardcoverListCacheSyncResult> SyncWithCalibreAsync(IEnumerable<CalibreMirrorBook> calibreBooks, CancellationToken cancellationToken = default)
    {
        var list = calibreBooks?.ToList() ?? new List<CalibreMirrorBook>();
        var keepIds = new HashSet<int>(list.Select(b => b.Id));
        var existing = await GetAllAsync(cancellationToken);
        var existingMap = existing.ToDictionary(e => e.CalibreId);

        var deleted = await DeleteMissingAsync(keepIds, cancellationToken);
        var added = 0;
        var updated = 0;

        foreach (var book in list)
        {
            if (existingMap.TryGetValue(book.Id, out var current))
            {
                if (!string.Equals(current.CalibreTitle, book.Title, StringComparison.Ordinal))
                {
                    current = current with { CalibreTitle = book.Title };
                    updated++;
                    await UpsertAsync(current, cancellationToken);
                }
                continue;
            }

            var entry = new HardcoverListCacheEntry(
                book.Id,
                book.Title,
                null,
                null,
                0,
                0,
                DateTime.UtcNow,
                "pending",
                null,
                null,
                null);
            added++;
            await UpsertAsync(entry, cancellationToken);
        }

        return new HardcoverListCacheSyncResult(added, updated, deleted);
    }

    private async Task<int> DeleteMissingAsync(IReadOnlyCollection<int> keepIds, CancellationToken cancellationToken)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();

        if (keepIds.Count == 0)
        {
            cmd.CommandText = "DELETE FROM hardcover_list_cache;";
            return await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        var parameters = keepIds.Select((id, idx) => new { id, name = $"@id{idx}" }).ToList();
        var placeholders = string.Join(",", parameters.Select(p => p.name));
        cmd.CommandText = $"DELETE FROM hardcover_list_cache WHERE calibre_id NOT IN ({placeholders});";
        foreach (var p in parameters)
        {
            cmd.Parameters.AddWithValue(p.name, p.id);
        }

        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        if (ColumnExists(connection, table, column))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type};";
        alter.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            var existing = reader.GetString(reader.GetOrdinal("name"));
            if (string.Equals(existing, column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveLegacyColumn(SqliteConnection connection, string legacyColumn)
    {
        if (!ColumnExists(connection, "hardcover_list_cache", legacyColumn))
        {
            return;
        }

        using var tx = connection.BeginTransaction();

        using (var create = connection.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                CREATE TABLE hardcover_list_cache_new (
                    calibre_id INTEGER PRIMARY KEY,
                    calibre_title TEXT NOT NULL,
                    hardcover_id TEXT,
                    hardcover_title TEXT,
                    list_count INTEGER NOT NULL DEFAULT 0,
                    recommendation_count INTEGER NOT NULL DEFAULT 0,
                    last_checked_utc TEXT NOT NULL,
                    status TEXT,
                    base_genres_json TEXT,
                    lists_json TEXT,
                    recommendations_json TEXT
                );
                """;
            create.ExecuteNonQuery();
        }

        using (var copy = connection.CreateCommand())
        {
            copy.Transaction = tx;
            copy.CommandText = """
                INSERT INTO hardcover_list_cache_new (
                    calibre_id, calibre_title, hardcover_id, hardcover_title,
                    list_count, recommendation_count, last_checked_utc, status,
                    base_genres_json, lists_json, recommendations_json
                )
                SELECT
                    calibre_id, calibre_title, hardcover_id, hardcover_title,
                    list_count, recommendation_count, last_checked_utc, status,
                    base_genres_json, lists_json, recommendations_json
                FROM hardcover_list_cache;
                """;
            copy.ExecuteNonQuery();
        }

        using (var drop = connection.CreateCommand())
        {
            drop.Transaction = tx;
            drop.CommandText = "DROP TABLE hardcover_list_cache;";
            drop.ExecuteNonQuery();
        }

        using (var rename = connection.CreateCommand())
        {
            rename.Transaction = tx;
            rename.CommandText = "ALTER TABLE hardcover_list_cache_new RENAME TO hardcover_list_cache;";
            rename.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public string Serialize<T>(T value) => JsonSerializer.Serialize(value, _jsonOptions);
}
