using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QARevit.Core.Config;

public sealed class StageDef
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class SystemDef
{
    public string Name { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
}

public sealed class DrawingTypeDef
{
    public string Name { get; set; } = "";
    public List<string> Keywords { get; set; } = new();
    public bool MustContainKeyword { get; set; }
}

public sealed class ZoneCompletenessDef
{
    public bool Enabled { get; set; }
    public List<string> WhenAnyOf { get; set; } = new();
    public List<string> Expect { get; set; } = new();
}

/// <summary>Aturan penomoran dan QA per proyek. Bisa diedit lewat file JSON tanpa ubah kode.</summary>
public sealed class QaConfig
{
    public string DocType { get; set; } = "DWG";
    public string Discipline { get; set; } = "ME";
    public List<string> ProjectCodes { get; set; } = new();
    /// <summary>Urutan penting: index kecil = stage lebih awal (CD → BD → DD → DC).</summary>
    public List<StageDef> Stages { get; set; } = new();
    public Dictionary<string, SystemDef> Systems { get; set; } = new();
    public Dictionary<string, DrawingTypeDef> DrawingTypes { get; set; } = new();
    public ZoneCompletenessDef ZoneCompleteness { get; set; } = new();
    public Dictionary<string, string> Typos { get; set; } = new();
    public string ConstructionStage { get; set; } = "DC";
    public List<string> ConstructionRequiredParameters { get; set; } = new();

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public int StageIndex(string? code) =>
        code is null ? -1 : Stages.FindIndex(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));

    public string StageName(string? code) =>
        Stages.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase))?.Name ?? code ?? "";

    public static QaConfig LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QARevit.Core.default-config.json")
            ?? throw new InvalidOperationException("default-config.json tidak ditemukan.");
        return JsonSerializer.Deserialize<QaConfig>(stream, JsonOptions)!;
    }

    public static string DefaultJson()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("QARevit.Core.default-config.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static QaConfig LoadFile(string path) =>
        JsonSerializer.Deserialize<QaConfig>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Config kosong: {path}");

    /// <summary>Pakai file kalau ada, kalau tidak pakai default bawaan.</summary>
    public static QaConfig LoadOrDefault(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? LoadFile(path) : LoadDefault();
}
