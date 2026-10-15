namespace UsageDisplay;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains(ClaudeStatusLine.Argument)) return ClaudeStatusLine.Publish();
        Log.AttachToTerminal();

        using var instance = new Mutex(true, @"Local\UsageDisplay", out var first);
        if (!first)
        {
            Log.Write("Usage Display is already running; quit it first");
            return 0;
        }

        ApplicationConfiguration.Initialize();
#pragma warning disable WFO5001 // Dark mode is still marked experimental; it styles the menu and the panel's controls.
        Application.SetColorMode(SystemColorMode.System);
#pragma warning restore WFO5001
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        string? relaunch;
        using (var app = new TrayApp())
        {
            Log.QuitOnCtrlC(app.Quit);
            if (app.Start()) Application.Run(app);
            relaunch = app.RelaunchPath;
        }
        if (relaunch is not null)
        {
            instance.Dispose();
            AppLocation.Relaunch(relaunch);
        }
        return 0;
    }
}
