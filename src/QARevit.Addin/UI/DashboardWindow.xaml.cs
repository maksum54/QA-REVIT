using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using QARevit.Addin.Revit;
using QARevit.Core.Ai;
using QARevit.Core.Config;
using QARevit.Core.Model;
using QARevit.Core.Register;
using QARevit.Core.Report;
using QARevit.Core.Rules;

namespace QARevit.Addin.UI;

public partial class DashboardWindow : Window
{
    private sealed record StageItem(string Code, string Label);

    /// <summary>Baris di tab Kop.</summary>
    public sealed class KopRow
    {
        public bool Selected { get; set; }
        public string UniqueId { get; init; } = "";
        public string Number { get; init; } = "";
        public string Name { get; init; } = "";
        public string TitleBlock { get; init; } = "";
        public string TitleBlockDisplay => TitleBlock.Length == 0 ? "— tanpa kop —" : TitleBlock;
    }

    private List<KopRow> _kopRows = new();
    private List<TitleBlockOption> _titleBlocks = new();
    private List<IssueRow> _issueRows = new();
    private IssueData? _issues;

    private readonly RevitTaskRunner _runner;
    private AddinSettings _settings;
    private QaConfig _config;
    private QaReport? _report;
    private List<RegisterEntry>? _register;
    private List<SheetChange> _changes = new();
    private AdviceService? _advice;
    private string? _lastAiAdvice;

    public DashboardWindow(RevitTaskRunner runner)
    {
        InitializeComponent();
        _runner = runner;
        _settings = AddinSettings.Load();
        _config = LoadConfigSafe();
        RefreshSettingsUi();
        foreach (var grid in new[] { KopGrid, IssueGrid, DiffGrid })
        {
            grid.PreviewMouseLeftButtonDown += CheckGrid_MouseDown;
            grid.PreviewKeyDown += CheckGrid_KeyDown;
        }
    }

    // ---------- Centang banyak baris ----------
    // Sorot beberapa baris (Shift/Ctrl+klik), lalu klik satu kotak centang atau tekan Spasi: semua yang disorot ikut.

    private static bool IsChecked(object row) => row switch
    {
        KopRow k => k.Selected, IssueRow i => i.Selected, SheetChange c => c.Selected, _ => false,
    };

    private static void SetChecked(object row, bool value)
    {
        switch (row)
        {
            case KopRow k: k.Selected = value; break;
            case IssueRow i: i.Selected = value; break;
            case SheetChange c when c.Applicable: c.Selected = value; break;
        }
    }

    private void ToggleChecked(System.Windows.Controls.DataGrid grid, object clicked)
    {
        var value = !IsChecked(clicked);
        var rows = grid.SelectedItems.Count > 1 && grid.SelectedItems.Contains(clicked)
            ? grid.SelectedItems.Cast<object>().ToList() : new List<object> { clicked };
        foreach (var row in rows) SetChecked(row, value);
        grid.Items.Refresh();
        UpdateKopCount();
        UpdateIssueCount();
    }

    private void CheckGrid_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (System.Windows.Controls.DataGrid)sender;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not System.Windows.Controls.DataGridCell)
            source = source is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        if (source is not System.Windows.Controls.DataGridCell { Column.DisplayIndex: 0 } cell) return;
        ToggleChecked(grid, cell.DataContext);
        e.Handled = true; // sorotan baris tidak berubah, dan satu klik langsung mencentang
    }

    private void CheckGrid_KeyDown(object sender, KeyEventArgs e)
    {
        var grid = (System.Windows.Controls.DataGrid)sender;
        if (e.Key != Key.Space || e.OriginalSource is System.Windows.Controls.TextBox || grid.SelectedItem is not { } first) return;
        ToggleChecked(grid, first);
        e.Handled = true;
    }

    private QaConfig LoadConfigSafe()
    {
        try { return _settings.LoadConfig(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Gagal membaca file konfigurasi, memakai bawaan.\n{ex.Message}", "QA Revit");
            return QaConfig.LoadDefault();
        }
    }

    private void RefreshSettingsUi()
    {
        StageCombo.ItemsSource = _config.Stages.Select(s => new StageItem(s.Code, $"{s.Code} — {s.Name}")).ToList();
        if (StageCombo.SelectedIndex < 0 && StageCombo.Items.Count > 0) StageCombo.SelectedIndex = 0;
        IssueStageCombo.ItemsSource = StageCombo.ItemsSource;
        if (IssueStageCombo.SelectedIndex < 0 && IssueStageCombo.Items.Count > 0) IssueStageCombo.SelectedIndex = 0;
        AiInfo.Text = _settings.Ai.IsConfigured
            ? $"AI: {_settings.Ai.Provider} / {_settings.Ai.Model}"
            : "AI belum diatur (Settings). Advice aturan tetap bisa dipakai.";
        _advice = _settings.Ai.IsConfigured ? new AdviceService(AiClientFactory.Create(_settings.Ai)) : null;
    }

    // ---------- QA ----------

    public async void RunQaCommand() => await RunQaAsync();

    private async Task RunQaAsync()
    {
        try
        {
            Status("Membaca sheet dari model…");
            var (sheets, titleBlocks, issues) = await _runner.Run(app =>
            {
                var doc = app.ActiveUIDocument.Document;
                return (SheetCollector.Collect(doc), TitleBlockService.GetTypes(doc), IssueService.Collect(doc));
            });
            UpdateTitleBlocks(titleBlocks, sheets);
            UpdateIssues(issues);
            _report = new RuleEngine().Run(sheets, _config);
            if (_register is not null) _changes = RegisterComparer.Compare(_register, sheets, _config);

            TotalText.Text = _report.Total.ToString();
            PassedText.Text = _report.Passed.ToString();
            FailedText.Text = _report.Failed.ToString();
            RateText.Text = $"{_report.PassRate:P0}";
            RateBar.Value = _report.PassRate * 100;
            RateBar.Foreground = (System.Windows.Media.Brush)FindResource(
                _report.PassRate >= 0.9 ? "Good" : _report.PassRate >= 0.6 ? "Warn" : "Bad");
            ErrorCount.Text = _report.Issues.Count(i => i.Severity == Severity.Error).ToString();
            WarnCount.Text = _report.Issues.Count(i => i.Severity == Severity.Warning).ToString();
            InfoCount.Text = _report.Issues.Count(i => i.Severity == Severity.Info).ToString();
            var stage = _report.Sheets.Select(x => SheetCode.TryParse(x.Number)?.Stage).Where(x => x is not null)
                .GroupBy(x => x).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            StageChip.Visibility = stage is null ? Visibility.Collapsed : Visibility.Visible;
            StageText.Text = stage is null ? "" : $"STAGE {stage} · {_config.StageName(stage)}";
            ApplyFilter();
            DiffGrid.ItemsSource = _changes;
            ShowRuleAdvice();
            _advice?.SetContext(_report, _config, _changes);
            Status($"QA selesai {DateTime.Now:HH:mm:ss}: {_report.Issues.Count} temuan dari {_report.Total} sheet.");
        }
        catch (Exception ex) { Error("QA gagal", ex); }
    }

    private void UpdateTitleBlocks(List<TitleBlockOption> types, IReadOnlyList<SheetInfo> sheets)
    {
        var keepKop = (KopCombo.SelectedItem as TitleBlockOption)?.Id;
        var keepNew = (NewSheetKopCombo.SelectedItem as TitleBlockOption)?.Id;
        var wasSelected = _kopRows.Where(r => r.Selected).Select(r => r.UniqueId).ToHashSet();

        _titleBlocks = types;
        KopCombo.ItemsSource = types;
        NewSheetKopCombo.ItemsSource = types;
        KopCombo.SelectedItem = types.FirstOrDefault(t => t.Id == keepKop) ?? types.FirstOrDefault();
        // Default kop sheet baru: yang paling banyak dipakai di model.
        var mostUsed = sheets.Where(s => s.TitleBlock.Length > 0).GroupBy(s => s.TitleBlock)
            .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
        NewSheetKopCombo.SelectedItem = types.FirstOrDefault(t => t.Id == keepNew)
            ?? types.FirstOrDefault(t => t.Label == mostUsed) ?? types.FirstOrDefault();

        _kopRows = sheets.Select(s => new KopRow
        {
            UniqueId = s.UniqueId, Number = s.Number, Name = s.Name, TitleBlock = s.TitleBlock,
            Selected = wasSelected.Contains(s.UniqueId),
        }).ToList();
        ApplyKopFilter();
    }

    private void KopFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (KopSearchHint is not null)
            KopSearchHint.Visibility = KopSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyKopFilter();
    }

    private IEnumerable<KopRow> VisibleKopRows()
    {
        var q = KopSearchBox.Text.Trim();
        return q.Length == 0 ? _kopRows : _kopRows.Where(r =>
            r.Number.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.TitleBlock.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyKopFilter()
    {
        KopGrid.ItemsSource = VisibleKopRows().ToList();
        UpdateKopCount();
    }

    private void UpdateKopCount() => KopCount.Text = $"{_kopRows.Count(r => r.Selected)} dipilih dari {_kopRows.Count}";

    private void KopSelectAll_Click(object sender, RoutedEventArgs e) => SetKopSelected(true);
    private void KopClear_Click(object sender, RoutedEventArgs e) => SetKopSelected(false);

    private void SetKopSelected(bool value)
    {
        // "Pilih Semua" mengikuti filter pencarian: cari dulu (mis. "LP"), lalu pilih semua hasilnya.
        foreach (var r in value ? VisibleKopRows() : _kopRows) r.Selected = value;
        KopGrid.Items.Refresh();
        UpdateKopCount();
    }

    private async void KopGoTo_Click(object sender, RoutedEventArgs e)
    {
        if (KopGrid.SelectedItem is KopRow row) await GoToAsync(row.UniqueId);
    }

    private async void ChangeKop_Click(object sender, RoutedEventArgs e)
    {
        KopGrid.CommitEdit();
        var targets = _kopRows.Where(r => r.Selected).ToList();
        // Tanpa centang: pakai baris yang sedang di-highlight (ganti satu per satu).
        if (targets.Count == 0) targets = KopGrid.SelectedItems.OfType<KopRow>().ToList();
        if (targets.Count == 0) { Status("Centang sheet yang akan diganti kopnya."); return; }
        if (KopCombo.SelectedItem is not TitleBlockOption kop) { Status("Pilih family kop dulu."); return; }

        if (MessageBox.Show(this, $"Ganti kop {targets.Count} sheet menjadi:\n{kop.Label}?", "QA Revit",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var ids = targets.Select(t => t.UniqueId).ToList();
            var result = await _runner.Run(app => TitleBlockService.Change(app.ActiveUIDocument.Document, ids, kop.Id));
            ShowApplyResult(result);
            await RunQaAsync();
        }
        catch (Exception ex) { Error("Ganti kop gagal", ex); }
    }

    // ---------- Issue / Revisi ----------

    private void UpdateIssues(IssueData data)
    {
        var keepRev = (IssueRevCombo.SelectedItem as RevisionOption)?.Id;
        var wasSelected = _issueRows.Where(r => r.Selected).Select(r => r.UniqueId).ToHashSet();

        _issues = data;
        _issueRows = data.Sheets;
        foreach (var r in _issueRows) r.Selected = wasSelected.Contains(r.UniqueId);
        IssueColCombo.ItemsSource = data.Columns;
        IssueRevCombo.ItemsSource = data.Revisions;
        // Default: revisi terbaru.
        IssueRevCombo.SelectedItem = data.Revisions.FirstOrDefault(r => r.Id == keepRev) ?? data.Revisions.LastOrDefault();
        if (IssueApvBox.Text.Length == 0) IssueApvBox.Text = data.Approver;
        IssueInfo.Text =
            "Banyak sheet: centang sheet, isi 1–4, klik Terapkan Issue.  Satu sheet: klik sel REVISI DI SHEET, KOLOM TERCENTANG atau STATUS." +
            "  Ketik langsung: klik dua kali NOMOR, JUDUL atau NOTE, lalu Enter.  Centang banyak: sorot baris dengan Shift/Ctrl+klik, lalu klik satu kotak centang atau tekan Spasi.  " +
            (data.Columns.Count == 0
                ? "Sheet list dengan kolom 'ACT ISSUE NN' tidak ditemukan, jadi kolom sheet list tidak diisi."
                : $"Kolom issue dibaca dari schedule '{data.ScheduleName}'.");
        ApplyIssueFilter();
    }

    private void IssueRev_Changed(object sender, RoutedEventArgs e)
    {
        if (_issues is null || IssueRevCombo.SelectedItem is not RevisionOption rev) return;
        // Kolom yang judulnya tanggal revisi; kalau belum ada, kolom kosong pertama.
        IssueColCombo.SelectedItem = _issues.Columns.FirstOrDefault(c => c.Heading == rev.Date)
            ?? _issues.Columns.FirstOrDefault(c => c.IsFree);
    }

    private void IssueFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IssueSearchHint is not null)
            IssueSearchHint.Visibility = IssueSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyIssueFilter();
    }

    private IEnumerable<IssueRow> VisibleIssueRows()
    {
        var q = IssueSearchBox.Text.Trim();
        return q.Length == 0 ? _issueRows : _issueRows.Where(r =>
            r.Number.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.Status.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyIssueFilter()
    {
        IssueGrid.ItemsSource = VisibleIssueRows().ToList();
        UpdateIssueCount();
    }

    private void UpdateIssueCount() => IssueCount.Text = $"{_issueRows.Count(r => r.Selected)} dipilih dari {_issueRows.Count}";

    private void IssueSelectAll_Click(object sender, RoutedEventArgs e) => SetIssueSelected(true);
    private void IssueClear_Click(object sender, RoutedEventArgs e) => SetIssueSelected(false);

    private void SetIssueSelected(bool value)
    {
        foreach (var r in value ? VisibleIssueRows() : _issueRows) r.Selected = value;
        IssueGrid.Items.Refresh();
        UpdateIssueCount();
    }

    private async void IssueGoTo_Click(object sender, RoutedEventArgs e)
    {
        if (IssueGrid.SelectedItem is IssueRow row) await GoToAsync(row.UniqueId);
    }

    private async void ApplyIssue_Click(object sender, RoutedEventArgs e)
    {
        var targets = IssueTargets();
        if (targets.Count == 0) { Status("Centang sheet yang akan di-issue."); return; }
        if (_issues is null || IssueRevCombo.SelectedItem is not RevisionOption rev) { Status("Pilih revisi dulu."); return; }

        var column = IssueColCombo.SelectedItem as IssueColumn;
        var note = IssueNoteBox.Text.Trim();
        var approver = IssueApvBox.Text.Trim();
        var summary = $"Terapkan ke {targets.Count} sheet?\n\nRevisi: {rev.Label}\nKolom sheet list: {column?.Label ?? "—"}" +
                      $"\nNote: {(note.Length == 0 ? "—" : note)}\nAPVD: {(approver.Length == 0 ? "—" : approver)}";
        if (MessageBox.Show(this, summary, "QA Revit", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var ids = targets.Select(t => t.UniqueId).ToList();
            var scheduleId = _issues.ScheduleId;
            var result = await _runner.Run(app =>
                IssueService.Apply(app.ActiveUIDocument.Document, ids, rev.Id, column, scheduleId, note, approver));
            ShowApplyResult(result);
            await RunQaAsync();
        }
        catch (Exception ex) { Error("Issue gagal", ex); }
    }

    /// <summary>Sheet tercentang; tanpa centang, baris yang sedang di-highlight.</summary>
    private List<IssueRow> IssueTargets()
    {
        IssueGrid.CommitEdit();
        var targets = _issueRows.Where(r => r.Selected).ToList();
        return targets.Count > 0 ? targets : IssueGrid.SelectedItems.OfType<IssueRow>().ToList();
    }

    private async Task RunStatesAsync(List<SheetState> states)
    {
        if (_issues is null || states.Count == 0) return;
        try
        {
            var columns = _issues.Columns;
            var approver = IssueApvBox.Text.Trim();
            var result = await _runner.Run(app => IssueService.SetStates(app.ActiveUIDocument.Document, states, columns, approver));
            ShowApplyResult(result);
            await RunQaAsync();
        }
        catch (Exception ex) { Error("Ubah revisi gagal", ex); }
    }

    private async void CancelIssue_Click(object sender, RoutedEventArgs e)
    {
        if (_issues is null || IssueRevCombo.SelectedItem is not RevisionOption rev) { Status("Pilih revisi dulu."); return; }
        var targets = IssueTargets().Where(t => t.RevisionIds.Contains(rev.Id)).ToList();
        if (targets.Count == 0) { Status("Tidak ada sheet terpilih yang memakai revisi itu."); return; }
        if (MessageBox.Show(this, $"Lepas revisi dari {targets.Count} sheet?\n\nRevisi: {rev.Label}\n\n" +
                "Centang kolom sheet list untuk tanggal itu ikut dilepas, SUB_REV kembali ke revisi sebelumnya dan REV_NOTE dikosongkan.",
                "QA Revit", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        var dateOf = _issues.Revisions.ToDictionary(r => r.Id, r => r.Date);
        var column = _issues.Columns.FirstOrDefault(c => c.Heading == rev.Date);
        await RunStatesAsync(targets.Select(t =>
        {
            var keep = t.RevisionIds.Where(id => id != rev.Id).ToList();
            // Kolom tanggal itu tetap dicentang kalau masih ada revisi lain bertanggal sama di sheet.
            var untick = column is not null && !keep.Any(id => dateOf.GetValueOrDefault(id) == rev.Date);
            return new SheetState(t.UniqueId, keep, untick ? t.Ticked.Where(n => n != column!.Number).ToList() : null);
        }).ToList());
    }

    private async void DeleteSheets_Click(object sender, RoutedEventArgs e)
    {
        var targets = IssueTargets();
        if (targets.Count == 0) { Status("Centang sheet yang akan dihapus."); return; }
        var list = string.Join("\n", targets.Take(10).Select(t => $"{t.Number}  {t.Name}")) + (targets.Count > 10 ? "\n…" : "");
        if (MessageBox.Show(this, $"Hapus {targets.Count} sheet dari model?\n\n{list}\n\nView di dalamnya tidak ikut terhapus. Bisa dibatalkan dengan Undo di Revit.",
                "QA Revit", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        try
        {
            var ids = targets.Select(t => t.UniqueId).ToList();
            var result = await _runner.Run(app => IssueService.Delete(app.ActiveUIDocument.Document, ids));
            ShowApplyResult(result);
            await RunQaAsync();
        }
        catch (Exception ex) { Error("Hapus sheet gagal", ex); }
    }

    private async void IssueStage_Click(object sender, RoutedEventArgs e)
    {
        var targets = IssueTargets().Select(t => t.UniqueId).ToHashSet();
        if (_report is null || targets.Count == 0) { Status("Centang sheet yang akan diganti stage-nya."); return; }
        if (IssueStageCombo.SelectedItem is not StageItem stage) { Status("Pilih stage dulu."); return; }

        var changes = RegisterComparer.PlanStageChange(_report.Sheets.Where(s => targets.Contains(s.UniqueId)).ToList(), stage.Code);
        if (changes.Count == 0) { Status($"Tidak ada yang diubah: sheet terpilih sudah stage {stage.Code} atau nomornya tidak mengikuti pola."); return; }
        var skipped = targets.Count - changes.Count;
        var sample = string.Join("\n", changes.Take(5).Select(c => $"{c.Number}  →  {c.NewNumber}")) + (changes.Count > 5 ? "\n…" : "");
        if (MessageBox.Show(this, $"Ganti stage {changes.Count} sheet ke {stage.Label}?\n\n{sample}" +
                (skipped > 0 ? $"\n\n{skipped} sheet dilewati (sudah stage itu, atau nomornya tidak mengikuti pola)." : ""),
                "QA Revit", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var result = await _runner.Run(app => SheetEditor.Apply(app.ActiveUIDocument.Document, changes, "QA Revit - Ganti Stage"));
            ShowApplyResult(result);
            await RunQaAsync();
        }
        catch (Exception ex) { Error("Ganti stage gagal", ex); }
    }

    // Ketik langsung di tabel: nomor, judul, note.
    private void IssueGrid_CellEditEnding(object? sender, System.Windows.Controls.DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != System.Windows.Controls.DataGridEditAction.Commit || e.Row.Item is not IssueRow row ||
            e.EditingElement is not System.Windows.Controls.TextBox box) return;
        var header = e.Column.Header as string ?? "";
        var (old, value) = (header.StartsWith("NOMOR") ? row.Number : header.StartsWith("JUDUL") ? row.Name : row.Note, box.Text.Trim());
        if (value == old) return;
        if (value.Length == 0 && !header.StartsWith("NOTE")) { e.Cancel = true; Status("Nomor dan judul tidak boleh kosong."); return; }

        var (id, number) = (row.UniqueId, row.Number);
        // Tunda sampai grid selesai commit; tabel tidak boleh di-refresh di tengah edit.
        Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                var result = await _runner.Run(app =>
                {
                    var doc = app.ActiveUIDocument.Document;
                    if (header.StartsWith("NOTE")) return IssueService.SetNote(doc, id, value);
                    var renumber = header.StartsWith("NOMOR");
                    return SheetEditor.Apply(doc, new[]
                    {
                        new SheetChange
                        {
                            Kind = renumber ? ChangeKind.RenumberSheet : ChangeKind.RenameSheet, UniqueId = id, Number = number,
                            NewNumber = renumber ? value : null, NewName = renumber ? null : value,
                        },
                    }, "QA Revit - Edit Sheet");
                });
                ShowApplyResult(result);
                await RunQaAsync();
            }
            catch (Exception ex) { Error("Edit sheet gagal", ex); }
        });
    }

    // ---------- Pilihan per sheet (popup) ----------

    public sealed class PickItem
    {
        public bool Checked { get; set; }
        public string Label { get; init; } = "";
        public string Key { get; init; } = "";
    }

    private IssueRow? _pickRow;
    private bool _pickRevisions;

    private void PickRevisions_Click(object sender, RoutedEventArgs e) => OpenPick(sender, revisions: true);
    private void PickColumns_Click(object sender, RoutedEventArgs e) => OpenPick(sender, revisions: false);

    private void OpenPick(object sender, bool revisions)
    {
        if (_issues is null || (sender as FrameworkElement)?.DataContext is not IssueRow row) return;
        if (!revisions && _issues.Columns.Count == 0) { Status("Sheet list dengan kolom 'ACT ISSUE NN' tidak ditemukan."); return; }
        (_pickRow, _pickRevisions) = (row, revisions);
        PickTitle.Text = (revisions ? "Revisi di sheet " : "Kolom sheet list untuk ") + row.Number;
        PickHint.Text = revisions
            ? "Centang revisi yang harus ada di sheet ini. Lepas centang untuk membatalkan revisi (mis. terlanjur C padahal masih B)."
            : "Centang kolom tanggal issue yang harus terisi untuk sheet ini.";
        PickList.ItemsSource = revisions
            ? _issues.Revisions.Select(r => new PickItem { Key = r.Id.ToString(), Label = r.Label, Checked = row.RevisionIds.Contains(r.Id) }).ToList()
            : _issues.Columns.Select(c => new PickItem { Key = c.Number, Label = c.Label, Checked = row.Ticked.Contains(c.Number) }).ToList();
        PickPopup.PlacementTarget = (UIElement)sender;
        PickPopup.IsOpen = true;
    }

    private void PickCancel_Click(object sender, RoutedEventArgs e) => PickPopup.IsOpen = false;

    private async void PickApply_Click(object sender, RoutedEventArgs e)
    {
        PickPopup.IsOpen = false;
        if (_pickRow is null || PickList.ItemsSource is not List<PickItem> items) return;
        var keys = items.Where(i => i.Checked).Select(i => i.Key).ToList();
        await RunStatesAsync(new List<SheetState>
        {
            _pickRevisions
                ? new SheetState(_pickRow.UniqueId, keys.Select(long.Parse).ToList(), null)
                : new SheetState(_pickRow.UniqueId, null, keys),
        });
    }

    private void Status_Click(object sender, RoutedEventArgs e)
    {
        if (_issues is null || sender is not FrameworkElement cell || cell.DataContext is not IssueRow row) return;
        if (row.Synced) { Status($"{row.Number}: centang sheet list sudah cocok dengan revisinya."); return; }

        var data = _issues;
        var byRevisions = new System.Windows.Controls.MenuItem { Header = "Revisi yang benar → samakan centang kolom sheet list" };
        byRevisions.Click += async (_, _) =>
        {
            var dates = data.Revisions.Where(r => row.RevisionIds.Contains(r.Id)).Select(r => r.Date).ToHashSet();
            await RunStatesAsync(new List<SheetState>
            {
                new(row.UniqueId, null, data.Columns.Where(c => !c.IsFree && dates.Contains(c.Heading)).Select(c => c.Number).ToList()),
            });
        };
        var byColumns = new System.Windows.Controls.MenuItem { Header = "Centang yang benar → samakan revisi di sheet" };
        byColumns.Click += async (_, _) =>
        {
            var ids = new List<long>();
            foreach (var column in data.Columns.Where(c => row.Ticked.Contains(c.Number)))
            {
                // Beberapa revisi bisa bertanggal sama: pertahankan yang sudah di sheet, kalau belum ada ambil yang pertama.
                var sameDate = data.Revisions.Where(r => r.Date == column.Heading).ToList();
                var onSheet = sameDate.Where(r => row.RevisionIds.Contains(r.Id)).ToList();
                ids.AddRange((onSheet.Count > 0 ? onSheet : sameDate.Take(1)).Select(r => r.Id));
            }
            await RunStatesAsync(new List<SheetState> { new(row.UniqueId, ids, null) });
        };
        cell.ContextMenu = new System.Windows.Controls.ContextMenu { Items = { byRevisions, byColumns }, PlacementTarget = cell,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, IsOpen = true };
    }

    private async void RunQa_Click(object sender, RoutedEventArgs e) => await RunQaAsync();

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (SearchHint is not null) SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private void ChatInput_TextChanged(object sender, RoutedEventArgs e) =>
        ChatHint.Visibility = ChatInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyFilter()
    {
        if (_report is null) return;
        IEnumerable<QaIssue> items = _report.Issues;
        if (HideInfoCheck.IsChecked == true) items = items.Where(i => i.Severity != Severity.Info);
        var q = SearchBox.Text.Trim();
        if (q.Length > 0)
            items = items.Where(i => i.RuleId.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                                     i.SheetNumber.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                     i.Message.Contains(q, StringComparison.OrdinalIgnoreCase));
        IssuesGrid.ItemsSource = items.ToList();
    }

    private async void GoTo_Click(object sender, RoutedEventArgs e)
    {
        if (IssuesGrid.SelectedItem is QaIssue { UniqueId: { } id }) await GoToAsync(id);
    }

    private async void DiffGoTo_Click(object sender, RoutedEventArgs e)
    {
        if (DiffGrid.SelectedItem is SheetChange { UniqueId: { } id }) await GoToAsync(id);
    }

    private async Task GoToAsync(string uniqueId)
    {
        try { await _runner.Run(app => SheetEditor.GoTo(app, uniqueId)); }
        catch (Exception ex) { Error("Tidak bisa membuka sheet", ex); }
    }

    private async void FixSelected_Click(object sender, RoutedEventArgs e) =>
        await FixAsync(IssuesGrid.SelectedItems.OfType<QaIssue>().ToList());

    private async void FixAll_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        // Perbaikan massal hanya untuk judul (aman). Renumber tetap dipilih manual.
        var renames = _report.Issues.Where(i => i.CanFix && i.Fix == FixKind.RenameSheet).ToList();
        if (renames.Count == 0) { Status("Tidak ada perbaikan judul otomatis."); return; }
        if (MessageBox.Show(this, $"Perbaiki {renames.Count} judul sheet (spasi/typo)?", "QA Revit",
                MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        await FixAsync(renames);
    }

    private async Task FixAsync(List<QaIssue> issues)
    {
        var fixable = issues.Where(i => i.CanFix).ToList();
        if (fixable.Count == 0) { Status("Temuan terpilih tidak punya perbaikan otomatis."); return; }
        try
        {
            var result = await _runner.Run(app => SheetEditor.ApplyFixes(app.ActiveUIDocument.Document, fixable));
            ShowApplyResult(result);
            await RunQaAsync();
        }
        catch (Exception ex) { Error("Fix gagal", ex); }
    }

    // ---------- Register ----------

    public void StartImport() => ImportExcel_Click(this, new RoutedEventArgs());

    private async void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", Title = "Pilih drawing list Excel" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var read = RegisterReader.Read(dlg.FileName);
            _register = read.Entries;
            RegisterInfo.Text = $"{System.IO.Path.GetFileName(dlg.FileName)} — format {read.Format}, {read.Entries.Count} drawing." +
                                (read.Warnings.Count > 0 ? "  ⚠ " + string.Join(" ", read.Warnings) : "");
            Tabs.SelectedIndex = 1;
            await RunQaAsync();
        }
        catch (Exception ex) { Error("Import Excel gagal", ex); }
    }

    private void SelectAllChanges_Click(object sender, RoutedEventArgs e) => SetAllSelected(true);
    private void ClearChanges_Click(object sender, RoutedEventArgs e) => SetAllSelected(false);

    private void SetAllSelected(bool value)
    {
        foreach (var c in _changes.Where(c => c.Applicable)) c.Selected = value;
        DiffGrid.Items.Refresh();
    }

    private void PreviewStage_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null || StageCombo.SelectedItem is not StageItem stage) return;
        _changes = RegisterComparer.PlanStageChange(_report.Sheets, stage.Code);
        DiffGrid.ItemsSource = _changes;
        RegisterInfo.Text = $"Preview ganti stage ke {stage.Label}: {_changes.Count} sheet akan di-renumber. Klik 'Terapkan ke Revit'.";
    }

    private async void ApplyRegister_Click(object sender, RoutedEventArgs e)
    {
        var selected = _changes.Where(c => c.Selected && c.Applicable).ToList();
        if (selected.Count == 0) { Status("Tidak ada perubahan yang dipilih."); return; }

        var regressions = selected.Where(c => c.Message.Contains("stage mundur")).ToList();
        var warn = regressions.Count > 0 ? $"\n\n⚠ {regressions.Count} perubahan menurunkan stage (stage mundur)." : "";
        if (MessageBox.Show(this, $"Terapkan {selected.Count} perubahan ke model?{warn}", "QA Revit",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        try
        {
            var defaultKop = (NewSheetKopCombo.SelectedItem as TitleBlockOption)?.Id;
            var result = await _runner.Run(app => SheetEditor.Apply(app.ActiveUIDocument.Document, selected, defaultTitleBlockTypeId: defaultKop));
            ShowApplyResult(result);
            await RunQaAsync();
        }
        catch (Exception ex) { Error("Gagal menerapkan perubahan", ex); }
    }

    private void ExportRegister_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = "DRAWING_LIST_FROM_MODEL.xlsx" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ReportExporter.ExportRegister(dlg.FileName, _report.Sheets, _issues is null ? null : RevStates(),
                _issues?.Revisions.Select(r => new RevisionColumn(r.Sequence, r.Date, r.Description)).ToList(), _report);
            Status($"Register disimpan: {dlg.FileName}");
        }
        catch (Exception ex) { Error("Export gagal", ex); }
    }

    // ---------- Excel yang sudah diedit → model ----------

    /// <summary>REV_NOTE dan revisi tiap sheet sekarang, memakai nomor urut revisi seperti di kolom Excel.</summary>
    private List<SheetRevState> RevStates()
    {
        if (_issues is null) return new();
        var seqOf = _issues.Revisions.ToDictionary(r => r.Id, r => r.Sequence);
        return _issueRows.Select(r => new SheetRevState(r.UniqueId, r.Number, r.Note,
            r.RevisionIds.Where(seqOf.ContainsKey).Select(id => seqOf[id]).ToList())).ToList();
    }

    /// <summary>Semua beda Excel terlampir vs model: sheet (buat / nomor / judul) dan revisi + note.</summary>
    private (List<SheetChange> Sheets, List<ExtraChange> Extras) ExcelPlan() =>
        (_changes.Where(c => c.Applicable).ToList(),
            _register is null ? new List<ExtraChange>() : RegisterComparer.PlanExtras(_register, RevStates()));

    private async void AttachExcel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", Title = "Pilih drawing list Excel yang sudah diedit" };
        if (dlg.ShowDialog(this) == true) await AttachExcelAsync(dlg.FileName);
    }

    private async void Advice_Drop(object sender, DragEventArgs e)
    {
        var file = (e.Data.GetData(DataFormats.FileDrop) as string[])?
            .FirstOrDefault(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase));
        if (file is null) { Status("Seret file Excel (.xlsx)."); return; }
        await AttachExcelAsync(file);
    }

    private async Task AttachExcelAsync(string path)
    {
        try
        {
            var name = System.IO.Path.GetFileName(path);
            var read = RegisterReader.Read(path);
            _register = read.Entries;
            RegisterInfo.Text = $"{name} — format {read.Format}, {read.Entries.Count} drawing." +
                                (read.Warnings.Count > 0 ? "  ⚠ " + string.Join(" ", read.Warnings) : "");
            await RunQaAsync();
            AddBubble($"📎 {name}  ({read.Entries.Count} drawing)", fromUser: true);
            if (read.Entries.Count == 0)
            {
                AddBubble("Tidak ada drawing yang terbaca dari file itu. " + string.Join(" ", read.Warnings), fromUser: false);
                return;
            }
            ShowExcelPlan();
        }
        catch (Exception ex) { Error("Baca Excel gagal", ex); }
    }

    private void ShowExcelPlan()
    {
        var (sheets, extras) = ExcelPlan();
        var missing = _changes.Count(c => c.Kind == ChangeKind.MissingInRegister);
        var missingNote = missing > 0 ? $"\n\n*{missing} sheet ada di model tetapi tidak ada di Excel. Sheet itu tidak dihapus.*" : "";
        if (sheets.Count + extras.Count == 0)
        {
            AddBubble("Excel sudah sama dengan model: tidak ada yang perlu diubah." + missingNote, fromUser: false);
            return;
        }

        var sb = new System.Text.StringBuilder($"## Excel berbeda dengan model di {sheets.Count + extras.Count} hal\n");
        void Count(ChangeKind kind, string label)
        {
            var n = sheets.Count(c => c.Kind == kind);
            if (n > 0) sb.Append($"- **{n}** {label}\n");
        }
        Count(ChangeKind.CreateSheet, "sheet baru dibuat");
        Count(ChangeKind.RenumberSheet, "nomor sheet diganti");
        Count(ChangeKind.RenameSheet, "judul diganti");
        if (extras.Count > 0) sb.Append($"- **{extras.Count}** sheet berubah revisi atau note\n");

        const int max = 12;
        sb.Append("\n### Rincian\n");
        var lines = sheets.Select(c => c.Kind switch
            {
                ChangeKind.CreateSheet => $"**{c.Number}** dibuat: {c.Name}",
                ChangeKind.RenumberSheet => $"**{c.Number}** → nomor {c.NewNumber}",
                _ => $"**{c.Number}** → judul {c.NewName}",
            })
            .Concat(extras.Select(x => $"**{x.Number}** → {x.Message}")).ToList();
        foreach (var line in lines.Take(max)) sb.Append($"- {line}\n");
        if (lines.Count > max) sb.Append($"- … dan {lines.Count - max} lainnya (lihat tab Register Excel)\n");
        sb.Append(missingNote);

        FrameworkElement? bubble = null;
        bubble = AddBubble(sb.ToString(), fromUser: false, actions: new (string, Action)[]
        {
            ("✔ Terapkan semua ke Revit", async () => { bubble!.IsEnabled = false; if (!await ApplyExcelAsync()) bubble.IsEnabled = true; }),
            ("Lihat di tab Register Excel", () => Tabs.SelectedIndex = 1),
        });
    }

    /// <returns>False kalau dibatalkan di konfirmasi.</returns>
    private async Task<bool> ApplyExcelAsync()
    {
        var (sheets, firstExtras) = ExcelPlan();
        if (MessageBox.Show(this, $"Terapkan {sheets.Count + firstExtras.Count} perubahan dari Excel ke model?", "QA Revit",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return false;
        var applied = 0;
        var errors = new List<string>();
        try
        {
            if (sheets.Count > 0)
            {
                var defaultKop = (NewSheetKopCombo.SelectedItem as TitleBlockOption)?.Id;
                var r = await _runner.Run(app => SheetEditor.Apply(app.ActiveUIDocument.Document, sheets, defaultTitleBlockTypeId: defaultKop));
                applied += r.Applied;
                errors.AddRange(r.Errors);
                await RunQaAsync();
            }

            // Revisi dan note dihitung ulang setelah nomor baru / sheet baru masuk, supaya sheet itu ikut terisi.
            var extras = ExcelPlan().Extras;
            if (extras.Count > 0 && _issues is { } data)
            {
                var bySeq = data.Revisions.ToDictionary(r => r.Sequence);
                var states = extras.Select(x =>
                {
                    if (x.NewRevisionSeqs is null) return new SheetState(x.UniqueId, null, null, x.NewNote);
                    foreach (var unknown in x.NewRevisionSeqs.Where(s => !bySeq.ContainsKey(s)))
                        errors.Add($"{x.Number}: Seq {unknown} tidak ada di model. Buat dulu revisinya di Sheet Issues/Revisions.");
                    var revs = x.NewRevisionSeqs.Where(bySeq.ContainsKey).Select(s => bySeq[s]).ToList();
                    var dates = revs.Select(r => r.Date).ToHashSet();
                    // Kolom sheet list ikut disamakan dengan revisi yang baru.
                    var columns = data.Columns.Count == 0 ? null
                        : data.Columns.Where(c => !c.IsFree && dates.Contains(c.Heading)).Select(c => c.Number).ToList();
                    return new SheetState(x.UniqueId, revs.Select(r => r.Id).ToList(), columns, x.NewNote);
                }).ToList();
                var approver = IssueApvBox.Text.Trim();
                var r = await _runner.Run(app => IssueService.SetStates(app.ActiveUIDocument.Document, states, data.Columns, approver));
                applied += r.Applied;
                errors.AddRange(r.Errors);
                await RunQaAsync();
            }

            var (leftSheets, leftExtras) = ExcelPlan();
            var left = leftSheets.Count + leftExtras.Count;
            var text = $"**{applied} perubahan diterapkan ke model.** " +
                       (left == 0 ? "Model sekarang sama dengan Excel." : $"Masih ada {left} beda yang belum bisa disamakan.");
            if (errors.Count > 0)
                text += "\n\n### Tidak berhasil\n" + string.Join("\n", errors.Take(10).Select(x => $"- {x}")) +
                        (errors.Count > 10 ? $"\n- … dan {errors.Count - 10} lainnya" : "");
            AddBubble(text + "\n\n*Bisa dibatalkan dengan Undo di Revit.*", fromUser: false);
        }
        catch (Exception ex) { Error("Terapkan Excel gagal", ex); }
        return true;
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = $"QA_REPORT_{DateTime.Now:yyyyMMdd_HHmm}.xlsx" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ReportExporter.ExportQa(dlg.FileName, _report, _changes, _lastAiAdvice);
            Status($"Laporan disimpan: {dlg.FileName}");
        }
        catch (Exception ex) { Error("Export gagal", ex); }
    }

    // ---------- Advice ----------

    private AdviceView? _adviceView;

    /// <summary>Ringkasan dan kartu per aturan (offline). Chat AI di bawahnya tidak ikut dihapus.</summary>
    private void ShowRuleAdvice()
    {
        if (_report is null) return;
        var view = _adviceView ??= new AdviceView(this);
        RulePanel.Children.Clear();
        RulePanel.Children.Add(view.Summary(_report));
        foreach (var g in RuleAdvice.Groups(_report))
            RulePanel.Children.Add(view.RuleCard(g, ShowRuleInResults, id => _ = GoToAsync(id)));
        var applicable = _changes.Count(c => c.Applicable);
        if (applicable > 0)
            RulePanel.Children.Add(view.Bubble($"Register Excel berbeda dengan model di {applicable} sheet. Lihat tab Register Excel.", fromUser: false, muted: true));
    }

    private void ShowRuleInResults(string ruleId)
    {
        SearchBox.Text = ruleId;
        Tabs.SelectedIndex = 0;
    }

    private FrameworkElement AddBubble(string text, bool fromUser, bool muted = false, (string Label, Action Run)[]? actions = null)
    {
        var view = _adviceView ??= new AdviceView(this);
        if (ChatPanel.Children.Count == 0) ChatPanel.Children.Add(view.SectionTitle("Percakapan dengan AI"));
        var bubble = view.Bubble(text, fromUser, muted, actions ?? Array.Empty<(string, Action)>());
        ChatPanel.Children.Add(bubble);
        AdviceScroll.ScrollToEnd();
        return bubble;
    }

    /// <summary>Tampilkan pertanyaan, penanda "sedang menulis", lalu jawabannya.</summary>
    private async Task<string> AskInChatAsync(string shown, Func<Task<string>> ask)
    {
        AddBubble(shown, fromUser: true);
        var waiting = AddBubble("AI sedang menulis…", fromUser: false, muted: true);
        try
        {
            var answer = await ask();
            ChatPanel.Children.Remove(waiting);
            AddBubble(answer, fromUser: false);
            return answer;
        }
        finally { ChatPanel.Children.Remove(waiting); }
    }

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        ChatPanel.Children.Clear();
        if (_report is not null) _advice?.SetContext(_report, _config, _changes);
        AdviceScroll.ScrollToTop();
    }

    private async void Quick_Click(object sender, RoutedEventArgs e)
    {
        ChatInput.Text = (sender as System.Windows.Controls.Button)?.Content as string ?? "";
        await SendChatAsync();
    }

    private async void AiAdvice_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        if (_advice is null) { Settings_Click(sender, e); return; }
        await WithBusy(async () =>
        {
            Status("Meminta advice AI…");
            _lastAiAdvice = await AskInChatAsync("Buat rencana perbaikan dari hasil QA ini.",
                () => _advice.GenerateAdviceAsync(_report, _config, _changes));
            Status("Advice AI selesai.");
        });
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        var q = ChatInput.Text.Trim();
        if (q.Length == 0) return;
        if (_advice is null) { Settings_Click(this, new RoutedEventArgs()); return; }
        await WithBusy(async () =>
        {
            ChatInput.Clear();
            await AskInChatAsync(q, () => _advice.ChatAsync(q));
        });
    }

    private async Task WithBusy(Func<Task> work)
    {
        AiButton.IsEnabled = SendButton.IsEnabled = QuickPanel.IsEnabled = false;
        try { await work(); }
        catch (Exception ex) { Error("AI gagal", ex); }
        finally { AiButton.IsEnabled = SendButton.IsEnabled = QuickPanel.IsEnabled = true; }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings) { Owner = this };
        if (win.ShowDialog() != true) return;
        _settings = AddinSettings.Load();
        _config = LoadConfigSafe();
        RefreshSettingsUi();
        if (_report is not null) _advice?.SetContext(_report, _config, _changes);
    }

    // ---------- util ----------

    private void ShowApplyResult(ApplyResult r)
    {
        var msg = $"{r.Applied} perubahan diterapkan.";
        if (r.Errors.Count > 0)
            MessageBox.Show(this, msg + $"\n\n{r.Errors.Count} gagal:\n" + string.Join("\n", r.Errors.Take(20)), "QA Revit",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        Status(msg);
    }

    private void Status(string text) => StatusText.Text = text;

    private void Error(string title, Exception ex)
    {
        Status($"{title}: {ex.Message}");
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
