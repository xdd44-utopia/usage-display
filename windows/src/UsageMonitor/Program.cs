using System.Diagnostics;

namespace UsageMonitor;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains(ClaudeStatusLine.Argument)) return ClaudeStatusLine.Publish();
        if (args.Contains(ClaudeStatusLine.RemoveArgument))
        {
            new ClaudeStatusLine(ClaudeStatusLine.ConfigDirectory, Environment.ProcessPath!).Remove();
            return 0;
        }
        Log.AttachToTerminal();
        if (AppLocation.IsPublished) UpdateCheck.CleanUp();

        string? installer;
        using (var instance = new Mutex(true, @"Local\UsageMonitor", out var first))
        {
            if (!first)
            {
                Log.Write("Usage Monitor is already running; quit it first");
                return 0;
            }

            ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // Dark mode is still marked experimental; it styles the menu and the panel's controls.
            Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            using var app = new TrayApp();
            Log.QuitOnCtrlC(app.Quit);
            if (app.Start()) Application.Run(app);
            installer = app.PendingInstaller;
        }

        // The installer refuses to run while the app holds its mutex, so it starts only after that is released.
        if (installer is not null)
        {
            try
            {
                if (!File.Exists(installer)) throw new FileNotFoundException("The downloaded installer is gone", installer);
                UpdateCheck.StartInstaller(installer);
            }
            catch (Exception e)
            {
                Log.Write($"Could not start the installer: {e.Message}; relaunching");
                try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true }); }
                catch (Exception) { }
            }
        }
        return 0;
    }
}
