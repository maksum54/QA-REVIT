using System.Reflection;
using System.Windows.Interop;
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

        panel.AddItem(new PushButtonData("QaDashboard", "QA\nDashboard", asm, typeof(ShowDashboardCommand).FullName)
        {
            ToolTip = "Jalankan QA sheet, import drawing list Excel, lihat advice dan perbaiki.",
        });
        panel.AddItem(new PushButtonData("QaSettings", "Settings", asm, typeof(SettingsCommand).FullName)
        {
            ToolTip = "Pengaturan AI (Claude / API custom) dan file konfigurasi proyek.",
        });
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication app)
    {
        _dashboard?.Close();
        return Result.Succeeded;
    }

    internal static void ShowDashboard(UIApplication uiapp)
    {
        if (_dashboard is { IsLoaded: true })
        {
            _dashboard.Activate();
            return;
        }
        _dashboard = new DashboardWindow(Runner!);
        new WindowInteropHelper(_dashboard).Owner = uiapp.MainWindowHandle;
        _dashboard.Closed += (_, _) => _dashboard = null;
        _dashboard.Show();
        _dashboard.RunQaCommand();
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
