using System.Diagnostics;
using Microsoft.Data.Sqlite;
using SQLitePCL;
namespace CodeRim.Core.Services;

/// <summary>Bounds reads of local databases whose schema and values are not trusted.</summary>
internal static class SqliteReadSafety
{
    internal static void ValidateFiles(string path, long maximumBytes = 128 * 1024 * 1024)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked local databases cannot be read safely.");
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            if (File.Exists(file) && ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 || new FileInfo(file).Length > maximumBytes))
                throw new InvalidDataException("The local database exceeds its safety limit.");
    }
    internal static void Configure(SqliteConnection connection, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        // Busy timeout bounds locks, not SQL preparation, VM work or row allocation.
        raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_LENGTH, 1024 * 1024);
        raw.sqlite3_limit(connection.Handle, raw.SQLITE_LIMIT_SQL_LENGTH, 65536);
        var clock = Stopwatch.StartNew(); var callbacks = 0;
        raw.sqlite3_progress_handler(connection.Handle, 1000,
            _ => token.IsCancellationRequested || ++callbacks >= 1000 || clock.Elapsed > TimeSpan.FromSeconds(1) ? 1 : 0, null);
        using var options = connection.CreateCommand();
        options.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA query_only=ON;"; options.ExecuteNonQuery();
    }
    internal static void ValidateStoredTable(SqliteConnection connection, SqliteTransaction transaction, string table, params string[] requiredColumns)
    {
        if (table.Length is 0 or > 64 || table.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Invalid local table name.", nameof(table));
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "PRAGMA main.table_list('" + table + "')";
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(2) != "table" || reader.Read())
                throw new InvalidDataException("The local database must contain an ordinary stored table.");
        }
        using var columns = connection.CreateCommand(); columns.Transaction = transaction;
        columns.CommandText = "PRAGMA main.table_xinfo('" + table + "')";
        using var fields = columns.ExecuteReader(); var missing = requiredColumns.ToHashSet(StringComparer.Ordinal);
        while (fields.Read())
        {
            var name = fields.GetString(1);
            if (!missing.Contains(name)) continue;
            if (fields.GetInt64(6) != 0) throw new InvalidDataException("Local database fields must be stored columns.");
            missing.Remove(name);
        }
        if (missing.Count > 0) throw new InvalidDataException("The local database is missing required fields.");
    }
}
