using Microsoft.Data.SqlClient;
using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BioStarExporter;

public sealed record UserFilter(string Search = "", int DepartmentMode = 0, string? Department = null, DateTime? Start = null, DateTime? End = null)
{
    public DateTime? EndExclusive()
    {
        if (Start.HasValue && End.HasValue && Start.Value.Date > End.Value.Date)
            throw new InvalidOperationException("Tanggal mulai melebihi tanggal akhir.");
        if (End?.Date == DateTime.MaxValue.Date) throw new InvalidOperationException("Tanggal akhir terlalu besar.");
        return End?.Date.AddDays(1);
    }
}
public sealed record UserRow(int SourceId, string UserId, string Name, string? Department, DateTime? Created, long Fingerprints, long Cards);
public sealed record ColumnInfo(string Table, string Name, string Type, short MaxLength, byte Precision, byte Scale, bool Nullable, bool Identity, string? Collation);
public sealed record Selection(UserFilter Filter, HashSet<int> Ids, bool AllResults);

public sealed class BioStarSource(string connectionString)
{
    // ponytail: fixed source-review profile, NOT an approved migration schema. Replace only after DDL/vendor review.
    public static readonly string[] UserColumns = ["USRUID", "USRID", "NM", "GEN", "TIT", "PH", "EML", "BTH", "STTDT", "EXPDT", "SECLVL", "DISDRT", "DISCNT", "DISMSG", "DSBDUSR", "IHR", "USATHPHT", "DEL", "DELDT", "DELUSRID", "USRGRUID", "ISUSRGR", "LSTMOD", "MODFLG", "PERMUID", "LSTMODDT", "UDTCNT", "CREATEDT", "IDXUSRID", "IDXUSRIDNUM", "IDXNM", "IDXPH", "IDXEML", "IDXLSTMOD", "USERIP", "DEPARTMENT", "TITLE"];
    public static readonly string[] FingerColumns = ["USRUID", "FNGPIDX", "DUR", "TMPL0", "TMPL1", "CHS", "LSTMOD"];
    public static readonly string[] CardColumns = ["CRDUID", "CRDTYPUID", "CRDCSN", "ASMUID", "WGDFMUID", "WGDFMTVER"];
    public static readonly string[] LinkColumns = ["USRUID", "CRDUID", "IDX", "ISSDDT", "EXPDT", "ISSDCNT", "CRDMSK", "PRDSTDT", "PRDEDDT"];
    private const string Eligible = "u.DEL=N'N' AND u.ISUSRGR=N'N' AND NULLIF(u.LGNID,N'') IS NULL AND NULLIF(u.PIN,N'') IS NULL";
    private const string Selected = "u.USRUID IN (SELECT CONVERT(int,[value]) FROM OPENJSON(@ids))";
    public List<ColumnInfo> Schema { get; private set; } = [];
    public string ServerVersion { get; private set; } = "";

    private async Task<SqlConnection> Open(CancellationToken ct)
    {
        var c = new SqlConnection(connectionString);
        try { await c.OpenAsync(ct); return c; } catch { await c.DisposeAsync(); throw; }
    }
    private static SqlCommand Command(SqlConnection c, SqlTransaction? tx, string sql, UserFilter? filter = null, HashSet<int>? ids = null)
    {
        var cmd = new SqlCommand(sql, c, tx) { CommandTimeout = 60 };
        if (ids is not null) cmd.Parameters.Add("@ids", SqlDbType.NVarChar, -1).Value = JsonSerializer.Serialize(ids.Order());
        if (filter is not null)
        {
            cmd.Parameters.Add("@search", SqlDbType.NVarChar, 512).Value = filter.Search;
            cmd.Parameters.Add("@deptMode", SqlDbType.Int).Value = filter.DepartmentMode;
            cmd.Parameters.Add("@dept", SqlDbType.NVarChar, -1).Value = (object?)filter.Department ?? DBNull.Value;
            cmd.Parameters.Add("@start", SqlDbType.DateTime2).Value = (object?)filter.Start?.Date ?? DBNull.Value;
            cmd.Parameters.Add("@end", SqlDbType.DateTime2).Value = (object?)filter.EndExclusive() ?? DBNull.Value;
        }
        return cmd;
    }
    private static string Predicate => $"{Eligible} AND (@search=N'' OR CHARINDEX(@search,u.USRID)>0 OR CHARINDEX(@search,u.NM)>0) AND (@deptMode=0 OR (@deptMode=1 AND NULLIF(LTRIM(RTRIM(u.DEPARTMENT)),N'') IS NULL) OR (@deptMode=2 AND u.DEPARTMENT=@dept)) AND (@start IS NULL OR u.CREATEDT>=@start) AND (@end IS NULL OR u.CREATEDT<@end)";

    public async Task InspectAsync(CancellationToken ct)
    {
        await using var c = await Open(ct);
        ServerVersion = c.ServerVersion;
        using (var check = Command(c, null, "SELECT compatibility_level FROM sys.databases WHERE database_id=DB_ID()"))
            if (Convert.ToInt32(await check.ExecuteScalarAsync(ct)) < 130)
                throw new InvalidOperationException("Database memerlukan compatibility level minimal 130. Aplikasi tidak mengubahnya otomatis.");
        using var cmd = Command(c, null, """
            SELECT t.name,c.name,ty.name,c.max_length,c.precision,c.scale,c.is_nullable,c.is_identity,c.collation_name
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id
            JOIN sys.columns c ON c.object_id=t.object_id JOIN sys.types ty ON ty.user_type_id=c.user_type_id
            WHERE s.name=N'dbo' AND t.name IN (N'T_USR',N'T_FNGPTMPL',N'T_CRD',N'T_USRCRD',N'T_CRDTYP',N'T_BLKL')
            ORDER BY t.name,c.column_id
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var schema = new List<ColumnInfo>();
        while (await reader.ReadAsync(ct)) schema.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt16(3), reader.GetByte(4), reader.GetByte(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.IsDBNull(8) ? null : reader.GetString(8)));
        Schema = schema;
        var required = new Dictionary<string, string[]>
        {
            ["T_USR"] = UserColumns.Concat(["LGNID", "PIN"]).ToArray(), ["T_FNGPTMPL"] = FingerColumns,
            ["T_CRD"] = CardColumns, ["T_USRCRD"] = LinkColumns,
            ["T_CRDTYP"] = ["CRDTYPUID", "NM", "TYP", "MD", "CRDLAYUID"], ["T_BLKL"] = ["CRDUID"]
        };
        foreach (var (table, columns) in required)
            foreach (string col in columns)
                if (!schema.Any(x => x.Table == table && x.Name == col))
                    throw new InvalidOperationException($"Schema belum didukung atau metadata tidak terlihat: {table}.{col}. Gunakan Inspeksi schema untuk review.");
        foreach (var (table, col) in new[] { ("T_USR", "USRUID"), ("T_FNGPTMPL", "USRUID"), ("T_CRD", "CRDUID"), ("T_USRCRD", "USRUID"), ("T_USRCRD", "CRDUID") })
            if (schema.Single(x => x.Table == table && x.Name == col).Type != "int") throw new InvalidOperationException($"Tipe {table}.{col} harus int untuk build ini.");
        foreach (string col in new[] { "USRID", "NM", "DEPARTMENT", "LGNID", "PIN" })
            if (schema.Single(x => x.Table == "T_USR" && x.Name == col).Type is not ("varchar" or "nvarchar" or "char" or "nchar"))
                throw new InvalidOperationException($"Tipe T_USR.{col} bukan teks yang didukung.");
        if (schema.Single(x => x.Table == "T_USR" && x.Name == "CREATEDT").Type is not ("datetime" or "datetime2" or "smalldatetime"))
            throw new InvalidOperationException("CREATEDT harus datetime/datetime2/smalldatetime; zona waktu tidak dikonversi.");
        foreach (string col in new[] { "TMPL0", "TMPL1" })
            if (schema.Single(x => x.Table == "T_FNGPTMPL" && x.Name == col).Type is not ("binary" or "varbinary" or "image"))
                throw new InvalidOperationException("Template bukan biner yang didukung.");
    }

    public async Task<List<string>> DepartmentsAsync(CancellationToken ct)
    {
        await using var c = await Open(ct);
        using var cmd = Command(c, null, $"SELECT DISTINCT u.DEPARTMENT FROM dbo.T_USR u WHERE {Eligible} AND NULLIF(LTRIM(RTRIM(u.DEPARTMENT)),N'') IS NOT NULL ORDER BY u.DEPARTMENT");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<string>();
        while (await r.ReadAsync(ct))
        {
            if (result.Count >= 100_000) throw new InvalidDataException("Terlalu banyak department.");
            result.Add(r.GetString(0));
        }
        return result;
    }

    public async Task<List<UserRow>> UsersAsync(UserFilter filter, CancellationToken ct, SqlConnection? existing = null, SqlTransaction? tx = null)
    {
        filter.EndExclusive();
        if (filter.Search.Length > 512) throw new InvalidOperationException("Pencarian maksimal 512 karakter.");
        var c = existing ?? await Open(ct);
        try
        {
            using var cmd = Command(c, tx, $"""
                SELECT TOP (100001) u.USRUID,u.USRID,u.NM,u.DEPARTMENT,u.CREATEDT,
                 (SELECT COUNT_BIG(*) FROM dbo.T_FNGPTMPL f WHERE f.USRUID=u.USRUID),
                 (SELECT COUNT_BIG(*) FROM dbo.T_USRCRD l JOIN dbo.T_CRD c ON c.CRDUID=l.CRDUID WHERE l.USRUID=u.USRUID AND c.CRDTYPUID=0)
                FROM dbo.T_USR u WHERE {Predicate} ORDER BY u.USRUID
                """, filter);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var users = new List<UserRow>();
            while (await r.ReadAsync(ct))
            {
                if (users.Count == PackageCodec.MaxRows) throw new InvalidOperationException("Hasil melebihi 100.000 user; persempit filter.");
                users.Add(new(r.GetInt32(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetDateTime(4), r.GetInt64(5), r.GetInt64(6)));
            }
            return users;
        }
        finally { if (existing is null) await c.DisposeAsync(); }
    }

    public async Task<object> ValidateAsync(Selection selection, CancellationToken ct)
    {
        await using var c = await Open(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Snapshot, ct);
        return await ValidateInside(c, tx, selection, ct);
    }

    private async Task<object> ValidateInside(SqlConnection c, SqlTransaction tx, Selection selection, CancellationToken ct)
    {
        if (selection.Ids.Count == 0) throw new InvalidOperationException("Pilih minimal satu user.");
        var current = await UsersAsync(selection.Filter, ct, c, tx);
        var currentIds = current.Select(u => u.SourceId).ToHashSet();
        if (selection.AllResults ? !currentIds.SetEquals(selection.Ids) : !selection.Ids.IsSubsetOf(currentIds))
            throw new InvalidOperationException("Hasil filter/pilihan berubah. Muat ulang dan konfirmasi kembali.");
        var checks = new (string Sql, string Error)[]
        {
            ($"SELECT COUNT_BIG(*) FROM dbo.T_USR u WHERE {Selected} AND (NULLIF(LTRIM(RTRIM(u.USRID)),N'') IS NULL)", "USRID kosong tidak didukung."),
            ($"SELECT COUNT_BIG(*) FROM (SELECT u.USRID FROM dbo.T_USR u WHERE {Selected} GROUP BY u.USRID HAVING COUNT_BIG(*)>1) d", "Duplikasi USRID menurut collation sumber."),
            ("SELECT CASE WHEN COUNT(*)=1 THEN 0 ELSE 1 END FROM dbo.T_CRDTYP WHERE CRDTYPUID=0 AND NM=N'CSN' AND TYP=1 AND MD=N'C' AND CRDLAYUID IS NULL", "Definisi CSN 0 tidak sesuai."),
            ($"SELECT COUNT_BIG(*) FROM dbo.T_USRCRD l JOIN dbo.T_USR u ON u.USRUID=l.USRUID LEFT JOIN dbo.T_CRD c ON c.CRDUID=l.CRDUID WHERE {Selected} AND (c.CRDUID IS NULL OR c.CRDTYPUID<>0 OR c.ASMUID IS NOT NULL OR c.WGDFMUID IS NOT NULL OR c.WGDFMTVER IS NOT NULL OR NULLIF(LTRIM(RTRIM(c.CRDCSN)),N'') IS NULL)", "Kartu yatim/non-CSN/dependensi atau nomor tidak didukung."),
            ($"SELECT COUNT_BIG(*) FROM dbo.T_USRCRD l JOIN dbo.T_USR u ON u.USRUID=l.USRUID WHERE {Selected} AND EXISTS(SELECT 1 FROM dbo.T_BLKL b WHERE b.CRDUID=l.CRDUID)", "Kartu terkait blacklist diblokir."),
            ($"SELECT COUNT_BIG(*) FROM (SELECT l.CRDUID FROM dbo.T_USRCRD l WHERE l.CRDUID IN (SELECT x.CRDUID FROM dbo.T_USRCRD x JOIN dbo.T_USR u ON u.USRUID=x.USRUID WHERE {Selected}) GROUP BY l.CRDUID HAVING COUNT_BIG(*)<>1) d", "Kartu memiliki relasi duplikat/lebih dari satu pemilik."),
            ($"SELECT COUNT_BIG(*) FROM (SELECT c.CRDCSN FROM dbo.T_CRD c WHERE c.CRDUID IN (SELECT l.CRDUID FROM dbo.T_USRCRD l JOIN dbo.T_USR u ON u.USRUID=l.USRUID WHERE {Selected}) GROUP BY c.CRDCSN HAVING COUNT_BIG(*)>1) d", "Nomor CSN duplikat dalam pilihan."),
            ($"SELECT COUNT_BIG(*) FROM dbo.T_FNGPTMPL f JOIN dbo.T_USR u ON u.USRUID=f.USRUID WHERE {Selected} AND (f.FNGPIDX IS NULL OR f.FNGPIDX<0 OR f.TMPL0 IS NULL OR DATALENGTH(f.TMPL0)=0 OR f.TMPL1 IS NULL OR DATALENGTH(f.TMPL1)=0 OR DATALENGTH(f.TMPL0)>1048576 OR DATALENGTH(f.TMPL1)>1048576)", "Template kosong/ukuran/indeks belum didukung build review."),
            ($"SELECT COUNT_BIG(*) FROM (SELECT f.USRUID,f.FNGPIDX FROM dbo.T_FNGPTMPL f JOIN dbo.T_USR u ON u.USRUID=f.USRUID WHERE {Selected} GROUP BY f.USRUID,f.FNGPIDX HAVING COUNT_BIG(*)>1) d", "Indeks fingerprint duplikat.")
        };
        foreach (var (sql, error) in checks)
        {
            using var cmd = Command(c, tx, sql, ids: selection.Ids);
            if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) != 0) throw new InvalidDataException(error);
        }
        var picked = current.Where(u => selection.Ids.Contains(u.SourceId)).ToArray();
        if (picked.Sum(u => u.Fingerprints) > PackageCodec.MaxRows || picked.Sum(u => u.Cards) > PackageCodec.MaxRows)
            throw new InvalidDataException("Fingerprint/kartu melebihi batas 100.000 record.");
        return new { users = picked.Length, fingerprints = picked.Sum(u => u.Fingerprints), cards = picked.Sum(u => u.Cards), production_import_approved = false };
    }

    public async Task ExportAsync(Selection selection, string destination, IProgress<string> progress, CancellationToken ct)
    {
        await InspectAsync(ct);
        await using var c = await Open(ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(IsolationLevel.Snapshot, ct);
        progress.Report("Validasi ulang dalam snapshot…");
        var totals = await ValidateInside(c, tx, selection, ct);
        CsvSource Source(string name, string[] columns, string alias, string from, string order)
        {
            var headers = columns.Select(x => x == "USRUID" ? "source_usr_uid" : x == "CRDUID" ? "source_crd_uid" : x).ToArray();
            string sql = $"SELECT {string.Join(',', columns.Select(x => $"{alias}.[{x}]"))} {from} WHERE {Selected} ORDER BY {order}";
            return new(name, headers, token => Rows(c, tx, sql, selection.Ids, token));
        }
        CsvSource[] sources =
        [
            Source("users.csv", UserColumns, "u", "FROM dbo.T_USR u", "u.USRUID"),
            Source("fingerprints.csv", FingerColumns, "f", "FROM dbo.T_FNGPTMPL f JOIN dbo.T_USR u ON u.USRUID=f.USRUID", "u.USRUID,f.FNGPIDX"),
            Source("cards.csv", CardColumns, "c", "FROM dbo.T_CRD c JOIN dbo.T_USRCRD l ON l.CRDUID=c.CRDUID JOIN dbo.T_USR u ON u.USRUID=l.USRUID", "c.CRDUID"),
            Source("user_cards.csv", LinkColumns, "l", "FROM dbo.T_USRCRD l JOIN dbo.T_USR u ON u.USRUID=l.USRUID", "u.USRUID,l.CRDUID")
        ];
        await PackageCodec.WriteAsync(destination, sources, new
        {
            source = new { sql_server_version = ServerVersion, biostar_version = "unverified", time_semantics = "database-wall-clock-unverified" },
            selection_summary = new { department_mode = selection.Filter.DepartmentMode, department = selection.Filter.Department, start = selection.Filter.Start, end = selection.Filter.End, all_results = selection.AllResults, selected_count = selection.Ids.Count },
            totals,
            schema = Schema,
            limitations = new[] { "SOURCE REVIEW ONLY: not approved for production import", "User metadata/dependencies not vendor verified", "No access-group relationships exported", "No PIN/login/face/photo exported", "Eligibility assumes DEL=N, ISUSRGR=N and empty LGNID/PIN; review required", "CREATEDT and other timestamps have no verified timezone" }
        }, progress, ct);
        // Read-only transaction is disposed/rolled back. No source data is changed.
    }

    private static async IAsyncEnumerable<object?[]> Rows(SqlConnection c, SqlTransaction tx, string sql, HashSet<int> ids, [EnumeratorCancellation] CancellationToken ct)
    {
        using var cmd = Command(c, tx, sql, ids: ids);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var values = new object[r.FieldCount];
            r.GetValues(values);
            yield return values;
        }
    }
}
