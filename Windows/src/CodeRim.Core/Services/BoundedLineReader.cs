using System.Buffers;
using System.Security.Cryptography;
using CodeRim.Core.Parsing;

namespace CodeRim.Core.Services;

internal sealed record BoundedLine(byte[]? Bytes, bool Oversized);

internal static class BoundedLineReader
{
    private const int ReadBlockBytes = 64 * 1024;

    public static IEnumerable<BoundedLine> Read(
        Stream stream,
        long maximumBytes,
        CancellationToken cancellationToken,
        IncrementalHash? prefixHash = null) =>
        maximumBytes <= ReadBlockBytes
            ? ReadSmall(stream, maximumBytes, prefixHash, cancellationToken)
            : ReadChunks(stream, maximumBytes, prefixHash, cancellationToken);

    // A frozen prefix that fits in one read is normally served directly from
    // this buffer. Keep a separate prefix only if the stream returns a short read.
    private static IEnumerable<BoundedLine> ReadSmall(Stream stream, long budget, IncrementalHash? hash, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (budget <= 0) yield break;
        var input = new byte[(int)budget];
        byte[]? prefix = null;
        var length = 0;
        var remaining = input.Length;
        while (remaining > 0)
        {
            var read = stream.Read(input, 0, remaining);
            if (read == 0) break;
            remaining -= read;
            hash?.AppendData(input, 0, read);
            var start = 0;
            while (start < read)
            {
                var end = Array.IndexOf(input, (byte)'\n', start, read - start);
                if (end < 0)
                {
                    if (remaining != 0)
                    {
                        prefix ??= new byte[input.Length];
                        input.AsSpan(start, read - start).CopyTo(prefix.AsSpan(length));
                    }
                    length += read - start;
                    break;
                }
                yield return new BoundedLine(CopyLine(prefix.AsMemory(0, length), input, start, end - start), false);
                length = 0;
                start = end + 1;
            }
            token.ThrowIfCancellationRequested();
        }
        if (length > 0) yield return new BoundedLine(null, true);
    }

    private static IEnumerable<BoundedLine> ReadChunks(Stream stream, long maximumBytes, IncrementalHash? prefixHash, CancellationToken cancellationToken)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(ReadBlockBytes);
        ArrayBufferWriter<byte>? prefix = null;
        uint[]? ends = null;
        uint pending = 0;
        var totalBytesRead = 0L;
        try
        {
            ends = ArrayPool<uint>.Shared.Rent(128);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remainingBytes = maximumBytes - totalBytesRead;
                if (remainingBytes <= 0) break;
                var read = stream.Read(
                    readBuffer,
                    0,
                    (int)Math.Min(readBuffer.Length, remainingBytes));
                if (read == 0) break;
                totalBytesRead += read;
                prefixHash?.AppendData(readBuffer, 0, read);

                var offset = 0;
                while (offset < read)
                {
                    var chunk = BoundedLineChunk.Inspect(readBuffer.AsSpan(offset, read - offset), pending, ends);
                    if (chunk.Consumed == 0 || chunk.Consumed > read - offset || chunk.Count > ends.Length)
                        throw new InvalidDataException("Invalid bounded line projection.");

                    var start = offset;
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        var end = offset + (int)(ends[i] & ~BoundedLineChunk.Oversized);
                        if ((ends[i] & BoundedLineChunk.Oversized) != 0)
                            yield return new BoundedLine(null, true);
                        else
                            yield return new BoundedLine(CopyLine(prefix?.WrittenMemory ?? default, readBuffer, start, end - start), false);
                        prefix?.Clear();
                        start = end + 1;
                    }

                    offset += (int)chunk.Consumed;
                    pending = chunk.Pending;
                    if (pending > CodexJsonlParser.MaximumLineBytes) prefix?.Clear();
                    else if (offset > start)
                    {
                        prefix ??= new ArrayBufferWriter<byte>(offset - start);
                        Append(prefix, readBuffer, start, offset - start);
                    }
                }
            }
            // A source can be frozen in the middle of a write. The release marks
            // every unfinished final record, including short ones, as oversized.
            if (pending != 0) yield return new BoundedLine(null, true);
        }
        finally
        {
            if (ends is not null) ArrayPool<uint>.Shared.Return(ends);
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static byte[] CopyLine(ReadOnlyMemory<byte> prefix, byte[] source, int start, int length)
    {
        if (prefix.IsEmpty) return source.AsMemory(start, length).ToArray();
        var line = new byte[prefix.Length + length];
        prefix.Span.CopyTo(line);
        source.AsSpan(start, length).CopyTo(line.AsSpan(prefix.Length));
        return line;
    }

    private static void Append(ArrayBufferWriter<byte> target, byte[] source, int start, int length)
    {
        if (length == 0) return;
        source.AsSpan(start, length).CopyTo(target.GetSpan(length));
        target.Advance(length);
    }
}
