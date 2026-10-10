using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QARevit.Core.Ai;
using QARevit.Core.Config;

namespace QARevit.Addin;

/// <summary>
/// Pengaturan per user di %AppData%\QARevit. API key disimpan terpisah, dienkripsi DPAPI (hanya bisa dibuka user Windows yang sama).
/// </summary>
public sealed class AddinSettings
{
    public AiSettings Ai { get; set; } = new();
    /// <summary>File konfigurasi proyek (kode proyek/stage/sistem). Kosong = pakai bawaan.</summary>
    public string? ConfigPath { get; set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QARevit");
    private static string SettingsFile => Path.Combine(Folder, "settings.json");
    private static string KeyFile => Path.Combine(Folder, "apikey.bin");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QARevit.ApiKey.v1");

    public static AddinSettings Load()
    {
        AddinSettings s;
        try
        {
            s = File.Exists(SettingsFile)
                ? JsonSerializer.Deserialize<AddinSettings>(File.ReadAllText(SettingsFile), QaConfig.JsonOptions) ?? new()
                : new();
        }
        catch (Exception) { s = new(); }

        try
        {
            if (File.Exists(KeyFile))
                s.Ai.ApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { s.Ai.ApiKey = ""; }
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, QaConfig.JsonOptions));
        if (string.IsNullOrEmpty(Ai.ApiKey))
        {
            if (File.Exists(KeyFile)) File.Delete(KeyFile);
        }
        else
        {
            File.WriteAllBytes(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(Ai.ApiKey), Entropy, DataProtectionScope.CurrentUser));
        }
    }

    public QaConfig LoadConfig() => QaConfig.LoadOrDefault(ConfigPath);
}
