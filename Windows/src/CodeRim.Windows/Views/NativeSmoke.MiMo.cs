using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using CodeRim.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodeRim.Windows.Views;

internal static partial class NativeSmoke
{
    private static BrowserProfile MiMoSessionFixture(string directory)
    {
        var root = Path.Combine(directory, "mimo-firefox-fixture");
        var profile = Path.Combine(root, "Profiles", "selected");
        Directory.CreateDirectory(Path.Combine(profile, "sessionstore-backups"));
        File.WriteAllText(Path.Combine(root, "profiles.ini"), "[Profile0]\nName=Synthetic selected MiMo\nIsRelative=1\nPath=Profiles/selected\n");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(profile, "cookies.sqlite"), Pooling = false }.ToString()))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version=15; CREATE TABLE moz_cookies(name TEXT,value TEXT,host TEXT,path TEXT,isSecure INTEGER,expiry INTEGER,originAttributes TEXT)";
            command.ExecuteNonQuery();
        }
        var json = JsonSerializer.SerializeToUtf8Bytes(new { cookies = new[] {
            new { name = "api-platform_serviceToken", value = "fixture", host = "platform.xiaomimimo.com", path = "/api", secure = true },
            new { name = "userId", value = "fixture-user", host = "platform.xiaomimimo.com", path = "/api", secure = true } } });
        using var compressed = new MemoryStream(); compressed.Write("mozLz40\0"u8);
        Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)json.Length); compressed.Write(size);
        compressed.WriteByte(0xf0); var length = json.Length - 15;
        while (length >= 255) { compressed.WriteByte(255); length -= 255; }
        compressed.WriteByte((byte)length); compressed.Write(json);
        File.WriteAllBytes(Path.Combine(profile, "sessionstore-backups", "recovery.jsonlz4"), compressed.ToArray());
        var selected = FirefoxCookieImport.Profiles(root).Single();
        Require(FirefoxCookieImport.Read(selected, ["xiaomimimo.com"], DateTimeOffset.UtcNow).Count == 0,
            "MiMo session fixture unexpectedly has persisted cookies.");
        return selected;
    }
}
