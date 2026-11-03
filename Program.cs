namespace BioStarExporter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            try { SelfCheck.RunAsync().GetAwaiter().GetResult(); Console.WriteLine("Self-check PASS"); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
        ApplicationConfiguration.Initialize();
        if (args.Contains("--ui-test"))
        {
            using var form = new MainForm();
            form.Show();
            foreach (var size in new[] { new Size(1360, 940), new Size(1100, 820) })
            {
                form.Size = size;
                Application.DoEvents();
                var table = form.Controls.OfType<TableLayoutPanel>().Single();
                var grid = table.Controls.OfType<DataGridView>().Single();
                Console.WriteLine($"UI {size}: grid={grid.Bounds}, rows={string.Join(',', table.GetRowHeights())}");
                if (grid.Height < 100 || table.GetRowHeights()[4] < grid.Height || table.Controls.Cast<Control>().Any(c => c.Bottom > table.ClientSize.Height))
                    throw new InvalidOperationException("UI layout clips controls.");
            }
            Console.WriteLine("UI layout PASS");
            return 0;
        }
        Application.Run(new MainForm());
        return 0;
    }
}
