#if CODERIM_RUST_ACCOUNTING
using System.Buffers;
using System.Text.Json;
using CodeRim.Core.Domain;
using static CodeRim.Core.Services.RustCompanionEnvelope;

namespace CodeRim.Core.Services;

internal static class RustCompanionSnapshot
{
    private static readonly string[] PeriodNames = ["today", "week", "month", "all-time"];

    // The root, provider array and provider object consume part of the source document's
    // 64-level depth budget. Defer unusually nested fragments to the original full decoder.
    private static readonly JsonSerializerOptions ReadingOptions = new(CompanionFile.JsonOptions) { MaxDepth = 48 };

    internal static CompanionSnapshot? TryRead(ReadOnlySpan<byte> bytes)
    {
        var buffer = ArrayPool<Row>.Shared.Rent(ProviderLimit);
        try
        {
            int count = Read(bytes, buffer.AsSpan(0, ProviderLimit), out var generated);
            if (count < 0) return null;
            if (count > ProviderLimit) throw new InvalidDataException("Invalid companion provider count.");
            var providers = new CompanionProvider[count];
            for (var index = 0; index < count; index++)
            {
                ref readonly var row = ref buffer[index];
                var limits = JsonSerializer.Deserialize<ProviderReading>(row.Limits.From(bytes), ReadingOptions);
                if (limits is null) return null;
                var cached = row.Cache.Length == 0 ? null : JsonSerializer.Deserialize<ProviderReading>(row.Cache.From(bytes), ReadingOptions);
                LocalUsage? local = null;
                if (row.Scope.Length > 0)
                {
                    var totals = new Dictionary<string, TokenUsage>(row.Count, StringComparer.Ordinal);
                    for (int n = 0; n < row.Count; n++)
                    {
                        var period = row.At(n);
                        totals.Add(PeriodNames[period.Kind], period.Usage.ToUsage());
                    }
                    local = new(Text(row.Scope.From(bytes)), Text(row.State.From(bytes)),
                        row.UpdatedAt.Length == 0 ? null : Date(row.UpdatedAt.From(bytes)), Date(row.PeriodsAsOf.From(bytes)),
                        Text(row.Zone.From(bytes)), totals) { Stale = row.Stale != 0 };
                }
                providers[index] = new(Text(row.Id.From(bytes)), Text(row.Name.From(bytes)), row.Enabled != 0, local, limits,
                    row.AccountScope.Length == 0 ? null : Text(row.AccountScope.From(bytes)), cached);
            }
            return new(1, Date(generated.From(bytes)), providers);
        }
        // The complete Windows decoder owns compatibility and the public error classification
        // for unusual strings, dates, disabled rows and nested provider-readings.
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException)
        { return null; }
        finally { ArrayPool<Row>.Shared.Return(buffer); }
    }

    private static string Text(ReadOnlySpan<byte> raw)
    {
        var reader = new Utf8JsonReader(raw);
        return reader.Read() ? reader.GetString()! : throw new JsonException();
    }

    private static DateTimeOffset Date(ReadOnlySpan<byte> raw)
    {
        var reader = new Utf8JsonReader(raw);
        return reader.Read() ? reader.GetDateTimeOffset() : throw new JsonException();
    }
}
#endif
