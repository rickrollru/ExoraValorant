using Exora.Core;

namespace Exora;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Exora.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            Native.FocusExistingWindow(MainForm.WindowTitle);
            return;
        }

        Log.Init();
        Log.Info($"Exora {AppInfo.Version} starting");

        Application.ThreadException += (_, e) => Log.Error("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
