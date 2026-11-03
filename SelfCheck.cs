using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace BioStarExporter;

public static class SelfCheck
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Self-check failed: " + message); }
    private static async IAsyncEnumerable<object?[]> Data(object?[] row, [EnumeratorCancellation] CancellationToken ct)
    { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield return row; }
    private static string? Decode(string s)
    {
        if (s == "\\N") return null;
        var result = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && (++i == s.Length || s[i] != '\\')) throw new InvalidDataException("Escape invalid");
            result.Append(s[i]);
        }
        return result.ToString();
    }
    public static async Task RunAsync()
    {
        Check(!BioStarSource.UserColumns.Contains("USRIDORD"), "trigger-generated column excluded");
        Check(BioStarSource.UserColumns.Contains("IDXUSRID") && BioStarSource.UserColumns.Contains("IDXUSRIDNUM"), "user index columns retained");
        Check(BioStarSource.UserColumns.Contains("NM") && BioStarSource.UserColumns.Contains("DEPARTMENT"), "user profile retained");
        var f = new UserFilter(Start: new DateTime(2026, 11, 3), End: new DateTime(2026, 11, 3));
        Check(f.EndExclusive() == new DateTime(2026, 11, 4), "inclusive end date");
        Check(new UserFilter().EndExclusive() is null, "optional end");
        try { new UserFilter(Start: new DateTime(2026, 11, 4), End: new DateTime(2026, 11, 3)).EndExclusive(); throw new Exception("Invalid range accepted"); }
        catch (InvalidOperationException) { }
        try { new UserFilter(End: DateTime.MaxValue).EndExclusive(); throw new Exception("Overflow accepted"); }
        catch (InvalidOperationException) { }
        string folder = Path.Combine(Path.GetTempPath(), "biostar-selfcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "test.zip");
            byte[] binary = [0, 1, 2, 255, 128];
            object?[] row = ["0003", "Koma, kutip\"\r\nNama 日本", null, "", "\\N", "a\\b", binary, new DateTime(2026, 11, 3).AddTicks(1234567)];
            var sources = PackageCodec.Names.Select(n => new CsvSource(n, Enumerable.Range(0, row.Length).Select(i => "c" + i).ToArray(), ct => Data(row, ct))).ToArray();
            await PackageCodec.WriteAsync(path, sources, new { test = true }, null, CancellationToken.None);
            await PackageCodec.VerifyAsync(path, CancellationToken.None);
            using (var zip = ZipFile.OpenRead(path))
            {
                Check(zip.Entries.Count == 5, "five entries");
                using var input = zip.GetEntry("users.csv")!.Open();
                using var parser = new TextFieldParser(input, new UTF8Encoding(false, true)) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
                parser.SetDelimiters(","); parser.ReadFields();
                var fields = parser.ReadFields()!.Select(Decode).ToArray();
                Check(fields[0] == "0003", "leading zeros");
                // TextFieldParser normalizes embedded CRLF to LF; CSV bytes themselves retain CRLF.
                Check(fields[1]!.Replace("\r\n", "\n") == ((string)row[1]!).Replace("\r\n", "\n"), "multiline Unicode/quotes");
                Check(fields[2] is null && fields[3] == "" && fields[4] == "\\N" && fields[5] == "a\\b", "NULL/empty/backslash");
                Check(Convert.FromBase64String(fields[6]!).SequenceEqual(binary), "binary roundtrip");
                Check(fields[7] == "2026-11-03T00:00:00.1234567", "date precision");
                Check(parser.EndOfData, "one data record");
                using var manifestStream = zip.GetEntry("manifest.json")!.Open();
                using var manifest = JsonDocument.Parse(manifestStream);
                Check(!manifest.RootElement.GetProperty("production_import_approved").GetBoolean(), "review safety flag");
                Check(manifest.RootElement.GetProperty("schema_contract_id").GetString() == "biostar-source-review-v3", "updated schema contract");
            }
            try { await PackageCodec.WriteAsync(path, sources, new { }, null, CancellationToken.None); throw new Exception("Overwrite accepted"); }
            catch (IOException) { }
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                try { await PackageCodec.WriteAsync(Path.Combine(folder, "cancel.zip"), sources, new { }, null, cancelled.Token); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException) { }
            }
            Check(Directory.GetFiles(folder).Length == 1, "no cancellation partial");
            string emptyPath = Path.Combine(folder, "empty.zip");
            static async IAsyncEnumerable<object?[]> Empty([EnumeratorCancellation] CancellationToken ct) { await Task.Yield(); ct.ThrowIfCancellationRequested(); yield break; }
            await PackageCodec.WriteAsync(emptyPath, PackageCodec.Names.Select(n => new CsvSource(n, ["id"], Empty)).ToArray(), new { }, null, CancellationToken.None);
            await PackageCodec.VerifyAsync(emptyPath, CancellationToken.None);
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("users.csv")!.Delete();
                using var writer = new StreamWriter(zip.CreateEntry("users.csv").Open());
                writer.Write("tampered");
            }
            try { await PackageCodec.VerifyAsync(path, CancellationToken.None); throw new Exception("Tamper accepted"); }
            catch (InvalidDataException) { }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
