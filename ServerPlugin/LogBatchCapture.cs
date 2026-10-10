using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CometWorks.Diagnostics;

// This source is also compiled into the archive package; keep it compatible with net48.
public static class LogBatchCapture
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private const int MessageLimit = 7680;

    public static void Append(string directory, string policyPath, string source, string severity, string message)
    {
        if (string.IsNullOrEmpty(directory) || !Monitor.TryEnter(Gate)) return;
        try
        {
            if (ReadPolicy(policyPath, DateTimeOffset.UtcNow) == null
                && !File.Exists(Path.Combine(directory, "log-batch.pending"))) return;
            using (var lease = Lock(directory))
            {
                var now = DateTimeOffset.UtcNow;
                var policy = ReadPolicy(policyPath, now);
                string pendingPath = Path.Combine(directory, "log-batch.pending");
                if (policy == null) { File.Delete(pendingPath); return; }
                var pending = ReadPending(pendingPath, policy);
                string line = JsonSerializer.Serialize(new { atUtc = now, source = Limit(source, 128),
                    severity = Limit(severity, 32), message = Limit(message, 512) }, Json) + "\n";
                if (pending != null && (pending.Message.Length + line.Length > MessageLimit
                    || Count(pending) >= 64 || now - pending.CapturedAtUtc >= TimeSpan.FromSeconds(30)))
                {
                    Publish(directory, pendingPath, pending, policyPath);
                    pending = null;
                }
                pending = pending ?? new Batch
                {
                    ReportId = Guid.NewGuid().ToString("N"), CapturedAtUtc = now,
                    Context = new Dictionary<string, string>
                    {
                        ["captureConsentGeneration"] = policy.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["batchStartedAtUtc"] = now.ToString("O"), ["lineCount"] = "0"
                    }
                };
                pending.Message += line;
                pending.Context["lineCount"] = (Count(pending) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                pending.Context["batchEndedAtUtc"] = now.ToString("O");
                Write(pendingPath, pending, replace: true);
            }
        }
        catch { /* Never recurse into application logging or interrupt its caller. */ }
        finally { Monitor.Exit(Gate); }
    }

    public static void Flush(string directory, string policyPath, bool force = false)
    {
        if (string.IsNullOrEmpty(directory) || !Monitor.TryEnter(Gate)) return;
        try
        {
            if (ReadPolicy(policyPath, DateTimeOffset.UtcNow) == null
                && !File.Exists(Path.Combine(directory, "log-batch.pending"))) return;
            using (var lease = Lock(directory))
            {
                string path = Path.Combine(directory, "log-batch.pending");
                var policy = ReadPolicy(policyPath, DateTimeOffset.UtcNow);
                if (policy == null) { File.Delete(path); return; }
                var pending = ReadPending(path, policy);
                if (pending != null && (force || DateTimeOffset.UtcNow - pending.CapturedAtUtc >= TimeSpan.FromSeconds(30)))
                    Publish(directory, path, pending, policyPath);
            }
        }
        catch { }
        finally { Monitor.Exit(Gate); }
    }

    private static FileStream Lock(string directory) => new FileStream(Path.Combine(directory, ".log-batch.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static Policy ReadPolicy(string path, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path) || new FileInfo(path).Length > 16384) return null;
        var p = JsonSerializer.Deserialize<Policy>(File.ReadAllText(path), Json);
        return p != null && p.DiagnosticsGranted && p.Generation > 0 && p.GrantedSinceUtc != default
            && p.GrantedSinceUtc <= now && p.ExpiresAtUtc > now && p.KeyExpiresAtUtc > now ? p : null;
    }

    private static Batch ReadPending(string path, Policy policy)
    {
        if (!File.Exists(path)) return null;
        Batch batch = null;
        try
        {
            if (new FileInfo(path).Length <= 65536) batch = JsonSerializer.Deserialize<Batch>(File.ReadAllText(path), Json);
        }
        catch { }
        if (batch != null && batch.Context != null && batch.Message != null && batch.Message.Length <= MessageLimit
            && Guid.TryParseExact(batch.ReportId, "N", out _) && Eligible(batch, policy)
            && !File.Exists(Path.Combine(Path.GetDirectoryName(path), "log-" + batch.ReportId + ".json"))) return batch;
        File.Delete(path);
        return null;
    }

    private static bool Eligible(Batch batch, Policy policy) => policy != null && batch.CapturedAtUtc >= policy.GrantedSinceUtc
        && batch.Context.TryGetValue("captureConsentGeneration", out string generation)
        && generation == policy.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void Publish(string directory, string pendingPath, Batch batch, string policyPath)
    {
        if (!Eligible(batch, ReadPolicy(policyPath, DateTimeOffset.UtcNow))) { File.Delete(pendingPath); return; }
        // Keep ordinary batches separate from the error/fatal incident quota.
        var existing = Directory.EnumerateFiles(directory, "log-*.json").OrderBy(File.GetLastWriteTimeUtc).ToArray();
        foreach (string stale in existing.Take(Math.Max(0, existing.Length - 31)))
        {
            File.Delete(stale);
            string id = Path.GetFileNameWithoutExtension(stale).Substring(4);
            if (Guid.TryParseExact(id, "N", out _)) File.Delete(Path.Combine(directory, id + ".sealed"));
        }
        string destination = Path.Combine(directory, "log-" + batch.ReportId + ".json");
        // Retrying after a crash between publication and pending deletion retains the same report ID.
        if (!File.Exists(destination)) Write(destination, batch, replace: false);
        File.Delete(pendingPath);
    }

    private static void Write(string path, Batch batch, bool replace)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
#if NETCOREAPP
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#endif
                byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(batch, Json));
                output.Write(json, 0, json.Length);
                output.Flush(true);
            }
            if (replace && File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { File.Delete(temporary); }
    }

    private static int Count(Batch batch) => batch.Context.TryGetValue("lineCount", out string count)
        && int.TryParse(count, out int value) ? value : 0;
    private static string Limit(string value, int maximum) => value == null ? "" : value.Substring(0, Math.Min(value.Length, maximum));

    private sealed class Policy
    {
        public Policy() { }
        public bool DiagnosticsGranted { get; set; }
        public long Generation { get; set; }
        public DateTimeOffset GrantedSinceUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public DateTimeOffset KeyExpiresAtUtc { get; set; }
    }
    private sealed class Batch
    {
        public Batch() { }
        public int SchemaVersion { get; set; } = 1;
        public string ReportId { get; set; }
        public DateTimeOffset CapturedAtUtc { get; set; }
        public string Source { get; set; } = "logs";
        public string Kind { get; set; } = "log_batch";
        public string Severity { get; set; } = "Information";
        public string Message { get; set; } = "";
        public string Exception { get; set; }
        public Dictionary<string, string> Context { get; set; }
    }
}
