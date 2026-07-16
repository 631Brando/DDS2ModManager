using System.Diagnostics;

namespace DDS2ModManager.Services;

/// Windows won't let a running process delete or overwrite its own exe file, so an update or an
/// uninstall can't just do the file operation directly. This spawns a short-lived, detached
/// PowerShell script that waits for the current process to exit, then does what we couldn't do
/// while still running (replace the exe and relaunch, or delete the whole install directory).
/// Call one of the methods below, then shut the app down - the script is already waiting for
/// that exit.
public static class SelfReplaceHelper
{
    /// Replaces the running exe with newExePath and relaunches it. Call this, then shut the
    /// application down (e.g. Application.Current.Shutdown()).
    public static void ApplyUpdateAndRestart(string newExePath)
    {
        var currentExePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Couldn't determine the current executable's path.");
        var pid = Environment.ProcessId;
        var backupPath = currentExePath + ".old";

        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            try { Wait-Process -Id {{pid}} -Timeout 30 } catch {}
            Start-Sleep -Milliseconds 500
            Remove-Item -LiteralPath '{{Esc(backupPath)}}' -Force -ErrorAction SilentlyContinue
            Rename-Item -LiteralPath '{{Esc(currentExePath)}}' -NewName '{{Esc(Path.GetFileName(backupPath))}}' -Force
            Move-Item -LiteralPath '{{Esc(newExePath)}}' -Destination '{{Esc(currentExePath)}}' -Force
            Remove-Item -LiteralPath '{{Esc(backupPath)}}' -Force -ErrorAction SilentlyContinue
            Start-Process -FilePath '{{Esc(currentExePath)}}'
            Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """;

        RunDetached(script);
    }

    /// Deletes installDir entirely after this process exits. Does not relaunch anything.
    public static void DeleteDirectoryAfterExit(string installDir)
    {
        var pid = Environment.ProcessId;
        var script = $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            try { Wait-Process -Id {{pid}} -Timeout 30 } catch {}
            Start-Sleep -Milliseconds 500
            Remove-Item -LiteralPath '{{Esc(installDir)}}' -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """;

        RunDetached(script);
    }

    private static string Esc(string s) => s.Replace("'", "''");

    private static void RunDetached(string script)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), "DDS2MM_" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}
