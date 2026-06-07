using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

public class WardenStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);

    private readonly string _connectionString;

    public WardenStore()
    {
        var path = Path.Join(DavDatabaseContext.ConfigPath, "warden.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Initialize();
        Prune();
        TryMigrateLegacyJson();
    }

    public int Count
    {
        get
        {
            try
            {
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM warden_entries WHERE dead_at >= $cutoff";
                cmd.Parameters.AddWithValue("$cutoff", Cutoff());
                return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
            }
            catch (Exception e)
            {
                Log.Debug(e, "Warden: count failed");
                return 0;
            }
        }
    }

    public void MarkDead(string? fp, string? backbone)
    {
        if (string.IsNullOrEmpty(fp)) return;
        var bk = string.IsNullOrWhiteSpace(backbone) ? "unknown" : backbone;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            using var conn = Open();
            var existing = "";
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT backbones FROM warden_entries WHERE fp = $fp";
                sel.Parameters.AddWithValue("$fp", fp);
                if (sel.ExecuteScalar() is string s) existing = s;
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO warden_entries (fp, dead_at, n, backbones) VALUES ($fp, $t, 1, $bk) " +
                "ON CONFLICT(fp) DO UPDATE SET dead_at = $t, n = n + 1, backbones = $bk";
            cmd.Parameters.AddWithValue("$fp", fp);
            cmd.Parameters.AddWithValue("$t", now);
            cmd.Parameters.AddWithValue("$bk", MergeBackbones(existing, bk));
            cmd.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Log.Debug(e, "Warden: mark failed");
        }
    }

    public bool IsDeadAnywhere(string? fp)
    {
        if (string.IsNullOrEmpty(fp)) return false;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM warden_entries WHERE fp = $fp AND dead_at >= $cutoff LIMIT 1";
            cmd.Parameters.AddWithValue("$fp", fp);
            cmd.Parameters.AddWithValue("$cutoff", Cutoff());
            return cmd.ExecuteScalar() is not null;
        }
        catch (Exception e)
        {
            Log.Debug(e, "Warden: lookup failed");
            return false;
        }
    }

    public bool IsDead(string? fp, string? backbone)
    {
        if (string.IsNullOrEmpty(fp)) return false;
        var bk = string.IsNullOrWhiteSpace(backbone) ? "unknown" : backbone;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT backbones FROM warden_entries WHERE fp = $fp AND dead_at >= $cutoff LIMIT 1";
            cmd.Parameters.AddWithValue("$fp", fp);
            cmd.Parameters.AddWithValue("$cutoff", Cutoff());
            return cmd.ExecuteScalar() is string s && SplitBackbones(s).Contains(bk);
        }
        catch
        {
            return false;
        }
    }

    public int Clear()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM warden_entries";
            return cmd.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Log.Debug(e, "Warden: clear failed");
            return 0;
        }
    }

    public async Task ExportToAsync(Stream output, CancellationToken ct)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        await writer.WriteLineAsync("{\"warden\":1}".AsMemory(), ct);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fp, dead_at, n, backbones FROM warden_entries WHERE dead_at >= $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", Cutoff());
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            var rec = new WardenRecord
            {
                Fp = reader.GetString(0),
                DeadAt = reader.GetInt64(1),
                Count = reader.GetInt32(2),
                Backbones = SplitBackbones(reader.GetString(3)),
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(rec, JsonOptions).AsMemory(), ct);
        }
        await writer.FlushAsync(ct);
    }

    public async Task<int> ImportFromAsync(Stream input, CancellationToken ct)
    {
        using var reader = new StreamReader(input);
        using var conn = Open();
        var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO warden_entries (fp, dead_at, n, backbones) VALUES ($fp, $t, $n, $bk) " +
            "ON CONFLICT(fp) DO UPDATE SET dead_at = MAX(dead_at, $t), n = n + $n, backbones = $bk";
        var pFp = new SqliteParameter("$fp", SqliteType.Text);
        var pT = new SqliteParameter("$t", SqliteType.Integer);
        var pN = new SqliteParameter("$n", SqliteType.Integer);
        var pBk = new SqliteParameter("$bk", SqliteType.Text);
        cmd.Parameters.Add(pFp);
        cmd.Parameters.Add(pT);
        cmd.Parameters.Add(pN);
        cmd.Parameters.Add(pBk);

        var processed = 0;
        var inBatch = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0) continue;
            WardenRecord? rec;
            try { rec = JsonSerializer.Deserialize<WardenRecord>(line, JsonOptions); }
            catch { continue; }
            if (rec is null || string.IsNullOrEmpty(rec.Fp)) continue;

            pFp.Value = rec.Fp;
            pT.Value = rec.DeadAt;
            pN.Value = rec.Count <= 0 ? 1 : rec.Count;
            pBk.Value = JoinBackbones(rec.Backbones);
            cmd.ExecuteNonQuery();
            processed++;

            if (++inBatch >= 5000)
            {
                await tx.CommitAsync(ct);
                await tx.DisposeAsync();
                tx = conn.BeginTransaction();
                cmd.Transaction = tx;
                inBatch = 0;
            }
        }
        await tx.CommitAsync(ct);
        await tx.DisposeAsync();
        return processed;
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "PRAGMA journal_mode=WAL;" +
                "CREATE TABLE IF NOT EXISTS warden_entries (" +
                "fp TEXT PRIMARY KEY, dead_at INTEGER NOT NULL, n INTEGER NOT NULL, " +
                "backbones TEXT NOT NULL DEFAULT '');" +
                "CREATE INDEX IF NOT EXISTS ix_warden_dead_at ON warden_entries(dead_at);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Log.Warning(e, "Warden: initialize failed");
        }
    }

    private void Prune()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM warden_entries WHERE dead_at < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", Cutoff());
            cmd.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Log.Debug(e, "Warden: prune failed");
        }
    }

    private void TryMigrateLegacyJson()
    {
        try
        {
            var jsonPath = Path.Join(DavDatabaseContext.ConfigPath, "warden.json");
            if (!File.Exists(jsonPath)) return;
            if (Count > 0) return;

            var model = JsonSerializer.Deserialize<WardenFile>(File.ReadAllText(jsonPath), JsonOptions);
            var migrated = 0;
            if (model?.Entries is not null)
            {
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText =
                    "INSERT INTO warden_entries (fp, dead_at, n, backbones) VALUES ($fp, $t, $n, $bk) " +
                    "ON CONFLICT(fp) DO NOTHING";
                var pFp = new SqliteParameter("$fp", SqliteType.Text);
                var pT = new SqliteParameter("$t", SqliteType.Integer);
                var pN = new SqliteParameter("$n", SqliteType.Integer);
                var pBk = new SqliteParameter("$bk", SqliteType.Text);
                cmd.Parameters.Add(pFp);
                cmd.Parameters.Add(pT);
                cmd.Parameters.Add(pN);
                cmd.Parameters.Add(pBk);
                foreach (var r in model.Entries)
                {
                    if (string.IsNullOrEmpty(r.Fp)) continue;
                    pFp.Value = r.Fp;
                    pT.Value = r.DeadAt;
                    pN.Value = r.Count <= 0 ? 1 : r.Count;
                    pBk.Value = JoinBackbones(r.Backbones);
                    cmd.ExecuteNonQuery();
                    migrated++;
                }
                tx.Commit();
            }
            File.Move(jsonPath, jsonPath + ".migrated", overwrite: true);
            Log.Information("Warden: migrated {Count} fingerprint(s) from legacy warden.json", migrated);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Warden: legacy migration failed");
        }
    }

    private static long Cutoff() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)Ttl.TotalSeconds;

    private static string MergeBackbones(string existingCsv, string add)
    {
        var set = new HashSet<string>(SplitBackbones(existingCsv), StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(add)) set.Add(add);
        return set.Count == 0 ? "unknown" : string.Join(",", set);
    }

    private static string JoinBackbones(string[]? backbones)
    {
        if (backbones is null || backbones.Length == 0) return "unknown";
        var set = new HashSet<string>(backbones.Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal);
        return set.Count == 0 ? "unknown" : string.Join(",", set);
    }

    private static string[] SplitBackbones(string csv) =>
        string.IsNullOrEmpty(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public class WardenFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("entries")] public List<WardenRecord> Entries { get; set; } = new();
}

public class WardenRecord
{
    [JsonPropertyName("fp")] public string Fp { get; set; } = "";
    [JsonPropertyName("bk")] public string[]? Backbones { get; set; }
    [JsonPropertyName("deadAt")] public long DeadAt { get; set; }
    [JsonPropertyName("n")] public int Count { get; set; }
}
