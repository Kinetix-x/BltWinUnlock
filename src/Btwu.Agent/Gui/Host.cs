using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Btwu.Agent;

/// <summary>
/// How the GUI launches work: itself again, either as an ordinary hidden helper or elevated.
///
/// Every job - a self-check, a password verification, the installer - runs in a child process rather
/// than in the window's own process. Two reasons, both load-bearing:
///
///   * <b>the elevated part has to be a separate process.</b> A process cannot raise its own token;
///     the only way to touch System32, HKLM or a SYSTEM-only file is to be started elevated, and the
///     only way to start elevated is <c>ShellExecute</c> with the <c>runas</c> verb.
///   * <b>a crash or a hang must not take the window with it.</b> <c>doctor</c> probes the PnP tree and
///     <c>verify</c> calls into LSA; neither belongs on the thread that owns the UI.
///
/// <c>ShellExecute</c> and pipes are mutually exclusive, which is why each child writes a transcript
/// file and the window tails it instead of reading stdout. See <see cref="TeeTextWriter"/>.
/// </summary>
static class Host
{
    /// <summary>This very executable. The GUI, the CLI and the tray are one binary.</summary>
    public static string Exe => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>Where job transcripts land. Temp, not ProgramData: they are disposable by definition.</summary>
    public static string LogDir { get; } = EnsureDirectory(
        Path.Combine(Path.GetTempPath(), "BtwuGui"));

    static string EnsureDirectory(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* the caller reports the real failure */ }
        return path;
    }

    public static string NewLogPath(string name) =>
        Path.Combine(LogDir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");

    /// <summary>
    /// Windows command-line quoting, per the rules the C runtime uses to split argv.
    ///
    /// Needed because this launches processes with <c>UseShellExecute = true</c>, where
    /// <see cref="ProcessStartInfo.ArgumentList"/> is not available and the arguments must be
    /// pre-joined into a single string. Getting it wrong is not cosmetic: the transcript path lives
    /// under <c>%TEMP%</c>, which for an account like "Zhang San" contains a space.
    /// </summary>
    public static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;

        var builder = new StringBuilder("\"");
        for (var i = 0; i < value.Length; i++)
        {
            var backslashes = 0;
            while (i < value.Length && value[i] == '\\') { backslashes++; i++; }

            if (i == value.Length)
            {
                // Trailing backslashes would escape the closing quote.
                builder.Append('\\', backslashes * 2);
                break;
            }

            if (value[i] == '"')
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            else
                builder.Append('\\', backslashes).Append(value[i]);
        }

        return builder.Append('"').ToString();
    }

    /// <summary>
    /// Starts this executable again. <paramref name="elevated"/> decides between the two launch
    /// strategies; they cannot be mixed, because elevation requires the shell and a hidden console
    /// requires <c>CreateProcess</c>.
    /// </summary>
    /// <returns>The child, or <c>null</c> with <paramref name="error"/> set.</returns>
    public static Process? Start(string arguments, bool elevated, out string? error)
    {
        error = null;

        var info = new ProcessStartInfo(Exe)
        {
            Arguments = arguments,
            // Relative paths inside the scripts resolve against the install/publish directory, not
            // against wherever the shortcut happened to be.
            WorkingDirectory = AppContext.BaseDirectory,
        };

        if (elevated)
        {
            // ShellExecute only. WindowStyle = Hidden is the only way to keep the console subsystem
            // from flashing a black window for the duration of the elevated job.
            info.UseShellExecute = true;
            info.Verb = "runas";
            info.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
        }

        try
        {
            return Process.Start(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED - the user clicked "No" on the UAC prompt. Not an error worth a stack.
            error = "已取消管理员授权（UAC）。这一步需要管理员权限。";
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Full path of Windows PowerShell 5.1. <c>install.ps1</c> declares <c>#requires 5.1</c>.</summary>
    public static string PowerShell { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>True when the running process is the installed copy rather than a portable folder.</summary>
    public static bool RunningFromInstallDirectory
    {
        get
        {
            try
            {
                var installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Btwu");
                return Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\')
                    .Equals(Path.GetFullPath(installDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Btwu");
}
