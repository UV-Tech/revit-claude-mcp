using Autodesk.Revit.UI;

namespace RevitMCP;

/// <summary>
/// IExternalApplication entry point.
/// Starts the embedded HTTP server when Revit loads and stops it on shutdown.
/// </summary>
public class App : IExternalApplication
{
    private RevitHttpServer? _server;
    internal static UIControlledApplication? UiApp;

    public Result OnStartup(UIControlledApplication application)
    {
        UiApp = application;
        _server = new RevitHttpServer(application);
        _server.Start();

        TaskDialog.Show("RevitMCP", "RevitMCP bridge started on http://localhost:6543");
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _server?.Stop();
        return Result.Succeeded;
    }
}
