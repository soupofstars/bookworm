using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record SuggestedEntry(JsonElement Book, IReadOnlyList<string> BaseGenres, IReadOnlyList<HardcoverListReason> Reasons, string? SourceKey);

public record SuggestedResponse(int Id, JsonElement Book, IReadOnlyList<string> BaseGenres, IReadOnlyList<HardcoverListReason> Reasons, string? SourceKey);

public record SuggestedHideRequest(int[] Ids);

public class SuggestedRepository
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public SuggestedRepository(string connectionString)
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
            CREATE TABLE IF NOT EXISTS suggested (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                hardcover_key TEXT,
                book_json TEXT NOT NULL,
                base_genres_json TEXT,
                reasons_json TEXT,
                hidden INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // If old schema with book_key exists, rebuild the table without that primary key.
        using var info = conn.CreateCommand();
        info.CommandText = "PRAGMA table_info(suggested);";
        using var reader = info.ExecuteReader();
        var hasBookKey = false;
        var hasId = false;
        while (reader.Read())
        {
            var name = reader.GetString(reader.GetOrdinal("name"));
            if (string.Equals(name, "book_key", StringComparison.OrdinalIgnoreCase)) hasBookKey = true;
            if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase)) hasId = true;
        }

        if (hasBookKey || !hasId)
        {
            using var tx = conn.BeginTransaction();
            using (var create = conn.CreateCommand())
            {
                create.Transaction = tx;
                create.CommandText = """
                    CREATE TABLE suggested_new (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        hardcover_key TEXT,
                        book_json TEXT NOT NULL,
                        base_genres_json TEXT,
                        reasons_json TEXT,
                        hidden INTEGER NOT NULL DEFAULT 0,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );
                    """;
                create.ExecuteNonQuery();
            }

            using (var copy = conn.CreateCommand())
            {
                copy.Transaction = tx;
                copy.CommandText = """
                    INSERT INTO suggested_new (hardcover_key, book_json, base_genres_json, reasons_json, hidden, created_at, updated_at)
                    SELECT
                        NULL,
                        book_json,
                        base_genres_json,
                        reasons_json,
                        0,
                        COALESCE(created_at, CURRENT_TIMESTAMP),
                        COALESCE(updated_at, CURRENT_TIMESTAMP)
                    FROM suggested;
                    """;
                try { copy.ExecuteNonQuery(); } catch { /* best effort */ }
            }

            using (var drop = conn.CreateCommand())
            {
                drop.Transaction = tx;
                drop.CommandText = "DROP TABLE suggested;";
                drop.ExecuteNonQuery();
            }

            using (var rename = conn.CreateCommand())
            {
                rename.Transaction = tx;
                rename.CommandText = "ALTER TABLE suggested_new RENAME TO suggested;";
                rename.ExecuteNonQuery();
            }

            tx.Commit();
        }

        // Add hidden column if missing
        using var hiddenCheck = conn.CreateCommand();
        hiddenCheck.CommandText = "PRAGMA table_info(suggested);";
        using var hiddenReader = hiddenCheck.ExecuteReader();
        var hasHidden = false;
        while (hiddenReader.Read())
        {
            var name = hiddenReader.GetString(hiddenReader.GetOrdinal("name"));
            if (string.Equals(name, "hidden", StringComparison.OrdinalIgnoreCase))
            {
                hasHidden = true;
                break;
            }
        }

        if (!hasHidden)
        {
            using var addHidden = conn.CreateCommand();
            addHidden.CommandText = "ALTER TABLE suggested ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0;";
            addHidden.ExecuteNonQuery();
        }
    }

    public async Task UpsertMissingAsync(IEnumerable<SuggestedEntry> entries, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        // Load existing keys/fingerprints to avoid duplicates.
        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingFingerprints = new HashSet<string>(StringComparer.Ordinal);
        await using (var loadCmd = conn.CreateCommand())
        {
            loadCmd.Transaction = tx;
            loadCmd.CommandText = "SELECT hardcover_key, book_json FROM suggested;";
            await using var reader = await loadCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0))
                {
                    var key = reader.GetString(0);
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        existingKeys.Add(key.Trim());
                    }
                }

                if (!reader.IsDBNull(1))
                {
                    var fingerprint = reader.GetString(1);
                    if (!string.IsNullOrWhiteSpace(fingerprint))
                    {
                        existingFingerprints.Add(fingerprint);
                    }
                }
            }
        }

        var insertCmd = conn.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = """
            INSERT INTO suggested (hardcover_key, book_json, base_genres_json, reasons_json, created_at, updated_at)
            VALUES (@hkey, @book, @genres, @reasons, @created, @updated);
            """;
        var pHKey = insertCmd.Parameters.Add("@hkey", SqliteType.Text);
        var pBook = insertCmd.Parameters.Add("@book", SqliteType.Text);
        var pGenres = insertCmd.Parameters.Add("@genres", SqliteType.Text);
        var pReasons = insertCmd.Parameters.Add("@reasons", SqliteType.Text);
        var pCreated = insertCmd.Parameters.Add("@created", SqliteType.Text);
        var pUpdated = insertCmd.Parameters.Add("@updated", SqliteType.Text);

        foreach (var entry in entries)
        {
            var key = NormalizeKey(entry.SourceKey);
            var bookJson = JsonSerializer.Serialize(entry.Book, _jsonOptions);

            if (!string.IsNullOrWhiteSpace(key) && existingKeys.Contains(key))
            {
                continue;
            }

            if (existingFingerprints.Contains(bookJson))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(key))
            {
                existingKeys.Add(key);
            }
            existingFingerprints.Add(bookJson);

            pHKey.Value = string.IsNullOrWhiteSpace(key) ? (object)DBNull.Value : key;
            pBook.Value = bookJson;
            pGenres.Value = entry.BaseGenres is { Count: > 0 }
                ? JsonSerializer.Serialize(entry.BaseGenres, _jsonOptions)
                : (object)DBNull.Value;
            pReasons.Value = entry.Reasons is { Count: > 0 }
                ? JsonSerializer.Serialize(entry.Reasons, _jsonOptions)
                : (object)DBNull.Value;
            var now = DateTime.UtcNow.ToString("o");
            pCreated.Value = now;
            pUpdated.Value = now;
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SuggestedResponse>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<SuggestedResponse>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, hardcover_key, book_json, base_genres_json, reasons_json
            FROM suggested
            WHERE hidden = 0
            ORDER BY updated_at DESC, created_at DESC;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(0);
            var sourceKey = reader.IsDBNull(1) ? null : reader.GetString(1);
            var bookJson = reader.GetString(2);
            var baseGenresJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            var reasonsJson = reader.IsDBNull(4) ? null : reader.GetString(4);

            using var bookDoc = JsonDocument.Parse(bookJson);
            var book = bookDoc.RootElement.Clone();

            IReadOnlyList<string> baseGenres = Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(baseGenresJson))
            {
                try
                {
                    baseGenres = JsonSerializer.Deserialize<string[]>(baseGenresJson, _jsonOptions)
                                  ?? Array.Empty<string>();
                }
                catch
                {
                    baseGenres = Array.Empty<string>();
                }
            }

            IReadOnlyList<HardcoverListReason> reasons = Array.Empty<HardcoverListReason>();
            if (!string.IsNullOrWhiteSpace(reasonsJson))
            {
                try
                {
                    reasons = JsonSerializer.Deserialize<HardcoverListReason[]>(reasonsJson, _jsonOptions)
                              ?? Array.Empty<HardcoverListReason>();
                }
                catch
                {
                    reasons = Array.Empty<HardcoverListReason>();
                }
            }

            results.Add(new SuggestedResponse(id, book, baseGenres, reasons, sourceKey));
        }

        return results;
    }

    private static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var trimmed = key.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public async Task HideByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        if (ids is null || ids.Count == 0) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE suggested SET hidden = 1, updated_at = @updated WHERE id IN (" + string.Join(",", ids.Select((_, i) => $"@id{i}")) + ");";
        var now = DateTime.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("@updated", now);
        var idx = 0;
        foreach (var id in ids)
        {
            cmd.Parameters.AddWithValue($"@id{idx}", id);
            idx++;
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task DeleteByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default)
    {
        var toDelete = ids?.Distinct().ToList();
        if (toDelete is null || toDelete.Count == 0) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM suggested WHERE id IN (" + string.Join(",", toDelete.Select((_, i) => $"@id{i}")) + ");";
        for (var i = 0; i < toDelete.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@id{i}", toDelete[i]);
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }
}
