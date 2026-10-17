using System.Diagnostics;
using Btwu.Core;

namespace Btwu.Agent;

/// <summary>Thin shell helpers. Every one of them is best-effort: opening a folder is never a reason
/// to fail the operation the user actually asked for.</summary>
static class Shell
{
    /// <summary>Opens a file or folder with its default handler. Missing paths open their parent.</summary>
    public static void Open(string path)
    {
        try
        {
            var target = path;
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                var parent = Path.GetDirectoryName(target);
                if (parent is not null && Directory.Exists(parent)) target = parent;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot open {path}: {ex.Message}");
        }
    }

    /// <summary>Opens the data directory, creating it first so the click is never a no-op.</summary>
    public static void OpenDataDirectory()
    {
        try { Btwu.Core.Paths.EnsureDataDir(); } catch { }
        Open(Btwu.Core.Paths.DataDir);
    }

    /// <summary>Shows a modal error with a plain-language title.</summary>
    public static void Error(IWin32Window? owner, string title, string message)
    {
        if (owner is null) MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        else MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    public static void Info(IWin32Window? owner, string title, string message)
    {
        if (owner is null) MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        else MessageBox.Show(owner, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public static bool Confirm(IWin32Window? owner, string title, string message)
    {
        var result = owner is null
            ? MessageBox.Show(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
            : MessageBox.Show(owner, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        return result == DialogResult.Yes;
    }
}
