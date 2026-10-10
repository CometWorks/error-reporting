using System.Text.Json;
using PluginSdk.Logging;
using ServerPlugin;

string directory = Path.Combine(Path.GetTempPath(), "error-reporting-test-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("ERROR_REPORTING_DIRECTORY", directory);
Environment.SetEnvironmentVariable("ERROR_REPORTING_CONTEXT", "{\"runId\":\"test-run\",\"nodeId\":\"n1\"}");
try
{
    IncidentCapture.Start(directory);
    var log = new Logger("test-plugin", new NullSink());
    log.Info("Not an incident");
    if (Directory.GetFiles(directory).Length != 0) throw new Exception("Info captured");
    log.Error("Failure", new InvalidOperationException("test-exception"), new { transferId = "t1", worldState = "DO NOT CAPTURE" });
    string path = Directory.GetFiles(directory, "*.json").Single();
    using var report = JsonDocument.Parse(File.ReadAllBytes(path));
    var root = report.RootElement;
    if (root.GetProperty("schemaVersion").GetInt32() != 1 || !Guid.TryParseExact(root.GetProperty("reportId").GetString(), "N", out _))
        throw new Exception("Invalid report identity");
    if (root.GetProperty("context").GetProperty("runId").GetString() != "test-run"
        || root.GetProperty("context").GetProperty("transferId").GetString() != "t1"
        || root.GetProperty("context").TryGetProperty("worldState", out _)
        || !root.GetProperty("exception").GetString()!.Contains("test-exception"))
        throw new Exception("Missing correlation or captured world state");
    for (int i = 0; i < 500; i++) log.Error("Storm");
    if (Directory.GetFiles(directory, "*.json").Length != 32) throw new Exception("Rate limit failed");
    IncidentCapture.Capture("test", "unhandled_exception", "Critical", "Fatal", null, fatal: true);
    if (Directory.GetFiles(directory, "*.json").Length != 33) throw new Exception("Fatal bypass failed");
    if (Directory.GetFiles(directory, "*.tmp").Length != 0) throw new Exception("Partial files leaked");
    File.WriteAllText(Path.Combine(directory, "run.json"), "trusted run metadata");
    File.SetLastWriteTimeUtc(Path.Combine(directory, "run.json"), DateTime.UtcNow.AddYears(-1));
    while (Directory.GetFiles(directory, "*.json").Length < 257)
        File.WriteAllText(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"), "{}");
    IncidentCapture.Capture("test", "unhandled_exception", "Critical", "Full spool fatal", null, fatal: true);
    if (File.ReadAllText(Path.Combine(directory, "run.json")) != "trusted run metadata"
        || Directory.GetFiles(directory, "*.json").Length != 257)
        throw new Exception("Fatal eviction removed trusted run metadata");
    CheckLogBatches(directory);
    Console.WriteLine("Capture: identity, context, exception, filtering, rate limit, fatal reserve and consent-bound log batching passed.");
}
finally { Directory.Delete(directory, true); }
static void CheckLogBatches(string root)
{
    string run = Path.Combine(root, "batch-test"); Directory.CreateDirectory(run);
    string policy = Path.Combine(root, "policy.json");
    void SetPolicy(long generation, bool granted, DateTimeOffset? expiry = null) => File.WriteAllText(policy,
        JsonSerializer.Serialize(new { diagnosticsGranted = granted, generation, grantedSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            expiresAtUtc = expiry ?? DateTimeOffset.UtcNow.AddMinutes(1), keyExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) }));
    void Append(string text) => CometWorks.Diagnostics.LogBatchCapture.Append(run, policy, "SDK", "Information", text);
    void Flush() => CometWorks.Diagnostics.LogBatchCapture.Flush(run, policy, force: true);
    SetPolicy(1, false); Append("before consent"); Flush();
    if (Directory.GetFiles(run).Length != 0) throw new Exception("Disabled log collection wrote files");
    SetPolicy(1, true); Append("first permitted"); Append("second permitted");
    if (!File.Exists(Path.Combine(run, "log-batch.pending"))) throw new Exception("Pending batch was not durable");
    CometWorks.Diagnostics.LogBatchCapture.Flush(run, policy);
    if (Directory.GetFiles(run, "log-*.json").Length != 0) throw new Exception("Polling published a premature tiny batch");
    Flush();
    string batch = Directory.GetFiles(run, "log-*.json").Single();
    using (var doc = JsonDocument.Parse(File.ReadAllText(batch)))
    {
        var value = doc.RootElement;
        string message = value.GetProperty("message").GetString()!;
        if (value.GetProperty("kind").GetString() != "log_batch" || message.Contains("before consent")
            || value.GetProperty("context").GetProperty("lineCount").GetString() != "2") throw new Exception("Batch schema or consent filter failed");
        if (message.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length != 2) throw new Exception("Batch lines invalid");
    }
    // A crash can leave the pending journal after its completed batch was published.
    File.Copy(batch, Path.Combine(run, "log-batch.pending"));
    Append("after publication recovery"); Flush();
    if (Directory.GetFiles(run, "log-*.json").Length != 2) throw new Exception("Publication recovery reused an already completed ID");
    Append("withdrawn pending"); SetPolicy(2, false); Flush();
    if (File.Exists(Path.Combine(run, "log-batch.pending"))) throw new Exception("Revocation retained pending batch");
    SetPolicy(3, true); Append("previous generation"); SetPolicy(4, true); Append("current generation"); Flush();
    foreach (string file in Directory.GetFiles(run, "log-*.json"))
        if (File.ReadAllText(file).Contains("previous generation") || File.ReadAllText(file).Contains("withdrawn pending"))
            throw new Exception("Old consent generation was published");
    Append("expired pending"); SetPolicy(4, true, DateTimeOffset.UtcNow.AddSeconds(-1)); Flush();
    if (File.Exists(Path.Combine(run, "log-batch.pending"))) throw new Exception("Expired lease retained pending batch");
    SetPolicy(5, true);
    File.WriteAllText(Path.Combine(run, "run.json"), "trusted");
    string fatal = Path.Combine(run, Guid.NewGuid().ToString("N") + ".json"); File.WriteAllText(fatal, "fatal");
    for (int n = 0; n < 40; n++) { Append(new string('x', 10000)); Flush(); }
    if (Directory.GetFiles(run, "log-*.json").Length != 32 || !File.Exists(fatal)
        || File.ReadAllText(Path.Combine(run, "run.json")) != "trusted") throw new Exception("Ordinary batch quota affected fatal metadata");
    foreach (string file in Directory.GetFiles(run, "log-*.json"))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (doc.RootElement.GetProperty("message").GetString()!.Length > 7680) throw new Exception("Batch message exceeds bound");
    }
}
sealed class NullSink : ILogSink { public void Write(in LogEntry entry) { } }
