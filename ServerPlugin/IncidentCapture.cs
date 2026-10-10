using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using PluginSdk.Logging;
using CometWorks.Diagnostics;

namespace ServerPlugin;

/// <summary>Local, bounded incident spool. Quasar.Host owns encryption and external delivery.</summary>
internal static class IncidentCapture
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string directory;
    private static Dictionary<string, string> context;
    private static DateTime window;
    private static int count;
    private static bool started;
    private static string policyPath;
    private const int MaxFiles = 256;

    internal static void Start(string fallbackDirectory)
    {
        lock (Gate)
        {
            if (started) return;
            try
            {
                directory = Path.GetFullPath(Environment.GetEnvironmentVariable("ERROR_REPORTING_DIRECTORY")
                    ?? Path.Combine(fallbackDirectory, "Diagnostics"));
                context = new Dictionary<string, string>(StringComparer.Ordinal);
                string json = Environment.GetEnvironmentVariable("ERROR_REPORTING_CONTEXT");
                if (!string.IsNullOrEmpty(json) && json.Length <= 16384)
                {
                    var supplied = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (supplied != null)
                        foreach (var pair in supplied.Take(32))
                            context[Limit(pair.Key, 80)] = Limit(pair.Value, 1024);
                }
                if (!context.ContainsKey("runId")) context["runId"] = Guid.NewGuid().ToString("N");
                if (!context.ContainsKey("role")) context["role"] = Environment.GetEnvironmentVariable("CLUSTER_NODE_ROLE") ?? "server";
                Directory.CreateDirectory(directory);
#if NETCOREAPP
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#endif
                policyPath = Environment.GetEnvironmentVariable("ERROR_REPORTING_POLICY_FILE");
                Logger.EntryEmitted += OnEntry;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => LogBatchCapture.Flush(directory, policyPath, force: true);
                started = true;
            }
            catch { /* Missing/unwritable local storage must not prevent server startup. */ }
        }
    }

    private static void OnEntry(LogEntry entry)
    {
        if (entry.Level < LogLevel.Error)
        {
            LogBatchCapture.Append(directory, policyPath, entry.PluginName, entry.Level.ToString(), entry.Message);
            return;
        }
        Capture(entry.PluginName, "log", entry.Level.ToString(), entry.Message, entry.Exception, entry.Data);
    }

    private static void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
    {
        LogBatchCapture.Flush(directory, policyPath, force: true);
        Capture("Magnetar", "unhandled_exception", "Critical", "Unhandled process exception",
            args.ExceptionObject as Exception, null, fatal: true);
        // Do not exit or mark anything handled: the runtime owns termination and dump generation.
    }

    internal static void Capture(string source, string kind, string severity, string message, Exception exception,
        object data = null, bool fatal = false)
    {
        // A failing game can log recursively or concurrently. Never wait for another writer.
        if (!Monitor.TryEnter(Gate, fatal ? 100 : 0)) return;
        string temporary = null;
        try
        {
            if (!started) return;
            DateTime now = DateTime.UtcNow;
            if (now - window >= TimeSpan.FromMinutes(1)) { window = now; count = 0; }
            if (!fatal && ++count > 32) return;
            var files = Directory.EnumerateFiles(directory, "*.json")
                .Where(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)).Take(MaxFiles + 1).ToArray();
            if (files.Length >= MaxFiles)
            {
                if (!fatal) return;
                // Reserve fatal evidence even when a storm filled the local spool.
                File.Delete(files.OrderBy(File.GetLastWriteTimeUtc).First());
            }
            var metadata = new Dictionary<string, string>(context, StringComparer.Ordinal);
            if (data != null)
            {
                // Only semantic correlation fields: never serialize arbitrary world/plugin state.
                foreach (string key in new[] { "transferId", "partitionId", "phase", "sourceNode", "targetNode", "reasonCode", "eventCode" })
                {
                    try
                    {
                        var property = data.GetType().GetProperty(key);
                        var value = property?.GetValue(data);
                        if (value is string || value is Guid || value is long || value is int || value is ulong)
                            metadata[key] = Limit(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), 1024);
                    }
                    catch { /* A payload getter must not discard the incident itself. */ }
                }
            }
            string id = Guid.NewGuid().ToString("N");
            var report = new
            {
                SchemaVersion = 1, ReportId = id, CapturedAtUtc = new DateTimeOffset(now),
                Source = Limit(source, 128), Kind = kind, Severity = severity,
                Message = Limit(message, 8192), Exception = Limit(exception?.ToString(), 32768), Context = metadata,
            };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, JsonOptions));
            temporary = Path.Combine(directory, id + ".tmp");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes, 0, bytes.Length);
                file.Flush(true);
            }
            File.Move(temporary, Path.Combine(directory, id + ".json"));
        }
        catch { /* No SDK logging here: diagnostics must not recurse or break the game. */ }
        finally
        {
            if (temporary != null) try { File.Delete(temporary); } catch { }
            Monitor.Exit(Gate);
        }
    }

    private static string Limit(string value, int length) => value == null || value.Length <= length ? value : value.Substring(0, length);
}
