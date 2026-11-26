using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;

internal sealed record Sample(string Name, byte[] Input, int[] Chunks, long Budget = long.MaxValue,
    int ThrowOnRead = 0, int CancelOnRead = 0, int CancelAfter = -1, int StopAfter = int.MaxValue,
    bool WithHash = true);

internal sealed class ScriptedStream(Sample sample, CancellationTokenSource cancellation) : Stream
{
    internal int Consumed;
    internal int Calls;
    internal bool Closed;
    internal readonly List<int> Requested = new();
    public override int Read(byte[] buffer, int offset, int count)
    {
        Calls++;
        Requested.Add(count);
        if (sample.ThrowOnRead == Calls) throw new IOException("fixture read failed");
        if (sample.CancelOnRead == Calls) cancellation.Cancel();
        int chunk = sample.Chunks[(Calls - 1) % sample.Chunks.Length];
        int available = Math.Min(count, Math.Min(chunk, sample.Input.Length - Consumed));
        sample.Input.AsSpan(Consumed, available).CopyTo(buffer.AsSpan(offset));
        Consumed += available;
        return available;
    }
    protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] bytes, int offset, int count) => throw new NotSupportedException();
}

internal static class Program
{
    const int Block = 65536;
    const int MaximumLine = 1048576;
    const int LargeRows = 3072;
    static readonly byte[] HashSeed = [0xa5, 0, 0xff];
    static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);
    static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    static byte[] Line(int size, bool newline = true)
    {
        var data = new byte[size + (newline ? 1 : 0)];
        for (int i = 0; i < size; i++) data[i] = (byte)(33 + (i * 19) % 190);
        if (newline) data[^1] = 10;
        return data;
    }
    static IEnumerable<Sample> Samples()
    {
        foreach (var (name, content) in new (string, byte[])[]{
            ("empty", []), ("lfs", [10,10,10]), ("crlf", Text("\r\n\nA\r\nB\r")),
            ("utf8", Text("👩🏽‍💻\n한글\r\n끝")), ("invalid", [0,255,195,10,13,0,10,128]),
            ("no-newline", Text("tail")), ("tail", Text("a\nb\nc"))})
        foreach (int[] chunks in new int[][]{[1],[1,2,7,3],[Block]})
        foreach (long budget in new long[]{-1,0,1,2,content.Length,content.Length+1,long.MaxValue}.Distinct())
            yield return new($"{name}/{string.Join('-',chunks)}/{budget}",content,chunks,budget);
        foreach (int n in new[]{4095,4096,4097,Block-1,Block,Block+1,MaximumLine-1,MaximumLine,MaximumLine+1,3*MaximumLine+23})
        foreach (bool terminated in new[]{false,true})
        {
            var input = Line(n,terminated);
            if (terminated) input = [..input,13,10,(byte)'x',10];
            foreach (var budget in new long[]{n-1,n,n+1,input.Length,long.MaxValue}.Distinct())
                yield return new($"size/{n}/{terminated}/{budget}",input,[Block,3587,9181],budget);
        }
        foreach (int count in new[] { 127, 128, 129, 255, 256, 257, 8193 })
        {
            var dense = Enumerable.Range(0, count).Select(index => index % 5 == 0 ? (byte)13 : (byte)10).ToArray();
            var emptyLines = Enumerable.Repeat((byte)10, count).ToArray();
            foreach (var input in new[] { dense, emptyLines })
                foreach (int boundary in new[] { 127, 128, 129, int.MaxValue })
                    yield return new($"dense/{count}/{input[0]}/{boundary}", input, [Block], StopAfter:boundary);
        }
        var bytes = Text("keep\r\nsecond\n\nthird\nlast");
        foreach (int chunk in new[]{1,3,7,Block})
        foreach (int at in new[]{0,1,2,3,4,7})
        {
            yield return new($"stop/{chunk}/{at}",bytes,[chunk],StopAfter:at);
            yield return new($"cancel-consumer/{chunk}/{at}",bytes,[chunk],CancelAfter:at);
            yield return new($"cancel-reader/{chunk}/{at}",bytes,[chunk],CancelOnRead:at);
            yield return new($"throw/{chunk}/{at}",bytes,[chunk],ThrowOnRead:at);
        }
        yield return new("empty-interrupt",[],[1],CancelAfter:0);
        yield return new("reader-cancel-ioerror",bytes,[5,8],ThrowOnRead:2,CancelOnRead:1);
        yield return new("zero-before-eof",bytes,[3,0,6]);
        yield return new("without-prefix-hash",bytes,[5,2],WithHash:false);
        yield return new("negative-minimum",bytes,[Block],long.MinValue);
        var rng = new Random(74119);
        for(int i=0;i<107;i++)
        {
            var data = new byte[rng.Next(0,6600)]; rng.NextBytes(data);
            for(int j=i%13;j<data.Length;j+=73) data[j]=10;
            yield return new($"random/{i}",data,[rng.Next(1,41),rng.Next(1,510),rng.Next(1,901)],
                rng.Next(0,data.Length+4),StopAfter:i%3==1 ? 4 : int.MaxValue,WithHash:i%2==0);
        }
    }
    static object Observe(Sample sample)
    {
        using var token = new CancellationTokenSource();
        if(sample.CancelAfter==0) token.Cancel();
        using var source = new ScriptedStream(sample,token);
        using var prefix = sample.WithHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        prefix?.AppendData(HashSeed);
        var borrowed = new List<(byte[]? Bytes,bool Oversized,int Read,int Calls,string? Prefix)>();
        string? error=null;
        var iterator = BoundedLineReader.Read(source,sample.Budget,token.Token,prefix).GetEnumerator();
        if(source.Calls!=0)throw new InvalidDataException("Read during iterator creation");
        try
        {
            while(borrowed.Count<sample.StopAfter && iterator.MoveNext())
            {
                var line=iterator.Current;
                borrowed.Add((line.Bytes,line.Oversized,source.Consumed,source.Calls,
                    prefix is null ? null : Convert.ToHexString(prefix.GetCurrentHash())));
                if(borrowed.Count==sample.CancelAfter) token.Cancel();
            }
        }
        catch(Exception ex) { error=ex.GetType().Name+"|"+ex.Message; }
        finally { iterator.Dispose(); }
        // Digest only after disposal so retaining or overwriting a previously returned buffer is observable.
        var rows=borrowed.Select(x=>new {x.Oversized, length=x.Bytes?.Length, bytes=x.Bytes is null ? null :
            x.Bytes.Length<=32 ? Convert.ToHexString(x.Bytes) : Hash(x.Bytes), x.Read,x.Calls,x.Prefix}).ToArray();
        var finalHash = prefix is null ? null : Convert.ToHexString(prefix.GetHashAndReset());
        if (prefix is not null)
        {
            using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            expected.AppendData(HashSeed);
            expected.AppendData(sample.Input, 0, source.Consumed);
            if (Convert.ToHexString(expected.GetHashAndReset()) != finalHash)
                throw new InvalidDataException("Prefix omitted bytes already read from the stream");
        }
        byte[] req=new byte[4*source.Requested.Count];
        for(int i=0;i<source.Requested.Count;i++)BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(4*i),source.Requested[i]);
        return new{sample.Name,bytes=source.Consumed,source.Calls,source.Closed,requests=Hash(req), rows,error,
            prefix=finalHash};
    }

    static byte[] ScannerFile()
    {
        using var buffer = new MemoryStream();
        for(int i=0;i<LargeRows;i++)
        {
            byte[] row=JsonSerializer.SerializeToUtf8Bytes(new {type="assistant",sessionId="synthetic-session",
                timestamp="2026-05-01T03:04:05Z",message=new {role="assistant",id="turn-"+i,model="claude-synthetic",
                    usage=new {input_tokens=11,output_tokens=7,cache_read_input_tokens=3}}});
            buffer.Write(row); buffer.WriteByte((byte)'\n');
        }
        return buffer.ToArray();
    }
    static object Scan(string directory)
    {
        var reader = new UsageScanner("claude",[directory],new byte[32]);
        var snapshot = reader.Scan(WeekStart.Monday,new DateTimeOffset(2026,5,1,12,0,0,TimeSpan.Zero));
        if(snapshot.Events.Count!=LargeRows) throw new InvalidDataException($"Expected {LargeRows} events, got {snapshot.Events.Count}");
        return new {snapshot.SourceCount,count=snapshot.Events.Count, events=Hash(JsonSerializer.SerializeToUtf8Bytes(snapshot.Events))};
    }
    static object Time(string name,int rounds,Func<long> action)
    {
        _=action();
        var elapsed=new List<double>();var allocated=new List<long>();
        long result=0;
        for(int i=0;i<7;i++)
        {
            long before=GC.GetAllocatedBytesForCurrentThread();
            long start=Stopwatch.GetTimestamp();
            for(int j=0;j<rounds;j++)result=action();
            elapsed.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds/rounds);
            allocated.Add((GC.GetAllocatedBytesForCurrentThread()-before)/rounds);
        }
        elapsed.Sort();allocated.Sort();
        return new{name,rounds,result,us=elapsed[3],bytes=allocated[3]};
    }
    static object InterleavedReaders()
    {
        const int count = 20;
        var readers = new IEnumerator<BoundedLine>[count];
        var streams = new MemoryStream[count];
        var outputs = Enumerable.Range(0, count).Select(_ => new List<byte[]>()).ToArray();
        try
        {
            for (int i = 0; i < count; i++)
            {
                byte[] data = new byte[2 * Block + i * 7];
                Array.Fill(data, (byte)(i + 50));
                for (int j = i; j < data.Length; j += 19 + i) data[j] = 10;
                data[^1] = 10;
                streams[i] = new MemoryStream(data);
                readers[i] = BoundedLineReader.Read(streams[i], data.Length, CancellationToken.None).GetEnumerator();
                if (!readers[i].MoveNext()) throw new InvalidDataException("Expected an initial line");
                outputs[i].Add(readers[i].Current.Bytes!);
            }
            Parallel.For(0, count, i =>
            {
                // Each reader is suspended independently while other streams replace or return offset buffers.
                while (readers[i].MoveNext())
                {
                    outputs[i].Add(readers[i].Current.Bytes!);
                    if (i % 3 == 0 && outputs[i].Count == 151) break;
                }
                readers[i].Dispose();
            });
            return outputs.Select(rows =>
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var row in rows) { hash.AppendData(row); hash.AppendData([10]); }
                return new {count = rows.Count, hash = Convert.ToHexString(hash.GetHashAndReset())};
            }).ToArray();
        }
        finally
        {
            foreach (var reader in readers) reader?.Dispose();
            foreach (var stream in streams) stream?.Dispose();
        }
    }

    static void Main(string[] args)
    {
        if (args[0] == "interleaved")
        {
            Console.WriteLine(JsonSerializer.Serialize(InterleavedReaders()));
            return;
        }
        if (args[0] == "first")
        {
            int size = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
            var bytes = size == 0 ? Array.Empty<byte>() : Line(size - 1);
            using var stream = new MemoryStream(bytes, false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            int lines = 0;
            foreach (var line in BoundedLineReader.Read(stream, size, CancellationToken.None, hash))
                if (!line.Oversized) lines++;
            double us = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            long used = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (lines != (size == 0 ? 0 : 1) || !hash.GetHashAndReset().AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                throw new InvalidDataException("Unexpected cold line or prefix");
            Console.WriteLine(JsonSerializer.Serialize(new {size,us,bytes=used}));
            return;
        }
        string directory=args[1],file=Path.Combine(directory,"synthetic.jsonl");
        Directory.CreateDirectory(directory);
        var fixture=ScannerFile();File.WriteAllBytes(file,fixture);
        if(args[0]=="observe")
        {
            foreach(var s in Samples()) Console.WriteLine(JsonSerializer.Serialize(Observe(s)));
            Console.WriteLine(JsonSerializer.Serialize(Scan(directory)));
            return;
        }
        var cases=new List<object>();
        foreach(var (name,data,n) in new (string,byte[],int)[]{("empty",[],100),("short",Text("a\r\nb\n"),100),
            ("4096",Line(4096),30),("65536",Line(Block),12),("1048575",Line(MaximumLine-1),2),
            ("1048576",Line(MaximumLine),2),("1048577",Line(MaximumLine+1),2),("multi-MiB",Line(3*MaximumLine+23),1),
            ("3072-JSONL",fixture,2)})
        foreach(bool withHash in new[]{false,true})
        {
            cases.Add(Time(name+(withHash?"/prefix":"/plain"),n,()=>
            {
                using var stream=new MemoryStream(data,false);
                using var hash=withHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256):null;
                long sum=0;foreach(var line in BoundedLineReader.Read(stream,data.LongLength,CancellationToken.None,hash))
                    sum+=line.Oversized ? -1 : line.Bytes!.LongLength+1;
                return sum;
            }));
        }
        cases.Add(Time("3072-full-scanner",1,()=>
        {
            var scanner=new UsageScanner("claude",[directory],new byte[32]);
            return scanner.Scan(WeekStart.Monday,new DateTimeOffset(2026,5,1,12,0,0,TimeSpan.Zero)).Events.Count;
        }));
        Console.WriteLine(JsonSerializer.Serialize(cases));
    }
}
