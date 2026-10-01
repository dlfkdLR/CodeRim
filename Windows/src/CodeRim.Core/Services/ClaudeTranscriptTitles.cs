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
    private sealed class Scan { public long Offset; public string? Custom; public string? Generated; }
    private static readonly byte[] Marker = Encoding.UTF8.GetBytes("-title\"");
    private readonly Dictionary<string, Scan> scans = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    public static ClaudeTranscriptTitles Shared { get; } = new();

    public string? Title(string transcript)
    {
        lock (gate)
        {
            if (!scans.TryGetValue(transcript, out var scan)) scans[transcript] = scan = new Scan();
            try
            {
                using var stream = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < scan.Offset) { scan.Offset = 0; scan.Custom = scan.Generated = null; }
                if (stream.Length > scan.Offset)
                {
                    stream.Seek(scan.Offset, SeekOrigin.Begin);
                    var data = new byte[stream.Length - scan.Offset];
                    var read = 0;
                    while (read < data.Length && stream.Read(data, read, data.Length - read) is var count and > 0) read += count;
                    // Stop at the last complete line; a half-written one is read next time.
                    var last = Array.LastIndexOf(data, (byte)'\n', read - 1);
                    if (last >= 0) { Fold(data.AsSpan(0, last + 1), scan); scan.Offset += last + 1; }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
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
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) continue;
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
