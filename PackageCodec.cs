using System.Globalization;
using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace BioStarExporter;

public sealed record CsvSource(string Name, string[] Headers, Func<CancellationToken, IAsyncEnumerable<object?[]>> Rows);
public sealed record FileSummary(string Name, long SizeBytes, long RecordCount, string Sha256);

public static class PackageCodec
{
    public static readonly string[] Names = ["users.csv", "fingerprints.csv", "cards.csv", "user_cards.csv"];
    // ponytail: review builds cap output at 1 GiB, 100k rows/file, 2 MiB/field; raise only after importer benchmarks.
    public const long MaxBytes = 1024L * 1024 * 1024;
    public const int MaxRows = 100_000;
    public const int MaxFieldBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static string EncodeField(object? value)
    {
        string text;
        if (value is null or DBNull) text = "\\N";
        else
        {
            text = value switch
            {
                byte[] b => Convert.ToBase64String(b),
                DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                DateTimeOffset d => d.ToString("o", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture) ?? "",
                _ => value.ToString() ?? ""
            };
            text = text.Replace("\\", "\\\\");
        }
        text = "\"" + text.Replace("\"", "\"\"") + "\"";
        if (Utf8.GetByteCount(text) > MaxFieldBytes) throw new InvalidDataException("Field melampaui batas 2 MiB.");
        return text;
    }

    public static async Task WriteAsync(string destination, IReadOnlyList<CsvSource> sources,
        object metadata, IProgress<string>? progress, CancellationToken ct)
    {
        if (sources.Count != 4 || !sources.Select(s => s.Name).Order().SequenceEqual(Names.Order()))
            throw new InvalidDataException("Paket wajib mempunyai empat CSV dengan nama tetap.");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("File tujuan sudah ada; tidak ditimpa.");
        string parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Folder tujuan tidak tersedia.");
        string partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var files = new List<FileSummary>();
        long total = 0;
        ct.ThrowIfCancellationRequested();
        try
        {
            // Create with private ACL before any biometric byte is written; the ACL survives rename.
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            await using (var file = new FileInfo(partial).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 65536, FileOptions.Asynchronous, security))
            {
                using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Utf8);
                foreach (var source in sources)
                {
                    if (source.Headers.Length == 0 || source.Headers.Distinct(StringComparer.Ordinal).Count() != source.Headers.Length)
                        throw new InvalidDataException("Header tidak valid.");
                    progress?.Report($"Menulis {source.Name}");
                    var entry = zip.CreateEntry(source.Name, CompressionLevel.Optimal);
                    await using var stream = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long size = 0, rows = 0;
                    async Task WriteRow(object?[] values)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (values.Length != source.Headers.Length) throw new InvalidDataException("Jumlah field berbeda dari header.");
                        var bytes = Utf8.GetBytes(string.Join(',', values.Select(EncodeField)) + "\r\n");
                        total = checked(total + bytes.Length);
                        if (total > MaxBytes) throw new InvalidDataException("Paket melampaui batas 1 GiB dekompresi.");
                        await stream.WriteAsync(bytes, ct);
                        hash.AppendData(bytes);
                        size += bytes.Length;
                    }
                    await WriteRow(source.Headers.Cast<object?>().ToArray());
                    await foreach (var row in source.Rows(ct).WithCancellation(ct))
                    {
                        if (++rows > MaxRows) throw new InvalidDataException("CSV melampaui 100.000 record.");
                        await WriteRow(row);
                        if (rows % 100 == 0) progress?.Report($"{source.Name}: {rows:N0} record");
                    }
                    files.Add(new(source.Name, size, rows, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                }
                var manifest = new
                {
                    format_version = 1,
                    schema_contract_id = "biostar-source-review-v3",
                    production_import_approved = false,
                    package_id = Guid.NewGuid(),
                    exported_at_utc = DateTimeOffset.UtcNow,
                    exporter_version = "0.1.0",
                    export_context = metadata,
                    files,
                    totals = files.ToDictionary(f => Path.GetFileNameWithoutExtension(f.Name), f => f.RecordCount)
                };
                var json = JsonSerializer.SerializeToUtf8Bytes(manifest, Json);
                if (json.Length > 4 * 1024 * 1024) throw new InvalidDataException("Manifest melampaui 4 MiB.");
                await using var manifestStream = zip.CreateEntry("manifest.json").Open();
                await manifestStream.WriteAsync(json, ct);
            }
            progress?.Report("Memverifikasi ZIP dan checksum…");
            await VerifyAsync(partial, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: false);
        }
        catch
        {
            try { File.Delete(partial); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { throw new IOException($"Ekspor gagal. File sensitif parsial gagal dibersihkan: {partial}"); }
            throw;
        }
    }

    // This verifies our own bounded output; it is not a general-purpose untrusted-upload validator.
    public static async Task VerifyAsync(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        var expected = Names.Append("manifest.json").Order().ToArray();
        if (!zip.Entries.Select(e => e.FullName).Order().SequenceEqual(expected))
            throw new InvalidDataException("Struktur ZIP tidak sesuai.");
        var manifestEntry = zip.GetEntry("manifest.json")!;
        if (manifestEntry.Length > 4 * 1024 * 1024) throw new InvalidDataException("Manifest terlalu besar.");
        using var manifestStream = manifestEntry.Open();
        using var doc = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
        var summaries = doc.RootElement.GetProperty("files").EnumerateArray().ToArray();
        if (!summaries.Select(f => f.GetProperty("name").GetString()).Order().SequenceEqual(Names.Order()))
            throw new InvalidDataException("Daftar manifest tidak sesuai.");
        long total = 0;
        foreach (var summary in summaries)
        {
            string name = summary.GetProperty("name").GetString()!;
            var entry = zip.GetEntry(name)!;
            long expectedSize = summary.GetProperty("size_bytes").GetInt64();
            long count = summary.GetProperty("record_count").GetInt64();
            if (expectedSize < 0 || expectedSize > MaxBytes || count < 0 || count > MaxRows || entry.Length != expectedSize)
                throw new InvalidDataException("Ukuran/count ZIP tidak sesuai.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var input = entry.Open();
            var buffer = new byte[65536];
            long size = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                size += read;
                total += read;
                if (size > expectedSize || total > MaxBytes) throw new InvalidDataException("Ukuran ZIP melampaui batas.");
                hash.AppendData(buffer, 0, read);
            }
            if (size != expectedSize || !Convert.ToHexString(hash.GetHashAndReset()).Equals(summary.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Checksum CSV tidak sesuai.");
        }
    }
}
