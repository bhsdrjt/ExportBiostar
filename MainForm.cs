using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Text.Json;

namespace BioStarExporter;

public sealed class MainForm : Form
{
    private readonly TextBox server = new() { Width = 170, Text = "localhost", AccessibleName = "Server SQL" };
    private readonly TextBox database = new() { Width = 180, Text = "Biolite_AGM_AC", AccessibleName = "Database sumber" };
    private readonly ComboBox auth = new() { Width = 185, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Autentikasi" };
    private readonly TextBox login = new() { Width = 130, Enabled = false, AccessibleName = "Login SQL" };
    private readonly TextBox password = new() { Width = 130, UseSystemPasswordChar = true, Enabled = false, AccessibleName = "Password SQL" };
    private readonly TextBox search = new() { Width = 170, MaxLength = 512, PlaceholderText = "USRID / nama", AccessibleName = "Pencarian user" };
    private readonly ComboBox department = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Department" };
    private readonly DateTimePicker start = DatePicker("Tanggal mulai");
    private readonly DateTimePicker end = DatePicker("Tanggal akhir");
    private static readonly Color Ink = Color.FromArgb(30, 42, 55);
    private static readonly Color Muted = Color.FromArgb(85, 101, 117);
    private static readonly Color Accent = Color.FromArgb(15, 105, 112);
    private static readonly Color Line = Color.FromArgb(220, 228, 234);
    private readonly Label status = new() { AutoSize = true, Dock = DockStyle.Fill, Text = "Siap. Hubungkan database sumber untuk memuat user.", Padding = new Padding(0, 6, 0, 4), AccessibleName = "Status operasi" };
    private readonly Label counts = new() { AutoSize = true, Padding = new Padding(6) };
    private readonly ProgressBar progressBar = new() { Width = 160, Height = 18, Style = ProgressBarStyle.Marquee, Visible = false };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Daftar user" };
    private readonly List<Control> actionPanels = [];
    private readonly Button cancel = new() { Text = "Batalkan", AutoSize = true, Enabled = false };
    private readonly Button validate = new() { Text = "Validasi pilihan", AutoSize = true, Enabled = false };
    private readonly Button export = new() { Text = "Export ZIP uji", AutoSize = true, Enabled = false };
    private readonly Button connect = new() { Text = "Tes koneksi / Muat user", AutoSize = true };
    private readonly Button inspect = new() { Text = "Simpan metadata schema", AutoSize = true, Enabled = false };
    private BioStarSource? source;
    private List<UserRow> rows = [];
    private HashSet<int> selected = [];
    private UserFilter activeFilter = new();
    private bool allResults;
    private bool rendering;
    private int page;
    private const int PageSize = 100;
    private CancellationTokenSource? operation;
    private string? lastOutput;

    public MainForm()
    {
        Text = "BioStar Exporter — Source Review 0.1";
        MinimumSize = new Size(1100, 820);
        Size = new Size(1360, 940);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        ForeColor = Ink;
        BackColor = Color.FromArgb(241, 245, 247);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, Padding = new Padding(24), AutoScroll = true };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(i == 4 ? SizeType.Percent : SizeType.AutoSize, i == 4 ? 100 : 0));
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 16) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(new Label { Text = "BioStar Exporter", AutoSize = true, Font = new Font(Font.FontFamily, 23, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
        heading.Controls.Add(new Label { Text = "User, fingerprint, dan kartu CSN dalam satu paket ZIP.", AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 4, 0, 0) }, 0, 1);
        heading.Controls.Add(new Label { Text = "SOURCE REVIEW  /  0.1", AutoSize = true, ForeColor = Accent, BackColor = Color.FromArgb(221, 239, 237), Padding = new Padding(12, 8, 12, 8), Anchor = AnchorStyles.Right }, 1, 0);
        layout.Controls.Add(heading, 0, 0);
        var warning = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0, 0, 0, 14), BackColor = Color.FromArgb(255, 244, 220), ForeColor = Color.FromArgb(113, 74, 15), Text = "PAKET REVIEW — Belum disetujui untuk import produksi. Paket berisi biometrik; simpan pada media terenkripsi." };
        layout.Controls.Add(warning, 0, 1);
        auth.Items.AddRange(["Windows Authentication", "SQL Authentication"]); auth.SelectedIndex = 0;
        var connection = Panel();
        connection.Controls.AddRange([Field("Server / instance", server), Field("Database", database), Field("Autentikasi", auth), Field("Username", login), Field("Password", password)]);
        var connectionActions = Panel();
        connectionActions.Controls.AddRange([connect, inspect]);
        var connectionCard = Section("01  Koneksi sumber", connection);
        connectionCard.Controls.Add(connectionActions);
        layout.Controls.Add(connectionCard, 0, 2);
        var filters = Panel();
        SetDepartments([]);
        var apply = Button("Terapkan filter", async () => await LoadAsync());
        var reset = Button("Reset", async () => { search.Clear(); department.SelectedIndex = 0; start.Checked = end.Checked = false; await LoadAsync(); });
        filters.Controls.AddRange([Field("Cari USRID / nama", search), Field("Department", department), Field("CREATEDT mulai", start), Field("CREATEDT akhir (inklusif)", end), apply, reset]);
        apply.Margin = reset.Margin = new Padding(4, 23, 4, 0);
        layout.Controls.Add(Section("02  Filter user", filters), 0, 3);
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "pick", HeaderText = "Pilih", FillWeight = 35 });
        foreach (var title in new[] { "USRID", "Nama", "Department", "CREATEDT", "Fingerprint", "CSN" })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = title, HeaderText = title, ReadOnly = true });
        grid.Columns["Nama"]!.FillWeight = 180;
        grid.Columns["CREATEDT"]!.FillWeight = 140;
        foreach (DataGridViewColumn col in grid.Columns) col.SortMode = DataGridViewColumnSortMode.NotSortable;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 42;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(231, 238, 242), ForeColor = Ink, SelectionBackColor = Color.FromArgb(231, 238, 242), SelectionForeColor = Ink, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(8, 0, 8, 0) };
        grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = Ink, SelectionBackColor = Color.FromArgb(215, 239, 237), SelectionForeColor = Ink, Padding = new Padding(8, 0, 8, 0) };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(247, 250, 251);
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Line;
        grid.RowTemplate.Height = 36;
        grid.MinimumSize = new Size(0, 160);
        grid.Margin = Padding.Empty;
        grid.Columns["pick"]!.MinimumWidth = 58;
        grid.Columns["Fingerprint"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.Columns["CSN"]!.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        layout.Controls.Add(grid, 0, 4);
        var paging = Panel();
        paging.Controls.AddRange([
            Button("‹ Sebelumnya", () => { if (page > 0) { page--; Render(); } return Task.CompletedTask; }),
            Button("Berikutnya ›", () => { if ((page + 1) * PageSize < rows.Count) { page++; Render(); } return Task.CompletedTask; }),
            Button("Pilih semua hasil filter", () => { selected = rows.Select(r => r.SourceId).ToHashSet(); allResults = true; Render(); return Task.CompletedTask; }),
            Button("Hapus pilihan", () => { selected.Clear(); allResults = false; Render(); return Task.CompletedTask; }), counts]);
        layout.Controls.Add(paging, 0, 5);
        var bottom = Panel();
        bottom.Controls.AddRange([export, validate, Button("Buka folder hasil", () => { if (lastOutput is not null) Process.Start(new ProcessStartInfo("explorer.exe", Path.GetDirectoryName(lastOutput)!) { UseShellExecute = true }); return Task.CompletedTask; }), cancel, progressBar]);
        bottom.Padding = new Padding(0, 10, 0, 0);
        layout.Controls.Add(bottom, 0, 6);
        status.ForeColor = Muted;
        layout.Controls.Add(status, 0, 7);
        Controls.Add(layout);
        foreach (var button in new[] { connect, inspect, validate, export, cancel }) StyleButton(button);
        foreach (var button in new[] { connect, export })
        {
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(12, 88, 94);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(9, 72, 78);
        }
        counts.ForeColor = Muted;
        progressBar.Margin = new Padding(12, 12, 0, 0);
        actionPanels.AddRange([connection, filters, paging]);
        auth.SelectedIndexChanged += (_, _) => { login.Enabled = password.Enabled = auth.SelectedIndex == 1; InvalidateConnection(); };
        foreach (var box in new[] { server, database, login, password }) box.TextChanged += (_, _) => InvalidateConnection();
        connect.Click += async (_, _) => await ConnectAsync();
        inspect.Click += async (_, _) => await SaveSchemaAsync();
        validate.Click += async (_, _) => await Run(async ct =>
        {
            var result = await RequireSource().ValidateAsync(SnapshotSelection(), ct);
            MessageBox.Show(this, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }) + "\n\nValidasi data saja; bukan persetujuan produksi.", "Validasi pilihan", MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
        export.Click += async (_, _) => await ExportAsync();
        cancel.Click += (_, _) => operation?.Cancel();
        grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        grid.CellValueChanged += (_, e) =>
        {
            if (rendering || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            int id = (int)grid.Rows[e.RowIndex].Tag!;
            if (grid.Rows[e.RowIndex].Cells[0].Value is true) selected.Add(id); else selected.Remove(id);
            allResults = false;
            UpdateCounts();
        };
        FormClosing += (_, e) => { if (operation is not null) { e.Cancel = true; operation.Cancel(); status.Text = "Membatalkan operasi sebelum menutup. Tutup kembali setelah selesai."; } };
        Render();
    }

    private static DateTimePicker DatePicker(string name) => new() { Width = 155, Format = DateTimePickerFormat.Custom, CustomFormat = "dd MMM yyyy", ShowCheckBox = true, Checked = false, AccessibleName = name };
    private static FlowLayoutPanel Panel() => new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 4) };
    private static TableLayoutPanel Section(string title, Control content)
    {
        var section = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, BackColor = Color.White, Padding = new Padding(16, 10, 16, 10), Margin = new Padding(0, 0, 0, 12) };
        section.RowCount = 3;
        for (int i = 0; i < 3; i++) section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.Controls.Add(new Label { Text = title, AutoSize = true, ForeColor = Muted, Font = new Font("Segoe UI", 10, FontStyle.Bold), Margin = new Padding(0, 0, 0, 6) });
        section.Controls.Add(content);
        // Measure wrapping at actual width; unconstrained preferred size stacks every field.
        section.AutoSize = false;
        section.Layout += (_, _) =>
        {
            int height = section.Padding.Vertical;
            foreach (Control child in section.Controls)
                height += child.GetPreferredSize(new Size(Math.Max(1, section.ClientSize.Width - section.Padding.Horizontal - child.Margin.Horizontal), 0)).Height + child.Margin.Vertical;
            if (section.Height != height) section.Height = height;
        };
        return section;
    }
    private static Control Field(string label, Control input)
    {
        var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 16, 3) };
        input.Margin = new Padding(0, 4, 0, 0);
        if (input is TextBox text) text.BorderStyle = BorderStyle.FixedSingle;
        p.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = Muted, Margin = Padding.Empty }); p.Controls.Add(input); return p;
    }
    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Line;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(233, 242, 243);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(217, 232, 234);
        button.BackColor = Color.White;
        button.ForeColor = Ink;
        button.Padding = new Padding(10, 4, 10, 4);
        button.MinimumSize = new Size(0, 36);
        button.Cursor = Cursors.Hand;
        button.Margin = new Padding(0, 3, 8, 3);
        button.UseVisualStyleBackColor = false;
    }
    private static Button Button(string text, Func<Task> action)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 32), Margin = new Padding(4, 6, 4, 3) };
        StyleButton(b);
        b.Click += async (_, _) => await action(); return b;
    }
    private void InvalidateConnection()
    {
        source = null; rows.Clear(); selected.Clear(); allResults = false; inspect.Enabled = false; page = 0; Render();
        status.Text = "Koneksi berubah. Tes koneksi kembali.";
    }
    private BioStarSource RequireSource() => source ?? throw new InvalidOperationException("Tes koneksi terlebih dahulu.");
    private void SetDepartments(IEnumerable<string> values)
    {
        department.Items.Clear(); department.Items.Add("Semua department"); department.Items.Add("Tanpa department");
        foreach (string value in values) department.Items.Add(value);
        department.SelectedIndex = 0;
    }
    private UserFilter ReadFilter()
    {
        int mode = Math.Min(department.SelectedIndex, 2);
        var f = new UserFilter(search.Text, mode, mode == 2 ? department.SelectedItem?.ToString() : null, start.Checked ? start.Value.Date : null, end.Checked ? end.Value.Date : null);
        f.EndExclusive(); return f;
    }
    private Selection SnapshotSelection() => new(activeFilter, selected.ToHashSet(), allResults);
    private async Task ConnectAsync() => await Run(async ct =>
    {
        if (string.IsNullOrWhiteSpace(server.Text) || string.IsNullOrWhiteSpace(database.Text)) throw new InvalidOperationException("Server dan database wajib diisi.");
        if (auth.SelectedIndex == 1 && string.IsNullOrWhiteSpace(login.Text)) throw new InvalidOperationException("Username SQL wajib diisi.");
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server.Text.Trim(), InitialCatalog = database.Text.Trim(), IntegratedSecurity = auth.SelectedIndex == 0,
            Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = true, ConnectTimeout = 15,
            ApplicationName = "BioStar Source Review Exporter", PersistSecurityInfo = false
        };
        if (!builder.IntegratedSecurity) { builder.UserID = login.Text; builder.Password = password.Text; }
        source = new BioStarSource(builder.ConnectionString);
        rows.Clear(); selected.Clear(); page = 0;
        try { await source.InspectAsync(ct); }
        finally { inspect.Enabled = source.Schema.Count > 0; }
        SetDepartments(await source.DepartmentsAsync(ct));
        activeFilter = ReadFilter();
        rows = await source.UsersAsync(activeFilter, ct);
        Render();
        status.Text = "Terhubung. Data sumber tampil; waktu mengikuti nilai DB tanpa konversi. Maksimal 100.000 user.";
    });
    private async Task LoadAsync() => await Run(async ct =>
    {
        var db = RequireSource();
        activeFilter = ReadFilter(); selected.Clear(); allResults = false; page = 0; rows.Clear(); Render();
        rows = await db.UsersAsync(activeFilter, ct); Render();
        status.Text = "Filter diterapkan; pilihan sebelumnya dibersihkan.";
    });
    private void Render()
    {
        rendering = true;
        try
        {
            grid.Rows.Clear();
            foreach (var r in rows.Skip(page * PageSize).Take(PageSize))
            {
                int index = grid.Rows.Add(selected.Contains(r.SourceId), r.UserId, r.Name, r.Department ?? "", r.Created?.ToString("yyyy-MM-dd HH:mm:ss") ?? "", r.Fingerprints, r.Cards);
                grid.Rows[index].Tag = r.SourceId;
            }
        }
        finally { rendering = false; }
        UpdateCounts();
    }
    private void UpdateCounts()
    {
        counts.Text = $"{rows.Count:N0} hasil · {selected.Count:N0} dipilih · halaman {page + 1}/{Math.Max(1, (rows.Count + PageSize - 1) / PageSize)}";
        validate.Enabled = export.Enabled = operation is null && source is not null && selected.Count > 0;
    }
    private async Task ExportAsync()
    {
        if (MessageBox.Show(this,
            "Paket ini berisi biometrik. Simpan pada media terenkripsi dan jangan buka CSV di Excel.\n\nBuild review belum memverifikasi metadata/dependensi BioStar. Manifest production_import_approved=false; importer produksi harus menolak paket ini.\n\nBuat ZIP untuk review/pengujian?",
            "Konfirmasi ekspor uji", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        using var dialog = new SaveFileDialog { Filter = "ZIP package (*.zip)|*.zip", FileName = $"biostar-review-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}.zip", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Run(async ct =>
        {
            var selection = SnapshotSelection();
            await RequireSource().ExportAsync(selection, dialog.FileName, new Progress<string>(s => status.Text = s), ct);
            lastOutput = dialog.FileName;
            status.Text = "ZIP uji tersimpan dan checksum terverifikasi. Belum disetujui untuk import produksi.";
        });
    }
    private async Task SaveSchemaAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "Metadata JSON (*.json)|*.json", FileName = "biostar-schema-columns.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await Run(async ct =>
        {
            if (File.Exists(dialog.FileName)) throw new IOException("File sudah ada. Pilih nama baru.");
            await using var stream = new FileStream(dialog.FileName, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(stream, new { columns = RequireSource().Schema, warning = "Metadata kolom saja; bukan DDL lengkap. Index, FK, default, trigger masih perlu review." }, new JsonSerializerOptions { WriteIndented = true }, ct);
            status.Text = "Metadata kolom tersimpan; tidak berisi template atau record user.";
        });
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (operation is not null) return;
        using var cts = new CancellationTokenSource(); operation = cts;
        foreach (var panel in actionPanels) panel.Enabled = false;
        grid.Enabled = false; validate.Enabled = export.Enabled = false; cancel.Enabled = true; progressBar.Visible = true;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { status.Text = "Operasi dibatalkan. Tidak ada perubahan database."; }
        catch (SqlException e)
        {
            // SQL server error text can contain data values; never display raw server messages.
            status.Text = $"SQL error {e.Number}. Periksa koneksi/TLS, izin baca, schema dan snapshot isolation. Tidak ada perubahan otomatis.";
            MessageBox.Show(this, status.Text, "Operasi gagal", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            status.Text = e.Message;
            MessageBox.Show(this, e.Message, "Operasi gagal", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            status.Text = "Operasi gagal karena error internal. Tidak ada credential yang dicatat. Jalankan self-check dan periksa build.";
            MessageBox.Show(this, status.Text, "Operasi gagal", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            operation = null;
            foreach (var panel in actionPanels) panel.Enabled = true;
            grid.Enabled = true; cancel.Enabled = false; progressBar.Visible = false;
            login.Enabled = password.Enabled = auth.SelectedIndex == 1;
            UpdateCounts();
        }
    }
}
