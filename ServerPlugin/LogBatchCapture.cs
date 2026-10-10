using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CometWorks.Diagnostics;

// This source is also compiled into the archive package; keep it compatible with net48.
public static class LogBatchCapture
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private const int MessageLimit = 7680;

    private sealed class Work
    {
        public string Directory, PolicyPath, Source, Severity, Message;
        public DateTimeOffset At;
        public long Generation;
        public bool Force;
        public TaskCompletionSource<bool> Completion;
    }

    private static readonly BlockingCollection<Work> Queue = new(1024);
    private static readonly Lazy<Task> Writer = new(() => Task.Factory.StartNew(WriteLoop,
        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));

    public static void Append(string directory, string policyPath, string source, string severity, string message)
    {
        if (string.IsNullOrEmpty(directory) || Queue.Count >= 1024) return;
        try
        {
            var at = DateTimeOffset.UtcNow;
            // Bind admission to the actual consent generation, before asynchronous work.
            // This small read cannot be deferred: a queued line must never move into a later opt-in.
            var policy = ReadPolicy(policyPath, at);
            if (policy == null)
            {
                if (File.Exists(Path.Combine(directory, "log-batch.pending")) && Queue.TryAdd(new Work
                    { Directory = directory, PolicyPath = policyPath, Completion = new TaskCompletionSource<bool>() }))
                    _ = Writer.Value;
                return;
            }
            if (Queue.TryAdd(new Work { Directory = directory, PolicyPath = policyPath, At = at,
                Generation = policy.Generation, Source = Limit(source, 128), Severity = Limit(severity, 32),
                Message = Limit(message, 512) })) _ = Writer.Value;
        }
        catch { /* Never recurse into application logging or interrupt its caller. */ }
    }

    public static void Flush(string directory, string policyPath, bool force = false)
    {
        if (string.IsNullOrEmpty(directory)) return;
        try
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!Queue.TryAdd(new Work { Directory = directory, PolicyPath = policyPath, Force = force,
                Completion = completion })) return;
            _ = Writer.Value;
            // Explicit collector/shutdown flushes may wait; ordinary SDK calls never wait on persistence.
            completion.Task.Wait(TimeSpan.FromSeconds(5));
        }
        catch { }
    }

    private static void WriteLoop()
    {
        foreach (var first in Queue.GetConsumingEnumerable())
        {
            var work = new List<Work> { first };
            if (first.Completion == null)
            {
                var until = DateTime.UtcNow.AddMilliseconds(100);
                Work next;
                while (work.Count < 64 && Queue.TryTake(out next,
                    Math.Max(0, (int)(until - DateTime.UtcNow).TotalMilliseconds)))
                {
                    work.Add(next);
                    if (next.Completion != null) break;
                }
            }
            foreach (var group in work.Where(w => w.Completion == null).GroupBy(w => new { w.Directory, w.PolicyPath }))
            {
                try { AppendBatch(group.Key.Directory, group.Key.PolicyPath, group); }
                catch { /* A full disk or another process's writer cannot fault the capture worker. */ }
            }
            foreach (var flush in work.Where(w => w.Completion != null))
            {
                try { FlushCore(flush.Directory, flush.PolicyPath, flush.Force); }
                catch { }
                finally { flush.Completion.TrySetResult(true); }
            }
        }
    }

    private static void AppendBatch(string directory, string policyPath, IEnumerable<Work> work)
    {
        lock (Gate)
        using (var lease = Lock(directory))
        {
            var policy = ReadPolicy(policyPath, DateTimeOffset.UtcNow);
            string pendingPath = Path.Combine(directory, "log-batch.pending");
            if (policy == null) { File.Delete(pendingPath); return; }
            var pending = ReadPending(pendingPath, policy);
            foreach (var item in work)
            {
                if (item.Generation != policy.Generation || item.At < policy.GrantedSinceUtc) continue;
                string line = JsonSerializer.Serialize(new { atUtc = item.At, source = item.Source,
                    severity = item.Severity, message = item.Message }, Json) + "\n";
                if (pending != null && (pending.Message.Length + line.Length > MessageLimit
                    || Count(pending) >= 64 || item.At - pending.CapturedAtUtc >= TimeSpan.FromSeconds(30)))
                {
                    Publish(directory, pendingPath, pending, policyPath);
                    pending = null;
                }
                pending = pending ?? new Batch
                {
                    ReportId = Guid.NewGuid().ToString("N"), CapturedAtUtc = item.At,
                    Context = new Dictionary<string, string>
                    {
                        ["captureConsentGeneration"] = policy.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["batchStartedAtUtc"] = item.At.ToString("O"), ["lineCount"] = "0"
                    }
                };
                pending.Message += line;
                pending.Context["lineCount"] = (Count(pending) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                pending.Context["batchEndedAtUtc"] = item.At.ToString("O");
            }
            if (pending != null && Eligible(pending, ReadPolicy(policyPath, DateTimeOffset.UtcNow)))
                Write(pendingPath, pending, replace: true);
            else File.Delete(pendingPath);
        }
    }

    private static void FlushCore(string directory, string policyPath, bool force)
    {
        lock (Gate)
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
