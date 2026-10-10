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
            var sheets = await _runner.Run(app => SheetCollector.Collect(app.ActiveUIDocument.Document));
            _report = new RuleEngine().Run(sheets, _config);
            if (_register is not null) _changes = RegisterComparer.Compare(_register, sheets, _config);

            TotalText.Text = _report.Total.ToString();
            PassedText.Text = _report.Passed.ToString();
            FailedText.Text = _report.Failed.ToString();
            RateText.Text = $"{_report.PassRate:P0}";
            ApplyFilter();
            DiffGrid.ItemsSource = _changes;
            AdviceBox.Text = RuleAdvice.Build(_report, _changes);
            _advice?.SetContext(_report, _config, _changes);
            Status($"QA selesai {DateTime.Now:HH:mm:ss}: {_report.Issues.Count} temuan dari {_report.Total} sheet.");
        }
        catch (Exception ex) { Error("QA gagal", ex); }
    }

    private async void RunQa_Click(object sender, RoutedEventArgs e) => await RunQaAsync();

    private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_report is null) return;
        IEnumerable<QaIssue> items = _report.Issues;
        if (HideInfoCheck.IsChecked == true) items = items.Where(i => i.Severity != Severity.Info);
        var q = SearchBox.Text.Trim();
        if (q.Length > 0)
            items = items.Where(i => i.SheetNumber.Contains(q, StringComparison.OrdinalIgnoreCase) ||
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
            var result = await _runner.Run(app => SheetEditor.Apply(app.ActiveUIDocument.Document, selected));
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
            ReportExporter.ExportRegister(dlg.FileName, _report.Sheets);
            Status($"Register disimpan: {dlg.FileName}");
        }
        catch (Exception ex) { Error("Export gagal", ex); }
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

    private void RuleAdvice_Click(object sender, RoutedEventArgs e)
    {
        if (_report is not null) AdviceBox.Text = RuleAdvice.Build(_report, _changes);
    }

    private async void AiAdvice_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        if (_advice is null) { Settings_Click(sender, e); return; }
        await WithBusy(async () =>
        {
            Status("Meminta advice AI…");
            _lastAiAdvice = await _advice.GenerateAdviceAsync(_report, _config, _changes);
            AdviceBox.Text = _lastAiAdvice;
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
            AdviceBox.AppendText($"\n\n▶ {q}\n");
            var answer = await _advice.ChatAsync(q);
            AdviceBox.AppendText(answer + "\n");
            AdviceBox.ScrollToEnd();
        });
    }

    private async Task WithBusy(Func<Task> work)
    {
        AiButton.IsEnabled = SendButton.IsEnabled = false;
        try { await work(); }
        catch (Exception ex) { Error("AI gagal", ex); }
        finally { AiButton.IsEnabled = SendButton.IsEnabled = true; }
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
