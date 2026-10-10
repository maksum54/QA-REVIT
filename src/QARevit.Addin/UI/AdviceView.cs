using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using QARevit.Core.Ai;
using QARevit.Core.Model;

namespace QARevit.Addin.UI;

/// <summary>Membangun isi tab Advice: ringkasan, kartu per aturan, dan gelembung chat AI.</summary>
public sealed class AdviceView
{
    private const int MaxSheetChips = 8;
    private static readonly Regex Inline = new(@"(\*\*.+?\*\*|`[^`]+`)");
    private static readonly Regex Numbered = new(@"^(\d+)[.)]\s+(.*)$");

    private readonly FrameworkElement _owner;

    public AdviceView(FrameworkElement owner) => _owner = owner;

    private Brush B(string key) => (Brush)_owner.FindResource(key);

    private (Brush Fore, Brush Soft) Colors(Severity s) => s switch
    {
        Severity.Error => (B("Bad"), B("BadSoft")),
        Severity.Warning => (B("Warn"), B("WarnSoft")),
        _ => (B("Info"), B("InfoSoft")),
    };

    private static Border Chip(string text, Brush fore, Brush back) => new()
    {
        Background = back, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 6),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = fore, FontSize = 11, FontWeight = FontWeights.SemiBold },
    };

    private Border Card(UIElement child, Brush? accent = null) => new()
    {
        Background = Brushes.White, BorderBrush = accent ?? B("Border"), BorderThickness = new Thickness(accent is null ? 1 : 4, 1, 1, 1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 8), Margin = new Thickness(0, 0, 0, 10), Child = child,
    };

    /// <summary>Kalimat kesimpulan: siap issue atau belum.</summary>
    public UIElement Summary(QaReport report)
    {
        var errors = report.Issues.Count(i => i.Severity == Severity.Error);
        var warnings = report.Issues.Count(i => i.Severity == Severity.Warning);
        var (verdict, severity) = errors > 0 ? ($"Belum siap issue: {errors} error harus selesai dulu.", Severity.Error)
            : warnings > 0 ? ($"Hampir siap: tinggal {warnings} warning untuk diperiksa.", Severity.Warning)
            : ("Tidak ada temuan yang menghalangi. Set gambar siap di-issue.", Severity.Info);
        var (fore, _) = Colors(severity);
        if (errors == 0 && warnings == 0) fore = B("Good");

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = $"{report.Passed} dari {report.Total} sheet lulus ({report.PassRate:P0})", FontSize = 16, FontWeight = FontWeights.Bold, Foreground = B("Text"),
        });
        panel.Children.Add(new TextBlock { Text = verdict, Foreground = fore, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap });
        return Card(panel, fore);
    }

    public UIElement RuleCard(RuleAdviceGroup g, Action<string> showRule, Action<string> goToSheet)
    {
        var (fore, soft) = Colors(g.Severity);
        var panel = new StackPanel();

        var head = new WrapPanel();
        head.Children.Add(Chip(g.Severity.ToString().ToUpperInvariant(), fore, soft));
        head.Children.Add(new TextBlock { Text = g.Title, FontWeight = FontWeights.Bold, FontSize = 13.5, Foreground = B("Text"), Margin = new Thickness(0, 0, 8, 6), VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(Chip($"{g.Issues.Count} temuan", B("Muted"), B("PageBg")));
        if (g.Fixable > 0) head.Children.Add(Chip($"{g.Fixable} bisa Fix otomatis", B("AccentDark"), B("GoodSoft")));
        panel.Children.Add(head);

        panel.Children.Add(new TextBlock { Text = g.Advice, TextWrapping = TextWrapping.Wrap, Foreground = B("Text"), Margin = new Thickness(0, 0, 0, 8), LineHeight = 18 });

        // Sheet yang kena: klik untuk membuka sheet-nya.
        var sheets = new WrapPanel();
        var distinct = g.Issues.GroupBy(i => i.SheetNumber).Select(x => x.First()).ToList();
        foreach (var issue in distinct.Take(MaxSheetChips))
        {
            var chip = Chip(issue.SheetNumber, B("Text"), B("PageBg"));
            chip.ToolTip = issue.Message;
            if (issue.UniqueId is { } id)
            {
                chip.Cursor = System.Windows.Input.Cursors.Hand;
                chip.MouseLeftButtonUp += (_, _) => goToSheet(id);
            }
            sheets.Children.Add(chip);
        }
        if (distinct.Count > MaxSheetChips) sheets.Children.Add(Chip($"+{distinct.Count - MaxSheetChips} lagi", B("Muted"), Brushes.Transparent));
        var link = new TextBlock { Text = $"Lihat semua di Hasil QA  ›   ({g.RuleId})", Foreground = B("AccentDark"), FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(4, 2, 0, 6), VerticalAlignment = VerticalAlignment.Center };
        link.MouseLeftButtonUp += (_, _) => showRule(g.RuleId);
        sheets.Children.Add(link);
        panel.Children.Add(sheets);

        return Card(panel, fore);
    }

    public UIElement SectionTitle(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(), Foreground = B("Muted"), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 8, 0, 8),
    };

    /// <summary>Gelembung chat. Jawaban AI dirender dari markdown sederhana dan bisa disalin.</summary>
    public FrameworkElement Bubble(string text, bool fromUser, bool muted = false)
    {
        var content = new StackPanel();
        if (fromUser || muted)
            content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = fromUser ? Brushes.White : B("Muted"),
                FontStyle = muted ? FontStyles.Italic : FontStyles.Normal });
        else
        {
            foreach (var block in Markdown(text)) content.Children.Add(block);
            var copy = new TextBlock { Text = "⧉ Salin", Foreground = B("Muted"), FontSize = 11, Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
            copy.MouseLeftButtonUp += (_, _) =>
            {
                try { Clipboard.SetText(text); copy.Text = "✔ Tersalin"; } catch { /* clipboard sedang dipakai aplikasi lain */ }
            };
            content.Children.Add(copy);
        }
        return new Border
        {
            Background = fromUser ? B("Navy") : Brushes.White, BorderBrush = B("Border"), BorderThickness = new Thickness(fromUser ? 0 : 1),
            CornerRadius = fromUser ? new CornerRadius(12, 12, 2, 12) : new CornerRadius(12, 12, 12, 2),
            Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(fromUser ? 120 : 0, 0, fromUser ? 0 : 60, 10),
            HorizontalAlignment = fromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left, Child = content,
        };
    }

    private IEnumerable<UIElement> Markdown(string text)
    {
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) { yield return new Border { Height = 6 }; continue; }
            if (trimmed.All(c => c is '-' or '*' or '_') && trimmed.Length >= 3)
            {
                yield return new Border { Height = 1, Background = B("Border"), Margin = new Thickness(0, 6, 0, 6) };
                continue;
            }
            if (trimmed.StartsWith('#'))
            {
                var level = trimmed.TakeWhile(c => c == '#').Count();
                yield return Text(trimmed[level..].Trim(), bold: true, size: level <= 2 ? 14 : 13, margin: new Thickness(0, 8, 0, 4));
                continue;
            }
            var indent = (line.Length - trimmed.Length) / 2 * 14;
            var numbered = Numbered.Match(trimmed);
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("• ") || numbered.Success)
            {
                var row = new DockPanel { Margin = new Thickness(4 + indent, 2, 0, 2) };
                var marker = new TextBlock { Text = numbered.Success ? numbered.Groups[1].Value + "." : "•", Foreground = B("AccentDark"),
                    FontWeight = FontWeights.Bold, Width = numbered.Success ? 22 : 14 };
                DockPanel.SetDock(marker, Dock.Left);
                row.Children.Add(marker);
                row.Children.Add(Text(numbered.Success ? numbered.Groups[2].Value : trimmed[2..]));
                yield return row;
                continue;
            }
            yield return Text(trimmed, margin: new Thickness(indent, 2, 0, 2));
        }
    }

    private TextBlock Text(string text, bool bold = false, double size = 12.5, Thickness margin = default)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = B("Text"), FontSize = size, LineHeight = size + 6, Margin = margin,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal };
        foreach (var part in Inline.Split(text))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.Length > 4) block.Inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeights.Bold });
            else if (part.StartsWith('`') && part.Length > 2) block.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Background = B("PageBg") });
            else block.Inlines.Add(new Run(part));
        }
        return block;
    }
}
