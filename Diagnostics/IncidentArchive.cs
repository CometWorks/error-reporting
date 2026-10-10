using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ICSharpCode.SharpZipLib.Zip;

namespace CometWorks.Diagnostics;

public static class IncidentArchive
{
    public static async Task<ReportEnvelope> SealAsync(string outputDirectory, IncidentReport report,
        IReadOnlyDictionary<string, string> attachments, BackofficeKey key, long consentGeneration,
        CancellationToken token = default)
    {
        DiagnosticsProtocol.ValidateReport(report);
        if (consentGeneration <= 0 || attachments.Count > DiagnosticsProtocol.MaxEntries - 2
            || attachments.Keys.Any(n => !DiagnosticsProtocol.SafeEntry(n) || n is "report.json" or "manifest.json"))
            throw new InvalidDataException("Invalid archive attachments or consent generation.");
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PublicKeyPem);
        if (rsa.KeySize < 3072 || rsa.KeySize > 8192 || key.KeyId != DiagnosticsProtocol.KeyId(rsa)
            || key.ExpiresAtUtc <= DateTimeOffset.UtcNow || key.ExpiresAtUtc > DateTimeOffset.UtcNow.AddDays(400))
            throw new CryptographicException("Invalid or expired backoffice key.");
        Directory.CreateDirectory(outputDirectory);
        var zipPath = Path.Combine(outputDirectory, report.ReportId + ".zip");
        var envelopePath = Path.Combine(outputDirectory, report.ReportId + ".json");
        var temporary = Path.Combine(outputDirectory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        var envelopeTemporary = temporary + ".json";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var wrapped = Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(password), RSAEncryptionPadding.OaepSHA256));
        var containsDump = attachments.Keys.Any(n => n.EndsWith(".dmp", StringComparison.Ordinal)
            || n.EndsWith(".core", StringComparison.Ordinal));
        var published = false;
        try
        {
            var inventory = new List<ArchiveFile>();
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                PrivateFile(temporary);
                using (var zip = new ZipOutputStream(file) { IsStreamOwner = false, Password = password })
                {
                    zip.SetLevel(3);
                    using var reportStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(report, DiagnosticsProtocol.Json));
                    inventory.Add(await AddEntryAsync(zip, "report.json", reportStream, DiagnosticsProtocol.MaxJsonBytes, token));
                    long remaining = DiagnosticsProtocol.MaxExpandedBytes - inventory[0].Bytes - DiagnosticsProtocol.MaxJsonBytes;
                    foreach (var (name, path) in attachments.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                            throw new InvalidDataException("Attachments must be regular files.");
                        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var entry = await AddEntryAsync(zip, name, input, remaining, token);
                        inventory.Add(entry);
                        remaining -= entry.Bytes;
                    }
                    var manifest = new ArchiveManifest(1, report.ReportId, key.KeyId, consentGeneration, containsDump, inventory);
                    using var manifestStream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(manifest, DiagnosticsProtocol.Json));
                    await AddEntryAsync(zip, "manifest.json", manifestStream, DiagnosticsProtocol.MaxJsonBytes, token);
                    zip.Finish();
                }
                await file.FlushAsync(token);
                file.Flush(true);
            }
            var bytes = new FileInfo(temporary).Length;
            if (bytes > DiagnosticsProtocol.MaxArchiveBytes) throw new InvalidDataException("Archive exceeds limit.");
            await using var hashInput = File.OpenRead(temporary);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(hashInput, token));
            await hashInput.DisposeAsync();
            var envelope = new ReportEnvelope(1, report.ReportId, key.KeyId, wrapped, hash, bytes, containsDump, consentGeneration);
            await WriteDurableJsonAsync(envelopeTemporary, envelope, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, zipPath, false);
            published = true;
            File.Move(envelopeTemporary, envelopePath, false); // Envelope is the completion marker.
            return envelope;
        }
        catch
        {
            if (published) File.Delete(zipPath);
            throw;
        }
        finally
        {
            File.Delete(temporary);
            File.Delete(envelopeTemporary);
        }
    }

    private static async Task<ArchiveFile> AddEntryAsync(ZipOutputStream zip, string name, Stream input, long limit,
        CancellationToken token)
    {
        zip.PutNextEntry(new ZipEntry(name) { AESKeySize = 256, DateTime = new DateTime(2000, 1, 1), ExternalFileAttributes = 0 });
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long bytes = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            bytes += count;
            if (bytes > limit) throw new InvalidDataException("Expanded archive exceeds limit.");
            hash.AppendData(buffer.AsSpan(0, count));
            await zip.WriteAsync(buffer.AsMemory(0, count), token);
        }
        zip.CloseEntry();
        return new ArchiveFile(name, bytes, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    // No extraction: attachment plaintext is streamed through a hash and discarded.
    public static async Task<IncidentReport> ValidateAsync(string archivePath, ReportEnvelope envelope, RSA privateKey,
        long maximumExpandedBytes = DiagnosticsProtocol.MaxExpandedBytes, CancellationToken token = default)
    {
        DiagnosticsProtocol.ValidateEnvelope(envelope);
        if (privateKey.KeySize < 3072 || DiagnosticsProtocol.KeyId(privateKey) != envelope.KeyId)
            throw new CryptographicException("Unknown archive key.");
        await using var file = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != envelope.ArchiveBytes || Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token)) != envelope.ArchiveSha256)
            throw new InvalidDataException("Archive digest mismatch.");
        // Bound directory allocation BEFORE handing an untrusted ZIP to the library. Our <=2 GiB archives
        // never require a ZIP64 central directory (individual expanded entries can still use ZIP64).
        if (file.Length < 42) throw new InvalidDataException("Missing ZIP directory.");
        file.Position = file.Length - 22;
        var footer = new byte[22];
        await file.ReadExactlyAsync(footer, token);
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(footer.AsSpan(10));
        var directoryBytes = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(12));
        var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(16));
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer) != 0x06054b50
            || BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4)) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(footer.AsSpan(8)) != entryCount
            || entryCount < 2 || entryCount > DiagnosticsProtocol.MaxEntries
            || BinaryPrimitives.ReadUInt16LittleEndian(footer.AsSpan(20)) != 0
            || (long)directoryOffset + directoryBytes != file.Length - 22)
            throw new InvalidDataException("Unsupported or excessive ZIP directory.");
        // SharpZipLib honors a ZIP64 locator even when the ordinary footer does not request ZIP64.
        file.Position = file.Length - 42;
        var locator = new byte[4];
        await file.ReadExactlyAsync(locator, token);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == 0x07064b50)
            throw new InvalidDataException("ZIP64 central directories are not supported.");
        file.Position = 0;
        var passwordBytes = privateKey.Decrypt(Convert.FromBase64String(envelope.WrappedPassword), RSAEncryptionPadding.OaepSHA256);
        if (passwordBytes.Length != 44) throw new CryptographicException("Invalid archive password.");
        using var zip = new ZipFile(file) { IsStreamOwner = false, Password = Encoding.UTF8.GetString(passwordBytes) };
        CryptographicOperations.ZeroMemory(passwordBytes);
        if (zip.Count < 2 || zip.Count > DiagnosticsProtocol.MaxEntries) throw new InvalidDataException("Invalid entry count.");
        var actual = new Dictionary<string, ArchiveFile>(StringComparer.Ordinal);
        IncidentReport? report = null;
        ArchiveManifest? manifest = null;
        long expanded = 0;
        foreach (ZipEntry entry in zip)
        {
            token.ThrowIfCancellationRequested();
            var unixType = (entry.ExternalFileAttributes >> 16) & 0xF000;
            if (!DiagnosticsProtocol.SafeEntry(entry.Name) || !entry.IsFile || entry.AESKeySize != 256 || !entry.IsCrypted
                || entry.Size < 0 || (entry.ExternalFileAttributes & 0x410) != 0
                || unixType is not (0 or 0x8000) || actual.ContainsKey(entry.Name))
                throw new InvalidDataException("Unsafe, unencrypted, duplicate or unsupported archive entry.");
            if (entry.Size > maximumExpandedBytes - expanded) throw new InvalidDataException("Expanded archive exceeds limit.");
            var isJson = entry.Name is "report.json" or "manifest.json";
            if (isJson && entry.Size > DiagnosticsProtocol.MaxJsonBytes) throw new InvalidDataException("JSON entry exceeds limit.");
            using var plaintext = isJson ? new MemoryStream() : null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var input = zip.GetInputStream(entry);
            var buffer = new byte[65536];
            long length = 0;
            int count;
            while ((count = await input.ReadAsync(buffer, token)) > 0)
            {
                length += count;
                expanded += count;
                if (expanded > maximumExpandedBytes || length > entry.Size || (isJson && length > DiagnosticsProtocol.MaxJsonBytes))
                    throw new InvalidDataException("Expanded archive exceeds limit.");
                hash.AppendData(buffer.AsSpan(0, count));
                if (plaintext is not null) await plaintext.WriteAsync(buffer.AsMemory(0, count), token);
            }
            if (length != entry.Size) throw new InvalidDataException("Entry length mismatch.");
            actual.Add(entry.Name, new ArchiveFile(entry.Name, length, Convert.ToHexStringLower(hash.GetHashAndReset())));
            if (entry.Name == "report.json") report = JsonSerializer.Deserialize<IncidentReport>(plaintext!.ToArray(), DiagnosticsProtocol.Json);
            if (entry.Name == "manifest.json") manifest = JsonSerializer.Deserialize<ArchiveManifest>(plaintext!.ToArray(), DiagnosticsProtocol.Json);
        }
        if (report is null || manifest is null || manifest.Files is null || manifest.SchemaVersion != 1
            || manifest.ReportId != envelope.ReportId || report.ReportId != envelope.ReportId
            || manifest.KeyId != envelope.KeyId || manifest.ConsentGeneration != envelope.ConsentGeneration
            || manifest.ContainsDump != envelope.ContainsDump || manifest.Files.Count != actual.Count - 1
            || manifest.Files.Any(f => f is null || !DiagnosticsProtocol.SafeEntry(f.Name) || f.Bytes < 0 || !DiagnosticsProtocol.IsHash(f.Sha256))
            || manifest.Files.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count
            || manifest.Files.Any(f => f.Name == "manifest.json" || !actual.TryGetValue(f.Name, out var found) || found != f)
            || envelope.ContainsDump != actual.Keys.Any(n => n.EndsWith(".dmp", StringComparison.Ordinal) || n.EndsWith(".core", StringComparison.Ordinal)))
            throw new InvalidDataException("Archive inventory mismatch.");
        DiagnosticsProtocol.ValidateReport(report);
        return report;
    }

    public static async Task WriteDurableJsonAsync<T>(string path, T value, CancellationToken token = default)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        PrivateFile(path);
        await JsonSerializer.SerializeAsync(file, value, DiagnosticsProtocol.Json, token);
        await file.FlushAsync(token);
        file.Flush(true);
    }

    private static void PrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
