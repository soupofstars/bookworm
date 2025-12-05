using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record SuggestedEntry(JsonElement Book, IReadOnlyList<string> BaseGenres, IReadOnlyList<HardcoverListReason> Reasons, string? SourceKey);

public record SuggestedResponse(int Id, JsonElement Book, IReadOnlyList<string> BaseGenres, IReadOnlyList<HardcoverListReason> Reasons, string? SourceKey);

public record SuggestedHideRequest(int[] Ids, int Hidden = 1);

public class SuggestedRepository
{
    private const string TableName = "bookworm_suggested_books";
    private const string LegacyTableName = "suggested";
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
        MigrateLegacyTable(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {TableName} (
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
        info.CommandText = $"PRAGMA table_info({TableName});";
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
                create.CommandText = $"""
                    CREATE TABLE {TableName}_new (
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
                copy.CommandText = $"""
                    INSERT INTO {TableName}_new (hardcover_key, book_json, base_genres_json, reasons_json, hidden, created_at, updated_at)
                    SELECT
                        NULL,
                        book_json,
                        base_genres_json,
                        reasons_json,
                        0,
                        COALESCE(created_at, CURRENT_TIMESTAMP),
                        COALESCE(updated_at, CURRENT_TIMESTAMP)
                    FROM {TableName};
                    """;
                try { copy.ExecuteNonQuery(); } catch { /* best effort */ }
            }

            using (var drop = conn.CreateCommand())
            {
                drop.Transaction = tx;
                drop.CommandText = $"DROP TABLE {TableName};";
                drop.ExecuteNonQuery();
            }

            using (var rename = conn.CreateCommand())
            {
                rename.Transaction = tx;
                rename.CommandText = $"ALTER TABLE {TableName}_new RENAME TO {TableName};";
                rename.ExecuteNonQuery();
            }

            tx.Commit();
        }

        // Add hidden column if missing
        using var hiddenCheck = conn.CreateCommand();
        hiddenCheck.CommandText = $"PRAGMA table_info({TableName});";
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
            addHidden.CommandText = $"ALTER TABLE {TableName} ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0;";
            addHidden.ExecuteNonQuery();
        }
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = @name LIMIT 1;";
        cmd.Parameters.AddWithValue("@name", tableName);
        using var reader = cmd.ExecuteReader();
        return reader.Read();
    }

    private static void MigrateLegacyTable(SqliteConnection conn)
    {
        var legacyExists = TableExists(conn, LegacyTableName);
        var targetExists = TableExists(conn, TableName);
        if (legacyExists && !targetExists)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {LegacyTableName} RENAME TO {TableName};";
            cmd.ExecuteNonQuery();
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
            loadCmd.CommandText = $"SELECT hardcover_key, book_json FROM {TableName};";
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
        insertCmd.CommandText = $"""
            INSERT INTO {TableName} (hardcover_key, book_json, base_genres_json, reasons_json, created_at, updated_at)
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
        => await GetByHiddenInternalAsync(0, cancellationToken);

    public async Task<IReadOnlyList<SuggestedResponse>> GetByHiddenAsync(int hiddenValue, CancellationToken cancellationToken = default)
        => await GetByHiddenInternalAsync(hiddenValue, cancellationToken);

    private async Task<IReadOnlyList<SuggestedResponse>> GetByHiddenInternalAsync(int hiddenValue, CancellationToken cancellationToken)
    {
        var resolvedHidden = hiddenValue < 0 ? 0 : hiddenValue;
        var results = new List<SuggestedResponse>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, hardcover_key, book_json, base_genres_json, reasons_json
            FROM {TableName}
            WHERE hidden = @hidden
            ORDER BY updated_at DESC, created_at DESC;
            """;
        cmd.Parameters.AddWithValue("@hidden", resolvedHidden);
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

    private static string? NormalizeIsbn(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Where(ch => char.IsDigit(ch) || ch == 'X' || ch == 'x').ToArray()).ToUpperInvariant();
        return cleaned.Length >= 10 ? cleaned : null;
    }

    private static string? ExtractFirstString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var child)) return null;
        if (child.ValueKind == JsonValueKind.String)
        {
            return child.GetString();
        }

        if (child.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in child.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var str = item.GetString();
                    if (!string.IsNullOrWhiteSpace(str)) return str;
                }
            }
        }

        return null;
    }

    private static string? ExtractEditionIsbn(JsonElement root, string editionProperty)
    {
        if (!root.TryGetProperty(editionProperty, out var edition) || edition.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var isbn13 = ExtractFirstString(edition, "isbn_13");
        var isbn10 = ExtractFirstString(edition, "isbn_10");
        return NormalizeIsbn(isbn13) ?? NormalizeIsbn(isbn10);
    }

    private static string? ExtractAuthorFromContributors(JsonElement root)
    {
        if (root.TryGetProperty("cached_contributors", out var contribs) && contribs.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contribs.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("author", out var authorObj) && authorObj.ValueKind == JsonValueKind.Object)
                {
                    var name = ExtractFirstString(authorObj, "name");
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
        }

        if (root.TryGetProperty("contributions", out var contributions) && contributions.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in contributions.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (item.TryGetProperty("author", out var authorObj) && authorObj.ValueKind == JsonValueKind.Object)
                {
                    var name = ExtractFirstString(authorObj, "name");
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
        }

        return null;
    }

    private static string? ExtractPrimaryAuthor(JsonElement book)
    {
        string? TryArray(string property)
        {
            if (!book.TryGetProperty(property, out var child) || child.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in child.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var str = item.GetString();
                    if (!string.IsNullOrWhiteSpace(str)) return str;
                }
            }
            return null;
        }

        var first = TryArray("author_names") ?? TryArray("authors");
        if (!string.IsNullOrWhiteSpace(first)) return first;

        if (book.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.String)
        {
            var str = author.GetString();
            if (!string.IsNullOrWhiteSpace(str)) return str;
        }

        return ExtractAuthorFromContributors(book);
    }

    public async Task HideByIdsAsync(IReadOnlyCollection<int> ids, int hiddenValue = 1, CancellationToken cancellationToken = default)
    {
        if (ids is null || ids.Count == 0) return;

        var resolvedHidden = hiddenValue <= 0 ? 1 : hiddenValue;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {TableName} SET hidden = @hidden, updated_at = @updated WHERE id IN (" + string.Join(",", ids.Select((_, i) => $"@id{i}")) + ");";
        var now = DateTime.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("@hidden", resolvedHidden);
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

    public async Task<int> HideDuplicateByHardcoverKeyAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Fetch non-hidden entries, including book payload for additional dedup signals.
        var entries = new List<(int Id, string HardcoverKey, string? BookJson, DateTime UpdatedAt)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT id, hardcover_key, book_json, updated_at
                FROM {TableName}
                WHERE hidden = 0
                ORDER BY datetime(updated_at) DESC, id DESC;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt32(0);
                var key = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var bookJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                var updatedRaw = reader.IsDBNull(3) ? null : reader.GetString(3);
                var updatedAt = DateTime.TryParse(updatedRaw, out var parsed) ? parsed : DateTime.MinValue;
                entries.Add((id, key, bookJson, updatedAt));
            }
        }

        var toHide = new List<int>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIsbn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTitleAuthor = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var hardcoverKey = entry.HardcoverKey?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(hardcoverKey))
            {
                if (!seen.Add(hardcoverKey))
                {
                    toHide.Add(entry.Id);
                    continue;
                }
            }

            string? isbn = null;
            string? title = null;
            string? author = null;

            if (!string.IsNullOrWhiteSpace(entry.BookJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(entry.BookJson);
                    var root = doc.RootElement;
                    isbn = NormalizeIsbn(ExtractFirstString(root, "isbn13") ?? ExtractFirstString(root, "isbn_13"))
                          ?? NormalizeIsbn(ExtractFirstString(root, "isbn10") ?? ExtractFirstString(root, "isbn_10"))
                          ?? ExtractEditionIsbn(root, "default_physical_edition")
                          ?? ExtractEditionIsbn(root, "default_ebook_edition");
                    title = NormalizeKey(ExtractFirstString(root, "title"));
                    author = NormalizeKey(ExtractPrimaryAuthor(root));
                }
                catch
                {
                    // ignore parse errors; fallback to hardcover key only
                }
            }

            if (!string.IsNullOrWhiteSpace(isbn))
            {
                if (!seenIsbn.Add(isbn))
                {
                    toHide.Add(entry.Id);
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(author))
            {
                var combo = $"{title}||{author}";
                if (!seenTitleAuthor.Add(combo))
                {
                    toHide.Add(entry.Id);
                    continue;
                }
            }
        }

        if (toHide.Count == 0) return 0;

        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);
        var hideCmd = conn.CreateCommand();
        hideCmd.Transaction = tx;
        hideCmd.CommandText = $"UPDATE {TableName} SET hidden = 1, updated_at = @updated WHERE id IN (" + string.Join(",", toHide.Select((_, i) => $"@id{i}")) + ");";
        var now = DateTime.UtcNow.ToString("o");
        hideCmd.Parameters.AddWithValue("@updated", now);
        for (var i = 0; i < toHide.Count; i++)
        {
            hideCmd.Parameters.AddWithValue($"@id{i}", toHide[i]);
        }
        await hideCmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return toHide.Count;
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
        cmd.CommandText = $"DELETE FROM {TableName} WHERE id IN (" + string.Join(",", toDelete.Select((_, i) => $"@id{i}")) + ");";
        for (var i = 0; i < toDelete.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@id{i}", toDelete[i]);
        }

        await cmd.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }
}
