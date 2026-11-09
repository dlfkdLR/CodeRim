using System.IO;
using System.Text;
using System.Text.Json;

namespace CodeRim.Core.Services;

/// <summary>
/// The conversation title Claude Code shows in its terminal tab and /resume list.
/// Titles are appended as <c>custom-title</c> (/rename) and <c>ai-title</c> entries
/// anywhere in a transcript, so each file keeps a read offset and only the bytes
/// appended since the previous call are scanned.
/// </summary>
public sealed class ClaudeTranscriptTitles
{
    private const int ChunkBytes = 64 * 1024;
    private const int CachedFiles = 64;
    // shortcut: records over 1 MiB are skipped, raise only for a supported title needing more.
    private const int LineBytes = 1024 * 1024;
    private sealed class Scan
    {
        public long Offset;
        public long ReadOffset;
        public ulong Access;
        public byte[] Carry = [];
        public int CarryCount;
        public bool Discarding;
        public string? Custom;
        public string? Generated;
    }
    private static readonly byte[] Marker = Encoding.UTF8.GetBytes("-title\"");
    private readonly Dictionary<string, Scan> scans = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private ulong access;

    public static ClaudeTranscriptTitles Shared { get; } = new();

    public string? Title(string transcript)
    {
        lock (gate)
        {
            if (!scans.TryGetValue(transcript, out var scan))
            {
                if (scans.Count >= CachedFiles) scans.Remove(scans.MinBy(pair => pair.Value.Access).Key);
                scans[transcript] = scan = new Scan();
            }
            scan.Access = ++access;
            try
            {
                using var stream = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < scan.ReadOffset) scans[transcript] = scan = new Scan { Access = access };
                var end = stream.Length;
                if (end > scan.ReadOffset)
                {
                    stream.Seek(scan.ReadOffset, SeekOrigin.Begin);
                    var chunk = new byte[ChunkBytes];
                    while (scan.ReadOffset < end)
                    {
                        var read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, end - scan.ReadOffset));
                        if (read == 0) break;
                        var data = chunk.AsSpan(0, read);
                        while (!data.IsEmpty)
                        {
                            var newline = data.IndexOf((byte)'\n');
                            var part = newline < 0 ? data : data[..newline];
                            if (!scan.Discarding)
                            {
                                var required = scan.CarryCount + part.Length;
                                if (required <= LineBytes)
                                {
                                    if (required > scan.Carry.Length)
                                        Array.Resize(ref scan.Carry, Math.Min(LineBytes, Math.Max(required, scan.Carry.Length * 2)));
                                    part.CopyTo(scan.Carry.AsSpan(scan.CarryCount));
                                    scan.CarryCount = required;
                                }
                                else { scan.Carry = []; scan.CarryCount = 0; scan.Discarding = true; }
                            }
                            scan.ReadOffset += part.Length;
                            if (newline < 0) break;
                            scan.ReadOffset++;
                            scan.Offset = scan.ReadOffset;
                            if (!scan.Discarding) Fold(scan.Carry.AsSpan(0, scan.CarryCount), scan);
                            scan.CarryCount = 0;
                            scan.Discarding = false;
                            data = data[(newline + 1)..];
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            finally { if (scan.CarryCount == 0) scan.Carry = []; }
            return scan.Custom ?? scan.Generated;
        }
    }

    private static void Fold(ReadOnlySpan<byte> data, Scan scan)
    {
        while (data.Length > 0)
        {
            var end = data.IndexOf((byte)'\n');
            var line = end < 0 ? data : data[..end];
            data = end < 0 ? [] : data[(end + 1)..];
            if (line.IndexOf(Marker) < 0) continue;
            try
            {
                using var document = JsonDocument.Parse(line.ToArray());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) continue;
                string? Text(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    && value.GetString()?.Trim() is { Length: > 0 } text ? text : null;
                switch (type.GetString())
                {
                    case "custom-title": if (Text("customTitle") is { } custom) scan.Custom = custom; break;
                    case "ai-title": if (Text("aiTitle") is { } generated) scan.Generated = generated; break;
                }
            }
            catch (JsonException) { }
        }
    }
}
