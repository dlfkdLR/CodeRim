using System.Buffers;
using System.Security.Cryptography;
using CodeRim.Core.Parsing;

namespace CodeRim.Core.Services;

internal sealed record BoundedLine(byte[]? Bytes, bool Oversized);

internal static class BoundedLineReader
{
    public static IEnumerable<BoundedLine> Read(
        Stream stream,
        long maximumBytes,
        CancellationToken cancellationToken,
        IncrementalHash? prefixHash = null)
    {
        var readBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var lineBuffer = new ArrayBufferWriter<byte>(64 * 1024);
        var oversized = false;
        var totalBytesRead = 0L;
        try
        {
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

                for (var index = 0; index < read; index++)
                {
                    var value = readBuffer[index];
                    if (value == (byte)'\n')
                    {
                        yield return oversized
                            ? new BoundedLine(null, true)
                            : new BoundedLine(lineBuffer.WrittenMemory.ToArray(), false);
                        lineBuffer.Clear();
                        oversized = false;
                        continue;
                    }

                    if (oversized)
                    {
                        continue;
                    }

                    if (lineBuffer.WrittenCount >= CodexJsonlParser.MaximumLineBytes)
                    {
                        lineBuffer.Clear();
                        oversized = true;
                        continue;
                    }

                    lineBuffer.GetSpan(1)[0] = value;
                    lineBuffer.Advance(1);
                }
            }
            if (oversized || lineBuffer.WrittenCount > 0) yield return new BoundedLine(null, true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }
}
