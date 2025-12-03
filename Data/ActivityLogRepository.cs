using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Bookworm.Data;

public record ActivityLogEntry(
    long Id,
    DateTime CreatedAt,
    string Source,
    string Level,
    string Message,
    string? DetailsJson);

public class ActivityLogRepository
{
    private const string TableName = "bookworm_activity_log";
    private readonly string _connectionString;

    public ActivityLogRepository(string connectionString)
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
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                source TEXT NOT NULL,
                level TEXT NOT NULL,
                message TEXT NOT NULL,
                details_json TEXT
            );
            """;
        cmd.ExecuteNonQuery();

        using var idx = conn.CreateCommand();
        idx.CommandText = $"CREATE INDEX IF NOT EXISTS idx_{TableName}_created ON {TableName}(datetime(created_at) DESC);";
        idx.ExecuteNonQuery();
    }

    public async Task AppendAsync(string source, string level, string message, string? detailsJson, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(cancellationToken);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                INSERT INTO {TableName} (created_at, source, level, message, details_json)
                VALUES (@created, @source, @level, @message, @details);
                """;
            cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("o"));
            cmd.Parameters.AddWithValue("@source", source);
            cmd.Parameters.AddWithValue("@level", level);
            cmd.Parameters.AddWithValue("@message", message);
            cmd.Parameters.AddWithValue("@details", (object?)detailsJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await TrimAsync(conn, tx, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActivityLogEntry>> GetRecentAsync(int take = 200, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 500);
        var results = new List<ActivityLogEntry>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, created_at, source, level, message, details_json
            FROM {TableName}
            ORDER BY id DESC
            LIMIT @take;
            """;
        cmd.Parameters.AddWithValue("@take", take);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            var createdRaw = reader.GetString(1);
            var createdAt = DateTime.TryParse(createdRaw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : DateTime.UtcNow;
            var source = reader.GetString(2);
            var level = reader.GetString(3);
            var message = reader.GetString(4);
            var details = reader.IsDBNull(5) ? null : reader.GetString(5);
            results.Add(new ActivityLogEntry(id, createdAt, source, level, message, details));
        }

        return results;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName};";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TrimAsync(SqliteConnection conn, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            DELETE FROM {TableName}
            WHERE id NOT IN (
                SELECT id FROM {TableName}
                ORDER BY id DESC
                LIMIT @keep
            );
            """;
        cmd.Parameters.AddWithValue("@keep", 500);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
