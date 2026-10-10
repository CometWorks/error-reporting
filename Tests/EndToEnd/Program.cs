using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using CometWorks.Diagnostics;
using CometWorks.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PluginSdk.Logging;
using Quasar.Diagnostics;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.ServerList;
using Magnetar.Protocol.Runtime;
using ServerPlugin;
using HostContract = Quasar.Host.Contract.V1;
#if SERVER_LIST_CONTRACT
using CometWorks.ServerList;
#endif

// A separate process gives each actual plugin capture instance its own static lifecycle and context.
if (args is ["--emit-plugin", var emissionDirectory, var transfer])
{
    IncidentCapture.Start(emissionDirectory);
    var logger = new Logger("cluster", new NullSink());
    logger.Info("Fixture ordinary operational line " + transfer);
    logger.Error("Fixture handover failure", new InvalidOperationException("fixture"),
        new { phase = "restore", transferId = transfer });
    LogBatchCapture.Flush(emissionDirectory, Environment.GetEnvironmentVariable("ERROR_REPORTING_POLICY_FILE")!, force: true);
    return;
}
if (args.Length is not (3 or 4)) throw new ArgumentException("Usage: EndToEnd HOST_DLL BACKEND_DLL PRIVATE_WORK_DIRECTORY [SERVER_LIST_DLL]");
string root = Path.GetFullPath(args[2]);
Directory.CreateDirectory(root); DiagnosticCollector.PrivateDirectory(root);
int backendPort = Port(), directoryPort = Port();
using var consent = new DataHandlingConsentCatalog(NullLogger<DataHandlingConsentCatalog>.Instance, Path.Combine(root, "consent.json"));
// No network enrollment is permitted until diagnostic consent.

var credentials = new ClusterCredentialStore(new EphemeralDataProtectionProvider(), Path.Combine(root, "credentials.json"));
var hosts = new ClusterHostCatalog(credentials, Path.Combine(root, "hosts"));
var fixtures = new List<HostFixture>();
foreach (string id in new[] { "host-a", "host-b" })
{
    int port = Port();
    var enrolled = await hosts.RegisterAsync(id, "Fixture " + id, "127.0.0.1", port, default);
    string state = Path.Combine(root, id, "state"), config = Path.Combine(root, id, "host.json");
    DiagnosticCollector.PrivateDirectory(Path.GetDirectoryName(config)!);
    await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new { executorId = id, hostId = id, pollIntervalSeconds = 1,
        attachments = Array.Empty<object>(), stateDirectory = state,
        diagnosticsDirectory = Path.Combine(root, "large disk", id),
        command = new { url = $"http://127.0.0.1:{port}", tokenEnvironmentVariable = "DIAGNOSTIC_FIXTURE_HOST_TOKEN" } }));
    fixtures.Add(new HostFixture(enrolled, state, config, credentials.Resolve(enrolled.CredentialReference)!));
}
Check(fixtures[0].Secret != fixtures[1].Secret, "Hosts have distinct random credentials");
using var routing = new LoopbackHostHandler(fixtures.ToDictionary(f => f.Host.Id, f => f.Host.CommandPort));
using var hostHttp = new HttpClient(routing) { Timeout = TimeSpan.FromSeconds(10) };
var hostClient = new ClusterHostClient(hostHttp, credentials);
var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Quasar:Diagnostics:BackofficeUrl"] = $"http://127.0.0.1:{backendPort}/",
    ["Quasar:Diagnostics:StorageDirectory"] = Path.Combine(root, "large disk", "quasar"),
}).Build();
using var backofficeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var localCollector = new DiagnosticCollector(Path.Combine(root, "large disk", "quasar"), "quasar", Path.Combine(root, "quasar"));
var quasarCapture = new QuasarDiagnosticLoggerProvider(localCollector, TimeProvider.System, subscribe: false);
using var captureLoggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace).AddProvider(quasarCapture));
using var uplink = new DiagnosticsUplinkService(consent, hosts, hostClient, backofficeHttp, settings,
    captureLoggers.CreateLogger<DiagnosticsUplinkService>(), Path.Combine(root, "quasar", "Diagnostics"), localCollector);
Check(uplink.SpoolDirectory == Path.Combine(root, "large disk", "quasar", "outbound")
    && File.Exists(Path.Combine(root, "quasar", "Diagnostics", "installation-id")), "Quasar bulk queue uses its custom path while identity stays in stable state");
string keyDirectory = Path.Combine(root, "keys"); await KeyRing.GenerateAsync(keyDirectory);
using var keys = new KeyRing(keyDirectory);
Process StartBackend() => Launch(args[1], [], new()
{
    ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{backendPort}",
    ["Diagnostics__DataDirectory"] = Path.Combine(root, "backend"), ["Diagnostics__KeyDirectory"] = keyDirectory,
    ["Diagnostics__GitHubClientId"] = "fixture-unused", ["Diagnostics__GitHubClientSecret"] = "fixture-unused",
    ["Diagnostics__DirectoryUrl"] = $"http://127.0.0.1:{directoryPort}/",
    ["Diagnostics__Analysis__Enabled"] = "false", ["Diagnostics__Analysis__PublishIssues"] = "false",
});
void StartHost(HostFixture fixture) => fixture.Process = Launch(args[0], ["run", "--config", fixture.ConfigPath], new()
    { ["DIAGNOSTIC_FIXTURE_HOST_TOKEN"] = fixture.Secret });
Process? backend = null, directory = null;
#if SERVER_LIST_CONTRACT
if (args.Length != 4) throw new ArgumentException("Pass SERVER_LIST_DLL when building with ServerListRepo.");
var cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
cache.SetValue(null, Path.Combine(root, "quasar"));
using var rbac = new RbacConfigCatalog(NullLogger<RbacConfigCatalog>.Instance);
await rbac.SaveAsync(new() { SubjectRoleMappings = [new() { Provider = QuasarAuthSchemes.Steam,
    Subject = "76561198000000001", Roles = [QuasarRoles.Admin] }] });
string listingId = Guid.NewGuid().ToString("N");
using var publisher = new ServerListPublisher(new() { Enabled = true }, consent, rbac, backofficeHttp,
    _ => Task.FromResult<IReadOnlyList<ServerListListing>>([new(listingId, "Diagnostic fixture", "play.example.org", 27016,
        DateTimeOffset.UtcNow, true, 0, 16, 1f)]), Path.Combine(root, "quasar"), NullLogger<ServerListPublisher>.Instance, uplink);
using var directoryHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{directoryPort}"), Timeout = TimeSpan.FromSeconds(5) };
async Task<bool> Visible()
{ using var response = JsonDocument.Parse(await directoryHttp.GetStringAsync("/api/servers")); return response.RootElement.GetArrayLength() == 1; }
#endif
try
{
#if SERVER_LIST_CONTRACT
    directory = Launch(args[3], [], new()
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Development", ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{directoryPort}",
        ["Directory__PublicUrl"] = $"http://127.0.0.1:{directoryPort}", ["Directory__DataDirectory"] = Path.Combine(root, "directory"),
        ["Directory__DiagnosticsUrl"] = $"http://127.0.0.1:{backendPort}",
    });
    await Ready($"http://127.0.0.1:{directoryPort}/health");
#endif
    backend = StartBackend();
    foreach (var fixture in fixtures) StartHost(fixture);
    await Ready($"http://127.0.0.1:{backendPort}/health");
    foreach (var fixture in fixtures) await HostReady(fixture);
    await uplink.PollOnceAsync(default);
    Check(!File.Exists(Path.Combine(uplink.StateDirectory, "machine-key.pem")), "consent-off Quasar never enrolls");
    await consent.SaveAsync(true, true, true);
    await uplink.PollOnceAsync(default);
    await consent.SaveAsync(false); // Disable statistics independently after the fresh YES enrollment.
    Check(File.Exists(Path.Combine(uplink.StateDirectory, "machine-key.pem")), "automatic Quasar enrollment needs no configured ID/token");
    // The normal uplink debug log is consent-gated and naturally batched. No synthetic heartbeat or crash is needed.
    await Task.Delay(TimeSpan.FromSeconds(31));
    await uplink.PollOnceAsync(default);
    async Task<HttpResponseMessage> VerifyDelivery(long generation)
    {
        using var request = await uplink.CreateListingRequestAsync(HttpMethod.Get, "v1/quasar/challenge", default)
            ?? throw new InvalidOperationException("Missing negotiated credential.");
        request.Headers.Add("X-Diagnostics-Generation", generation.ToString());
        return await backofficeHttp.SendAsync(request);
    }
    using (var receipt = await VerifyDelivery(consent.GetSettings().Generation))
        Check(receipt.IsSuccessStatusCode, "quiet Quasar's actual encrypted logs grant delivery eligibility");
#if SERVER_LIST_CONTRACT
    await publisher.PublishOnceAsync(default);
    Check(!await Visible(), "diagnostic consent alone does not list a community server");
    await consent.SaveListingVisibilityAsync(true);
    Check(consent.GetSettings().ConsentGranted == false, "listing opt-in does not grant statistics sharing");
    await publisher.PublishOnceAsync(default);
    await Until(Visible, "Signed backoffice update did not activate listing");
    Check((await directoryHttp.GetStringAsync($"/api/servers/{listingId}/join")).Contains("steam://connect/play.example.org:27016"),
        "real website accepts signed service update and permits join without consuming a field proof");
    await consent.SaveListingVisibilityAsync(false);
    await publisher.PublishOnceAsync(default);
    await Until(async () => !await Visible(), "Visibility withdrawal did not push");
    Check(consent.GetSettings().DiagnosticsGranted, "visibility-only withdrawal preserves diagnostic reporting");
    await consent.SaveListingVisibilityAsync(true); await publisher.PublishOnceAsync(default);
    await Until(Visible, "Listing did not recover after visibility opt-in");
#endif
    foreach (var fixture in fixtures)
        Check(fixture.Collector().CurrentPolicy is not null,
            fixture.Host.Id + " received its authenticated sharing lease");
    using (var wrong = new HttpRequestMessage(HttpMethod.Get, fixtures[0].Host.CommandUrl + HostContract.HostProtocol.RoutePrefix + "/diagnostics/reports"))
    {
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixtures[1].Secret);
        using var denied = await hostHttp.SendAsync(wrong);
        Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Host B credential cannot authorize Host A");
    }

    // Both real Hosts continue collecting while the backoffice is down. Quasar must durably own each retry.
    await Stop(backend); backend.Dispose(); backend = null;
#if SERVER_LIST_CONTRACT
    await Task.Delay(TimeSpan.FromSeconds(61));
    Check(!await Visible(), "backoffice outage expires the website local lease without evidence queries");
#endif
    var emitted = new Dictionary<string, string>();
    var batches = new Dictionary<string, string>();
    foreach (var fixture in fixtures)
    {
        string id = await Emit(fixture, "transfer-" + fixture.Host.Id);
        emitted[fixture.Host.Id] = id;
        batches[fixture.Host.Id] = fixture.LastBatchId!;
        await Until(async () => (await hostClient.GetDiagnosticsReportsAsync(fixture.Host, default)).Any(e => e.ReportId == id),
            fixture.Host.Id + " did not seal its plugin report");
        await Until(async () => (await hostClient.GetDiagnosticsReportsAsync(fixture.Host, default)).Any(e => e.ReportId == fixture.LastBatchId),
            fixture.Host.Id + " did not seal its ordinary log batch");
    }
    try { await uplink.PollOnceAsync(default); }
    catch (HttpRequestException) { /* Expected outage; BackgroundService normally catches this and retries. */ }
    foreach (var fixture in fixtures)
    {
        string id = emitted[fixture.Host.Id];
        Check(File.Exists(Path.Combine(uplink.SpoolDirectory, id + ".json")) && File.Exists(Path.Combine(uplink.SpoolDirectory, id + ".zip")),
            fixture.Host.Id + " report survived backend outage in durable Quasar spool");
        Check(!(await hostClient.GetDiagnosticsReportsAsync(fixture.Host, default)).Any(e => e.ReportId == id),
            fixture.Host.Id + " source acknowledged only after durable handover");
    }
    backend = StartBackend(); await Ready($"http://127.0.0.1:{backendPort}/health");
    await Until(async () => { await uplink.PollOnceAsync(default); return emitted.Values.Concat(batches.Values).All(id => File.Exists(RecordPath(id))); }, "Backoffice retry did not recover");
    using (var available = await VerifyDelivery(consent.GetSettings().Generation))
        Check(available.IsSuccessStatusCode, "newer reports from both Hosts preserve Quasar listing attestation");
    using (var restartedUplink = new DiagnosticsUplinkService(consent, hosts, hostClient, backofficeHttp, settings,
        captureLoggers.CreateLogger<DiagnosticsUplinkService>(), uplink.StateDirectory, localCollector))
    {
        await restartedUplink.PollOnceAsync(default);
        Check(restartedUplink.InstallationId == uplink.InstallationId && restartedUplink.CurrentPolicy is not null,
            "Quasar restart re-enrolls automatically with its persisted machine key");
    }
#if SERVER_LIST_CONTRACT
    await publisher.PublishOnceAsync(default);
    await Until(Visible, "Listing did not recover after backend restart");
    Check(await Visible(), "backend restart preserves enrollment and advances signed feed epoch");
#endif
    foreach (var fixture in fixtures)
    {
        string id = emitted[fixture.Host.Id];
        var stored = JsonSerializer.Deserialize<StoredReport>(await File.ReadAllTextAsync(RecordPath(id)), DiagnosticsProtocol.Json)!;
        var report = await IncidentArchive.ValidateAsync(Path.Combine(Path.GetDirectoryName(RecordPath(id))!, "archive.zip"), stored.Envelope, keys.Find(stored.Envelope.KeyId)!);
        Check(report.Context["hostId"] == fixture.Host.Id && report.Context["nodeId"] == "node-" + fixture.Host.Id
            && report.Context["clusterId"] == "fixture-cluster" && report.Context["phase"] == "restore"
            && report.Context["transferId"] == "transfer-" + fixture.Host.Id && report.Context["installationId"] == uplink.InstallationId,
            fixture.Host.Id + " retains distinct trusted Host/node/run/transfer correlation");
        Check(!File.Exists(Path.Combine(uplink.SpoolDirectory, id + ".zip")), fixture.Host.Id + " backoffice acknowledgment clears local retry");
        var batch = JsonSerializer.Deserialize<StoredReport>(File.ReadAllText(RecordPath(batches[fixture.Host.Id])), DiagnosticsProtocol.Json)!;
        var batchReport = await IncidentArchive.ValidateAsync(Path.Combine(Path.GetDirectoryName(RecordPath(batches[fixture.Host.Id]))!, "archive.zip"), batch.Envelope, keys.Find(batch.Envelope.KeyId)!);
        Check(batchReport.Kind == "log_batch" && batchReport.Message.Contains("Fixture ordinary operational line")
            && batchReport.Context["hostId"] == fixture.Host.Id && batch.Summary.Kind == "log_batch",
            fixture.Host.Id + " ordinary log batch reaches encrypted storage without losing its category");
    }
    var beforeRetry = emitted.Values.ToDictionary(id => id, id => File.ReadAllText(RecordPath(id)));
    await uplink.PollOnceAsync(default);
    Check(beforeRetry.All(p => File.ReadAllText(RecordPath(p.Key)) == p.Value), "repeat delivery cycle does not rewrite acknowledged incidents");
    Check(routing.RoutedHosts.Order().SequenceEqual(new[] { "host-a", "host-b" }), "each enrolled identity reached its own real Host endpoint");

    // Demonstrate lease expiry without waiting two minutes, through the real authenticated policy endpoint.
    var secondHost = fixtures[1];
    var shortLease = uplink.CurrentPolicy! with { ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(1) };
    await hostClient.SetDiagnosticsPolicyAsync(secondHost.Host, shortLease, default);
    await Task.Delay(1250);
    Check(secondHost.Collector().CurrentPolicy is null
        && (await hostClient.GetDiagnosticsReportsAsync(secondHost.Host, default)).Length == 0,
        "Host B independently stops sharing when its lease expires");
    await uplink.PollOnceAsync(default);

    // Preserve the real native fixture check; no game process or global OS crash policy is touched.
    if (OperatingSystem.IsLinux())
    {
        var firstHost = fixtures[0];
        var capture = firstHost.Collector();
        var start = new ProcessStartInfo("/bin/sleep") { ArgumentList = { "30" } };
        string native = capture.Configure(start, new() { ["role"] = "native-fixture" }, native: true)!;
        using var child = Process.Start(start)!; capture.Started(native, child);
        using (var signal = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-SEGV", child.Id.ToString() } })!) await signal.WaitForExitAsync();
        await child.WaitForExitAsync(); DiagnosticCollector.Exited(native, child.ExitCode, false);
        for (int n = 0; n < 24 && !File.Exists(Path.Combine(native, "native.dmp")); n++) await Task.Delay(500);
        if (File.Exists(Path.Combine(native, "native.dmp")))
        {
            string nativeId = JsonSerializer.Deserialize<IncidentReport>(await File.ReadAllTextAsync(Path.Combine(native, "exit.json")), DiagnosticsProtocol.Json)!.ReportId;
            await Until(async () => { await uplink.PollOnceAsync(default); return File.Exists(RecordPath(nativeId)); }, "Native dump upload did not complete");
            Check(JsonSerializer.Deserialize<StoredReport>(File.ReadAllText(RecordPath(nativeId)), DiagnosticsProtocol.Json)!.Envelope.ContainsDump,
                "real SIGSEGV core was captured, encrypted and received");
        }
        else Console.WriteLine("NATIVE LIMITATION: SIGSEGV exercised, but this account/OS did not provide a readable core.");
    }

    string revokedId = await Emit(secondHost, "transfer-revoked");
    await Until(async () => (await hostClient.GetDiagnosticsReportsAsync(secondHost.Host, default)).Any(e => e.ReportId == revokedId), "Revocation fixture was not sealed");
    await Stop(secondHost.Process!); secondHost.Process!.Dispose(); secondHost.Process = null;
    await consent.SaveAsync(false, false, false);
    await uplink.PollOnceAsync(default);
    using (var withdrawn = await VerifyDelivery(consent.GetSettings().Generation))
        Check(withdrawn.StatusCode == HttpStatusCode.Forbidden, "withdrawn consent generation cannot use earlier logging evidence");
#if SERVER_LIST_CONTRACT
    await publisher.PublishOnceAsync(default);
    await Until(async () => !await Visible(), "Consent withdrawal did not hide listing");
    Check(!await Visible(), "diagnostic withdrawal pushes website visibility removal");
#endif
    Check(!File.Exists(RecordPath(revokedId)) && Directory.GetFiles(uplink.SpoolDirectory).Length == 0,
        "offline Host report is not uploaded after revocation");
    StartHost(secondHost); await HostReady(secondHost);
    await uplink.PollOnceAsync(default);
    foreach (var fixture in fixtures)
    {
        var policy = JsonSerializer.Deserialize<HostContract.HostDiagnosticPolicy>(await File.ReadAllTextAsync(Path.Combine(fixture.State, "diagnostics", "policy.json")), DiagnosticsProtocol.Json)!;
        Check(!policy.DiagnosticsGranted && !policy.DumpsGranted && policy.Generation == consent.GetSettings().Generation
            && (await hostClient.GetDiagnosticsReportsAsync(fixture.Host, default)).Length == 0,
            fixture.Host.Id + " reconnect applies current revocation and purges sharing queue");
    }
    Check(!File.Exists(RecordPath(revokedId)), "reconnected Host cannot replay a revoked archive");
    Console.WriteLine("MULTI-HOST END-TO-END PASSED: two real authenticated Hosts, plugin capture, distinct routing/correlation, backend outage/retry, lease expiry and offline revocation/reconnect.");
}
catch (Exception exception)
{
    // Report only the failure type: process startup/transport exception text can contain configuration.
    Console.Error.WriteLine("END-TO-END FAILED: " + exception.GetType().Name);
    Environment.ExitCode = 1;
}
finally
{
    if (directory is not null) { await Stop(directory); directory.Dispose(); }
    foreach (var fixture in fixtures) if (fixture.Process is { } process) { await Stop(process); process.Dispose(); }
    if (backend is not null) { await Stop(backend); backend.Dispose(); }
}
string RecordPath(string id) => Path.Combine(root, "backend", "reports", id, "record.json");
async Task<string> Emit(HostFixture fixture, string transferId)
{
    var capture = fixture.Collector();
    var emissionStart = new ProcessStartInfo(Environment.ProcessPath!);
    string run = capture.Configure(emissionStart, new() { ["role"] = "dedicated-server", ["nodeId"] = "node-" + fixture.Host.Id, ["clusterId"] = "fixture-cluster" })!;
    using (var self = Process.GetCurrentProcess()) capture.Started(run, self);
    using var child = Launch(Assembly.GetExecutingAssembly().Location, ["--emit-plugin", run, transferId],
        emissionStart.Environment.Where(p => p.Key.StartsWith("ERROR_REPORTING_", StringComparison.Ordinal) && p.Value is not null).ToDictionary(p => p.Key, p => p.Value!));
    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    if (child.ExitCode != 0) throw new Exception("Plugin fixture failed.");
    var reports = Directory.EnumerateFiles(run, "*.json").Where(p => Path.GetFileName(p) != "run.json")
        .Select(p => JsonSerializer.Deserialize<IncidentReport>(File.ReadAllText(p), DiagnosticsProtocol.Json))
        .OfType<IncidentReport>().ToArray();
    fixture.LastBatchId = reports.Single(r => r.Kind == "log_batch").ReportId;
    return reports.Single(r => r.Kind == "log" && r.Context.GetValueOrDefault("transferId") == transferId).ReportId;
}
async Task HostReady(HostFixture fixture) => await Until(async () =>
{
    if (fixture.Process!.HasExited) throw new InvalidOperationException("Host fixture exited before readiness.");
    try { _ = await hostClient.GetDiagnosticsReportsAsync(fixture.Host, default); return true; }
    catch (HttpRequestException) { return false; }
    catch (ClusterHostException error) when (error.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout) { return false; }
}, fixture.Host.Id + " did not become ready");
static void Check(bool success, string name) { if (!success) throw new Exception("FAILED: " + name); Console.WriteLine("PASS " + name); }
static int Port() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
static Process Launch(string file, string[] arguments, Dictionary<string, string> environment)
{
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(Path.GetFullPath(file));
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
    var process = Process.Start(start)!;
    // Consume output without echoing configuration or secrets from startup libraries.
    _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
    return process;
}
static async Task Until(Func<Task<bool>> condition, string failure)
{
    for (int attempt = 0; attempt < 80; attempt++) { if (await condition()) return; await Task.Delay(250); }
    throw new Exception(failure);
}
static async Task Ready(string url)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    await Until(async () => { try { return (await http.GetAsync(url)).IsSuccessStatusCode; } catch (HttpRequestException) { return false; } }, "Backend did not start");
}
static async Task Stop(Process process) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
sealed class NullSink : ILogSink { public void Write(in LogEntry entry) { } }
sealed class HostFixture(EnrolledClusterHost host, string state, string configPath, string secret)
{
    public EnrolledClusterHost Host { get; } = host;
    public string State { get; } = state;
    public string ConfigPath { get; } = configPath;
    public string Secret { get; } = secret;
    public Process? Process { get; set; }
    public string? LastBatchId { get; set; }
    public DiagnosticCollector Collector() => DiagnosticStorage.Create(Path.Combine(State, "diagnostics"), Host.Id,
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(State))!, "large disk", Host.Id));
}
sealed class LoopbackHostHandler(IReadOnlyDictionary<string, int> routes) : DelegatingHandler(new HttpClientHandler())
{
    private readonly ConcurrentDictionary<string, bool> routed = new();
    public IEnumerable<string> RoutedHosts => routed.Keys;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var uri = request.RequestUri!;
        string id = uri.Host.Split('.')[0];
        if (uri.Host != id + ".quasar-host.invalid" || !routes.TryGetValue(id, out int port) || uri.Port != port)
            throw new InvalidOperationException("Unexpected Host fixture route.");
        routed[id] = true;
        request.RequestUri = new UriBuilder(uri) { Host = "127.0.0.1", Port = port }.Uri;
        return base.SendAsync(request, token);
    }
}
