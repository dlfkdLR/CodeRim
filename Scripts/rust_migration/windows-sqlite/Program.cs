using SQLitePCL;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodeRim.Core.Domain;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

var path=Path.GetFullPath(args[1]);
var connectionString = new SqliteConnectionStringBuilder {DataSource=path,Pooling=false}.ToString();
SqliteConnection Open()
{
    var c=new SqliteConnection(connectionString);c.Open();return c;
}
if(args[0]=="seed")
{
    _ = new UsageRepository(path);
    var count=int.Parse(args[2]);var repairs=int.Parse(args[3]);
    if(count is <0 or >100000 || repairs<0) throw new ArgumentOutOfRangeException();
    using var c=Open();using var tx=c.BeginTransaction();using var insert=c.CreateCommand();insert.Transaction=tx;
    insert.CommandText="""
        WITH RECURSIVE n(x) AS (SELECT 1 WHERE $count>0 UNION ALL SELECT x+1 FROM n WHERE x<$count)
        INSERT INTO events(provider,id,time,input,cached,output,written,model,project,session,projectId,highContext)
        SELECT CASE WHEN x%11=0 THEN 'claude' ELSE 'codex' END,printf('event-%08d',x),
               1600000000000+(x/7)*15000,x%413,x%12,x%53,CASE WHEN x%7>1 THEN x%30 END,
               'model-'||(x%4),'project '||(x/100%13),'session-'||(x/8%37),'pr-'||(x/100%13),
               CASE x%3 WHEN 0 THEN 0 WHEN 1 THEN 1 END FROM n;
        INSERT INTO repaired_events SELECT provider,id,time FROM events WHERE $repairs>0 AND rowid%MAX(1,$repairs)=0;
        """;
    insert.Parameters.AddWithValue("$count",count);insert.Parameters.AddWithValue("$repairs",repairs);insert.ExecuteNonQuery();tx.Commit();return;
}
var repo=new UsageRepository(path);
if(args[0]=="query")
{
    try
    {
        var items=repo.Read(args.Length>2 ? args[2] : "codex");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(var e in items) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new{e.EventKey,t=e.OccurredAt.Ticks,e.OccurredAt.Offset,
            e.Usage.InputTokens,e.Usage.CachedInputTokens,e.Usage.OutputTokens,e.Usage.CacheWriteInputTokens,e.Model,e.Project,e.SessionId,e.Provider,e.ProjectId,e.PricingContext,e.Repaired}));
        Console.WriteLine(items.Count+" "+Convert.ToHexString(hash.GetHashAndReset()));
    }
    catch(Exception ex) { Console.WriteLine(ex.GetType().Name+" "+ex.Message); }
    return;
}
if(args[0]=="rollback")
{
    try {repo.Merge("codex",[new("transactional-event",DateTimeOffset.UnixEpoch,new(10,0,0),SessionId:"root")]);Console.WriteLine("committed");}
    catch(Exception ex) { Console.WriteLine(ex.GetType().Name); }
    using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM events WHERE id='transactional-event'";
    Console.WriteLine(cmd.ExecuteScalar());return;
}
if(args[0]=="snapshot")
{
    using var c=Open();using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
    cmd.CommandText="UPDATE events SET input=input+1 WHERE provider='codex'";cmd.ExecuteNonQuery();
    var before=repo.Read("codex").Sum(x=>x.Usage.InputTokens);tx.Commit();
    var after=repo.Read("codex").Sum(x=>x.Usage.InputTokens);
    Console.WriteLine((after-before)+" "+repo.Read("codex").Count);return;
}
if(args[0]=="interrupt")
{
    // Exercise the actual cursor on this test-owned live connection, so SQLite can
    // interrupt the reader at a deterministic progress callback and we can verify cleanup.
    using var c=Open();
    var read=typeof(UsageRepository).GetMethod("Read",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
    int steps=0;
    raw.sqlite3_progress_handler(c.Handle,100,_=>++steps>=10 ? 1 : 0,null);
    try { read.Invoke(null,["codex",c,repo.IsNewerSchema,null]); throw new InvalidOperationException("The SQLite interrupt was ignored."); }
    catch(System.Reflection.TargetInvocationException ex) when(ex.InnerException is SqliteException db)
    { Console.WriteLine("sqlite interrupted "+db.SqliteErrorCode); }
    finally { raw.sqlite3_progress_handler(c.Handle,0,null,null); }
    using var cmd=c.CreateCommand();cmd.CommandText="SELECT 1";Console.WriteLine(cmd.ExecuteScalar());return;
}
if(args[0]!="bench") throw new ArgumentException();
var repeats=int.Parse(args[2]);var mode=args[3];
IReadOnlyList<UsageEvent> SqlOnly()
{
    using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="""
        SELECT e.id,e.time,e.input,e.cached,e.output,e.written,e.model,e.project,e.session,e.projectId,e.highContext,
            CASE WHEN EXISTS(SELECT 1 FROM repaired_events) THEN EXISTS(
               SELECT 1 FROM repaired_events r WHERE r.provider=e.provider AND r.id=e.id) ELSE 0 END
        FROM events e WHERE e.provider=$provider ORDER BY e.time
        """;
    cmd.Parameters.AddWithValue("$provider","codex");
    using var reader=cmd.ExecuteReader();var result=new List<UsageEvent>();
    while(reader.Read())
    {
        if(mode=="managed-cursor")
        {
            var stmt=reader.Handle!;
            for(int n=0;n<10;n++)
                if(n!=5 && raw.sqlite3_column_type(stmt,n)==raw.SQLITE_NULL) throw new InvalidDataException("Requires the populated benchmark schema.");
            var t=raw.sqlite3_column_int64(stmt,1);var read=raw.sqlite3_column_int64(stmt,3);
            var has=raw.sqlite3_column_type(stmt,5)!=raw.SQLITE_NULL;var write=has?raw.sqlite3_column_int64(stmt,5):0;
            var context=raw.sqlite3_column_type(stmt,10)==raw.SQLITE_NULL?-1:raw.sqlite3_column_int64(stmt,10);
            result.Add(new UsageEvent(raw.sqlite3_column_text(stmt,0).utf8_to_string(), DateTimeOffset.FromUnixTimeMilliseconds(t),
                new(unchecked(raw.sqlite3_column_int64(stmt,2)+read+write),read,raw.sqlite3_column_int64(stmt,4),has?write:null),
                raw.sqlite3_column_text(stmt,6).utf8_to_string(),raw.sqlite3_column_text(stmt,7).utf8_to_string(),
                raw.sqlite3_column_text(stmt,8).utf8_to_string(),"codex",raw.sqlite3_column_text(stmt,9).utf8_to_string())
                {PricingContext=context switch {0=>PricingContext.Standard,1=>PricingContext.HighContext,_=>null},
                 Repaired=raw.sqlite3_column_int64(stmt,11)!=0});
            continue;
        }
        // SQL-only alternative uses the original driver's getters and the same stored-counter arithmetic.
        var key=reader.GetString(0);var time=DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
        var uncached=reader.GetInt64(2);var cached=reader.GetInt64(3);var output=reader.GetInt64(4);
        long? written=reader.IsDBNull(5)?null:reader.GetInt64(5);
        result.Add(new UsageEvent(key,time,new(unchecked(uncached+cached+(written??0)),cached,output,written),
            reader.GetString(6),reader.GetString(7),reader.GetString(8),"codex",reader.GetString(9)) {
            PricingContext=reader.IsDBNull(10)?null:reader.GetInt64(10) switch {0=>PricingContext.Standard,1=>PricingContext.HighContext,_=>null},
            Repaired=reader.GetInt64(11)!=0});
    }
    return result;
}
long Consume()
{
    var rows=mode=="app"?repo.Read("codex"):SqlOnly();
    long sum=0;foreach(var x in rows)sum=unchecked(sum*17+x.OccurredAt.Ticks+x.Usage.InputTokens+x.Usage.CachedInputTokens+
        x.Usage.OutputTokens+x.Usage.CacheWriteInputTokens.GetValueOrDefault()+x.Model.Length+x.Project.Length+x.SessionId.Length+(x.Repaired?1:0));return sum;
}
for(int i=0;i<3;i++)Consume();
var checksum=0L;using var process=Process.GetCurrentProcess();var cpu=process.TotalProcessorTime;var allocated=GC.GetAllocatedBytesForCurrentThread();
var sw=Stopwatch.StartNew();for(int i=0;i<repeats;i++)checksum=unchecked(checksum+Consume());sw.Stop();var bytes=(GC.GetAllocatedBytesForCurrentThread()-allocated)/repeats;
process.Refresh();Console.WriteLine(JsonSerializer.Serialize(new{checksum,wallUs=sw.Elapsed.TotalMicroseconds/repeats,
    cpuUs=(process.TotalProcessorTime-cpu).TotalMicroseconds/repeats,managedBytes=bytes,peakRss=process.PeakWorkingSet64}));
