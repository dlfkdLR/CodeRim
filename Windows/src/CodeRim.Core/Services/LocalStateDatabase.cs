using Microsoft.Data.Sqlite;
namespace CodeRim.Core.Services;

/// <summary>Reads explicitly selected editor state without loading unbounded values or executing schema functions.</summary>
public static class LocalStateDatabase
{
    private const int MaximumValueBytes = 262144;
    public static IReadOnlyDictionary<string, byte[]> Read(string path, params string[] keys)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || keys.Length is < 1 or > 16)
            throw new InvalidDataException("Select a local editor state database.");
        SqliteReadSafety.ValidateFiles(path);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
            Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        SqliteReadSafety.Configure(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        SqliteReadSafety.ValidateStoredTable(connection, transaction, "ItemTable", "key", "value");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT length(CAST(value AS BLOB)), CASE WHEN length(CAST(value AS BLOB)) <= $limit THEN CAST(value AS BLOB) ELSE NULL END FROM main.ItemTable WHERE key=$key COLLATE BINARY LIMIT 1";
            command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$limit", MaximumValueBytes);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0)) continue;
            if (reader.GetInt64(0) > MaximumValueBytes) throw new InvalidDataException("Editor state value exceeds its safety limit.");
            if (!reader.IsDBNull(1)) result[key] = (byte[])reader.GetValue(1);
        }
        transaction.Commit(); return result;
    }
}
