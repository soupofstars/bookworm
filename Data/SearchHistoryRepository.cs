using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record SearchHistoryEntry(string Query, int ResultCount, DateTimeOffset LastSearched);

public class SearchHistoryRepository
{
    private const string BookTable = "book_search_history";
    private const string AuthorTable = "author_search_history";
    private const string IsbnTable = "isbn_search_history";
    private readonly string? _connectionString;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_connectionString);

    public SearchHistoryRepository(string? connectionString)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? null
            : NormalizeConnectionString(connectionString);

        if (_connectionString is null) return;

        EnsureDatabase();
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

    private void EnsureDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        EnsureTable(conn, BookTable);
        EnsureTable(conn, AuthorTable);
        EnsureTable(conn, IsbnTable);
    }

    private static void EnsureTable(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {tableName} (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                query TEXT NOT NULL UNIQUE,
                result_count INTEGER NOT NULL DEFAULT 0,
                last_searched TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Task LogAsync(string query, int resultCount, CancellationToken cancellationToken = default)
        => LogBookAsync(query, resultCount, cancellationToken);

    public Task LogBookAsync(string query, int resultCount, CancellationToken cancellationToken = default)
        => LogToTableAsync(BookTable, query, resultCount, cancellationToken);

    public Task LogAuthorAsync(string query, int resultCount, CancellationToken cancellationToken = default)
        => LogToTableAsync(AuthorTable, query, resultCount, cancellationToken);

    public Task LogIsbnAsync(string query, int resultCount, CancellationToken cancellationToken = default)
        => LogToTableAsync(IsbnTable, query, resultCount, cancellationToken);

    public Task<IReadOnlyList<SearchHistoryEntry>> GetRecentAuthorsAsync(int take = 10, CancellationToken cancellationToken = default)
        => GetRecentFromTableAsync(AuthorTable, take, cancellationToken);

    public Task<IReadOnlyList<SearchHistoryEntry>> GetRecentIsbnAsync(int take = 10, CancellationToken cancellationToken = default)
        => GetRecentFromTableAsync(IsbnTable, take, cancellationToken);

    public Task DeleteAuthorAsync(string query, CancellationToken cancellationToken = default)
        => DeleteFromTableAsync(AuthorTable, query, cancellationToken);

    public Task DeleteIsbnAsync(string query, CancellationToken cancellationToken = default)
        => DeleteFromTableAsync(IsbnTable, query, cancellationToken);

    private async Task LogToTableAsync(string tableName, string query, int resultCount, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query)) return;
        var trimmed = query.Trim();
        if (!IsConfigured) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {tableName} (query, result_count, last_searched)
            VALUES (@query, @count, CURRENT_TIMESTAMP)
            ON CONFLICT(query) DO UPDATE SET
                result_count = excluded.result_count,
                last_searched = CURRENT_TIMESTAMP;
            """;
        cmd.Parameters.AddWithValue("@query", trimmed);
        cmd.Parameters.AddWithValue("@count", resultCount);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SearchHistoryEntry>> GetRecentAsync(int take = 10, CancellationToken cancellationToken = default)
    {
        return await GetRecentFromTableAsync(BookTable, take, cancellationToken);
    }

    private async Task<IReadOnlyList<SearchHistoryEntry>> GetRecentFromTableAsync(string tableName, int take, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 50);

        if (!IsConfigured) return Array.Empty<SearchHistoryEntry>();

        var results = new List<SearchHistoryEntry>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT query, result_count, last_searched FROM {tableName} ORDER BY datetime(last_searched) DESC LIMIT @take";
        cmd.Parameters.AddWithValue("@take", take);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var query = reader.GetString(0);
            var count = reader.GetInt32(1);
            var lastSearchedRaw = reader.GetString(2);
            var lastSearched = DateTimeOffset.TryParse(lastSearchedRaw, out var dto)
                ? dto
                : DateTimeOffset.UtcNow;
            results.Add(new SearchHistoryEntry(query, count, lastSearched));
        }

        return results;
    }

    public async Task DeleteAsync(string query, CancellationToken cancellationToken = default)
    {
        await DeleteFromTableAsync(BookTable, query, cancellationToken);
    }

    private async Task DeleteFromTableAsync(string tableName, string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || !IsConfigured) return;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {tableName} WHERE query = @query";
        cmd.Parameters.AddWithValue("@query", query.Trim());
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
