using System;
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

        try
        {
            _server = new RevitHttpServer(application);
            _server.Start();

            if (Environment.GetEnvironmentVariable("REVIT_MCP_SHOW_STARTUP_DIALOG") == "1")
                TaskDialog.Show("RevitMCP", "RevitMCP bridge started on http://localhost:6543");

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("RevitMCP", $"Failed to start RevitMCP bridge on http://localhost:6543\n\n{ex.Message}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _server?.Stop();
        return Result.Succeeded;
    }
}
