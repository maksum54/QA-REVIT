using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QARevit.Core.Ai;
using QARevit.Core.Config;

namespace QARevit.Addin.UI;

public partial class SettingsWindow : Window
{
    private readonly AddinSettings _settings;
    private bool _loading = true;

    public SettingsWindow(AddinSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ProviderCombo.SelectedIndex = settings.Ai.Provider == AiProvider.Anthropic ? 0 : 1;
        BaseUrlBox.Text = settings.Ai.BaseUrl;
        ModelBox.Text = settings.Ai.Model;
        KeyBox.Password = settings.Ai.ApiKey;
        ConfigBox.Text = settings.ConfigPath ?? "";
        _loading = false;
    }

    private AiProvider SelectedProvider =>
        (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string == "Anthropic" ? AiProvider.Anthropic : AiProvider.OpenAiCompatible;

    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (SelectedProvider == AiProvider.Anthropic)
        {
            BaseUrlBox.Text = "https://api.anthropic.com";
            ModelBox.Text = new AiSettings().Model;
        }
        else
        {
            BaseUrlBox.Text = "https://api.openai.com/v1";
            ModelBox.Text = "";
        }
    }

    private AiSettings ReadAi() => new()
    {
        Provider = SelectedProvider,
        BaseUrl = BaseUrlBox.Text.Trim(),
        Model = ModelBox.Text.Trim(),
        ApiKey = KeyBox.Password.Trim(),
        MaxTokens = _settings.Ai.MaxTokens,
    };

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var ai = ReadAi();
        if (!ai.IsConfigured) { TestResult.Text = "Isi model dan API key dulu."; return; }
        TestResult.Text = "Menghubungi…";
        try
        {
            var client = AiClientFactory.Create(ai);
            var reply = await client.CompleteAsync("Jawab singkat.", new[] { new ChatMessage("user", "Balas dengan kata: OK") });
            TestResult.Text = "Berhasil: " + reply.Trim();
        }
        catch (Exception ex) { TestResult.Text = "Gagal: " + ex.Message; }
    }

    private void BrowseConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dlg.ShowDialog(this) == true) ConfigBox.Text = dlg.FileName;
    }

    private void CreateConfig_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "qa-revit-config.json" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, QaConfig.DefaultJson());
        ConfigBox.Text = dlg.FileName;
        Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true });
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = ConfigBox.Text.Trim();
        if (path.Length > 0)
        {
            try { QaConfig.LoadFile(path); }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"File konfigurasi tidak valid:\n{ex.Message}", "QA Revit", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        _settings.Ai = ReadAi();
        _settings.ConfigPath = path.Length > 0 ? path : null;
        try { _settings.Save(); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Gagal menyimpan", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        DialogResult = true;
    }
}
