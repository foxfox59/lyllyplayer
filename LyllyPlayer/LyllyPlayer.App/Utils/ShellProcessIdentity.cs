using System.Runtime.InteropServices;

namespace LyllyPlayer.Utils;

/// <summary>
/// Ensures the process has a stable foreground identity for the Windows shell (taskbar, Alt+Tab, Task Manager "Apps").
/// </summary>
internal static class ShellProcessIdentity
{
    /// <summary>Stable ID (no spaces). Call once during startup before showing UI.</summary>
    private const string ExplicitAppUserModelId = "LyllyPlayer.LyllyPlayer.Foreground.1";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    public static void TrySetExplicitAppUserModelId()
    {
        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(ExplicitAppUserModelId);
        }
        catch
        {
            // ignore
        }
    }
}
