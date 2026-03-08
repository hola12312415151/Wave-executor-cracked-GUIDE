using System;
using System.Diagnostics;
using System.IO;

internal static class WavePatchedLauncherNew
{
    private static int Main()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var target = Path.Combine(baseDir, "Wave_New_Original.exe");
        var hook = Path.Combine(baseDir, "BypassHook_New.dll");
        if (!File.Exists(target))
        {
            Console.Error.WriteLine("Missing " + target);
            return 1;
        }
        if (!File.Exists(hook))
        {
            Console.Error.WriteLine("Missing " + hook);
            return 2;
        }

        var psi = new ProcessStartInfo(target);
        psi.UseShellExecute = false;
        psi.EnvironmentVariables["DOTNET_STARTUP_HOOKS"] = hook;
        psi.WorkingDirectory = baseDir;
        var p = Process.Start(psi);
        if (p == null)
            return 3;
        return 0;
    }
}
