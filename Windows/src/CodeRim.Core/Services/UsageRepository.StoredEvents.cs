#if CODERIM_RUST_ACCOUNTING
using CodeRim.Core.Domain;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CodeRim.Core.Services;

public sealed partial class UsageRepository
{
    private static UsageEvent ReadStoredEvent(SqliteDataReader reader, string provider)
    {
        // Microsoft.Data.Sqlite has already stepped this exact statement on its connection.
        // Use the same managed SQLitePCL provider for typed reads, so no native SQLite
        // handle crosses a library boundary. Keep its original NULL diagnostics on legacy rows.
        var statement = reader.Handle!;
        for (int column = 0; column < 10; column++)
            if (column != 5 && raw.sqlite3_column_type(statement, column) == raw.SQLITE_NULL)
                return ReadEvent(reader, provider);
        var time = raw.sqlite3_column_int64(statement, 1);
        var cached = raw.sqlite3_column_int64(statement, 3);
        var hasWritten = raw.sqlite3_column_type(statement, 5) != raw.SQLITE_NULL;
        var written = hasWritten ? raw.sqlite3_column_int64(statement, 5) : 0;
        var context = raw.sqlite3_column_type(statement, 10) == raw.SQLITE_NULL ? -1 : raw.sqlite3_column_int64(statement, 10);
        var restored = RustStoredUsage.Restore(time, raw.sqlite3_column_int64(statement, 2), cached, written, context);
        if (restored.Ticks < 0) return ReadEvent(reader, provider);
        return new UsageEvent(raw.sqlite3_column_text(statement,0).utf8_to_string(), new DateTimeOffset(restored.Ticks,TimeSpan.Zero),
            new(restored.Input, cached, raw.sqlite3_column_int64(statement,4), hasWritten ? written : null),
            raw.sqlite3_column_text(statement,6).utf8_to_string(), raw.sqlite3_column_text(statement,7).utf8_to_string(),
            raw.sqlite3_column_text(statement,8).utf8_to_string(), provider, raw.sqlite3_column_text(statement,9).utf8_to_string())
        {
            PricingContext = restored.Context switch { 0 => PricingContext.Standard, 1 => PricingContext.HighContext, _ => null },
            Repaired = raw.sqlite3_column_int64(statement,11) != 0
        };
    }
}
#endif
