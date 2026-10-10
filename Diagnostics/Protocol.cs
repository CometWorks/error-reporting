using System.Security.Cryptography;
using System.Text.Json;

namespace CometWorks.Diagnostics;

public sealed record IncidentReport(int SchemaVersion, string ReportId, DateTimeOffset CapturedAtUtc,
    string Source, string Kind, string Severity, string Message, string? Exception, Dictionary<string, string> Context);
public sealed record BackofficeKey(string KeyId, string PublicKeyPem, DateTimeOffset ExpiresAtUtc);
public sealed record ReportEnvelope(int SchemaVersion, string ReportId, string KeyId, string WrappedPassword,
    string ArchiveSha256, long ArchiveBytes, bool ContainsDump, long ConsentGeneration);
public sealed record ArchiveFile(string Name, long Bytes, string Sha256);
public sealed record ArchiveManifest(int SchemaVersion, string ReportId, string KeyId, long ConsentGeneration,
    bool ContainsDump, IReadOnlyList<ArchiveFile> Files);

public static class DiagnosticsProtocol
{
    public const int Version = 1;
    public const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const long MaxExpandedBytes = 4L * 1024 * 1024 * 1024;
    public const int MaxEntries = 34;
    public const int MaxJsonBytes = 1024 * 1024;
    public static JsonSerializerOptions Json { get; } = new() { PropertyNameCaseInsensitive = true, MaxDepth = 16 };
    public static bool IsReportId(string? value) => value is { Length: 32 } && Guid.TryParseExact(value, "N", out _)
        && value == value.ToLowerInvariant();
    public static string KeyId(RSA rsa) => Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));

    public static void ValidateReport(IncidentReport report)
    {
        if (report.SchemaVersion != Version || !IsReportId(report.ReportId) || report.CapturedAtUtc == default
            || report.CapturedAtUtc > DateTimeOffset.UtcNow.AddMinutes(10)
            || string.IsNullOrWhiteSpace(report.Source) || report.Source.Length > 128
            || string.IsNullOrWhiteSpace(report.Kind) || report.Kind.Length > 128
            || string.IsNullOrWhiteSpace(report.Severity) || report.Severity.Length > 32
            || report.Message is null || report.Message.Length > 65536 || report.Exception?.Length > 262144
            || report.Context is null || report.Context.Count > 64
            || report.Context.Any(p => p.Key.Length > 128 || p.Value is null || p.Value.Length > 8192))
            throw new InvalidDataException("Invalid incident report.");
    }

    public static void ValidateEnvelope(ReportEnvelope envelope, long maximumBytes = MaxArchiveBytes)
    {
        if (envelope.SchemaVersion != Version || !IsReportId(envelope.ReportId)
            || !IsHash(envelope.KeyId) || !IsHash(envelope.ArchiveSha256)
            || envelope.ArchiveBytes <= 0 || envelope.ArchiveBytes > maximumBytes
            || envelope.ConsentGeneration <= 0 || string.IsNullOrEmpty(envelope.WrappedPassword)
            || envelope.WrappedPassword.Length > 2048)
            throw new InvalidDataException("Invalid report envelope.");
    }

    internal static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool SafeEntry(string? name) => name is { Length: > 0 and <= 100 }
        && name[0] is >= 'a' and <= 'z' && !name.Contains("..", StringComparison.Ordinal)
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');
}
