using System.Reflection;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using QARevit.Addin.Revit;
using QARevit.Addin.UI;

namespace QARevit.Addin;

public sealed class App : IExternalApplication
{
    internal static RevitTaskRunner? Runner { get; private set; }
    private static DashboardWindow? _dashboard;

    public Result OnStartup(UIControlledApplication app)
    {
        Runner = new RevitTaskRunner();

        const string tab = "QA Revit";
        try { app.CreateRibbonTab(tab); } catch (Autodesk.Revit.Exceptions.ArgumentException) { /* sudah ada */ }
        var panel = app.CreateRibbonPanel(tab, "Drawing QA");
        var asm = Assembly.GetExecutingAssembly().Location;

        panel.AddItem(Button("QaDashboard", "QA\nDashboard", asm, typeof(ShowDashboardCommand), "dashboard",
            "Jalankan QA semua sheet: format nomor, stage, judul, revisi. Lihat hasil, Go To, dan Fix."));
        panel.AddItem(Button("QaImport", "Import\nExcel", asm, typeof(ImportExcelCommand), "excel",
            "Import drawing list Excel dan bandingkan dengan sheet di model."));
        panel.AddSeparator();
        panel.AddItem(Button("QaSettings", "Settings", asm, typeof(SettingsCommand), "settings",
            "Pengaturan AI (Claude / API custom) dan file konfigurasi proyek."));
        return Result.Succeeded;
    }

    private static PushButtonData Button(string id, string text, string asm, Type command, string icon, string tooltip) =>
        new(id, text, asm, command.FullName)
        {
            ToolTip = tooltip,
            LargeImage = Icon($"{icon}32.png"),
            Image = Icon($"{icon}16.png"),
        };

    internal static ImageSource? Icon(string file)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"QARevit.Resources.{file}");
        if (stream is null) return null;
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad; // baca sekarang, stream boleh ditutup
        image.EndInit();
        image.Freeze();
        return image;
    }

    public Result OnShutdown(UIControlledApplication app)
    {
        _dashboard?.Close();
        return Result.Succeeded;
    }

    internal static void ShowDashboard(UIApplication uiapp, bool startImport = false)
    {
        if (_dashboard is { IsLoaded: true })
        {
            _dashboard.Activate();
            if (startImport) _dashboard.StartImport();
            return;
        }
        _dashboard = new DashboardWindow(Runner!);
        new WindowInteropHelper(_dashboard).Owner = uiapp.MainWindowHandle;
        _dashboard.Closed += (_, _) => _dashboard = null;
        _dashboard.Show();
        if (startImport) _dashboard.StartImport();
        else _dashboard.RunQaCommand();
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class ShowDashboardCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        if (data.Application.ActiveUIDocument is null)
        {
            message = "Buka project Revit terlebih dahulu.";
            return Result.Failed;
        }
        App.ShowDashboard(data.Application);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class ImportExcelCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        if (data.Application.ActiveUIDocument is null)
        {
            message = "Buka project Revit terlebih dahulu.";
            return Result.Failed;
        }
        App.ShowDashboard(data.Application, startImport: true);
        return Result.Succeeded;
    }
}

[Transaction(TransactionMode.Manual)]
public sealed class SettingsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var win = new SettingsWindow(AddinSettings.Load());
        new WindowInteropHelper(win).Owner = data.Application.MainWindowHandle;
        win.ShowDialog();
        return Result.Succeeded;
    }
}
