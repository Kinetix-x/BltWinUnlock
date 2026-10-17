using System.Diagnostics;
using System.Globalization;
using System.Text;
using Btwu.Core;

namespace Btwu.Agent;

/// <summary>
/// The privileged half of the GUI: it drives the same two PowerShell scripts the command line uses,
/// instead of reimplementing them.
///
/// That is a deliberate choice. The scripts already do four things the GUI would otherwise have to
/// duplicate - the data-directory ACL, the cred.bin DACL, the logon scheduled task with its exact
/// settings, and the robocopy mirror - and a second implementation of a security-relevant ACL is
/// exactly the kind of thing that drifts. One installer, two front ends: <c>scripts\install.ps1</c>
/// stays the single source of truth, and whatever the GUI does can be reproduced by hand with one
/// command line copied out of the transcript.
///
/// The cost is that the GUI needs PowerShell present - which is a given on any Windows 10/11 machine
/// this tool targets. The <c>btwu</c> subcommands that are pure .NET (install-cp, uninstall-cp,
/// doctor, verify) are called directly and do not go through PowerShell at all.
/// </summary>
static class Scripts
{
    /// <summary>
    /// Locates a script bundled next to the executable.
    ///
    /// Four layouts have to work, which is why this walks upward instead of checking one path:
    ///   * <c>dist\app\scripts\</c>      - a repo build (the GUI is usually run from here while developing);
    ///   * <c>&lt;portable&gt;\app\scripts\</c> - the green package;
    ///   * <c>%ProgramFiles%\Btwu\scripts\</c> - the installed copy, so "repair" and "roll back" work
    ///     from the installed copy as well as from the folder it came from;
    ///   * <c>src\Btwu.Agent\bin\Release\&lt;tfm&gt;\</c> - a raw <c>dotnet build</c> output.
    /// </summary>
    public static string? FindScript(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ------------------------------------------------------------------------- install

    /// <summary>
    /// Runs <c>install.ps1</c> from the folder this executable lives in.
    ///
    /// <paramref name="keepPids"/> is not optional in spirit: the installer's first act is to stop
    /// every running <c>btwu</c> process, and every process here *is* btwu - the job itself and the
    /// window that started it. Without the exclusion list the installer kills the window mid-install
    /// and the transcript ends with no explanation. The child adds its own pid; the window passes its
    /// own through <c>--keep-pid</c>.
    /// </summary>
    public static int RunInstall(string appDirectory, string? keepPids, IReadOnlyCollection<string> options)
    {
        var script = FindScript("install.ps1");
        if (script is null)
        {
            Console.Error.WriteLine("找不到 scripts\\install.ps1。");
            Console.Error.WriteLine("安装/回滚必须从绿色版文件夹里运行——那里面带着这个脚本。");
            Console.Error.WriteLine($"当前程序目录: {AppContext.BaseDirectory}");
            return 2;
        }

        var dll = Path.Combine(appDirectory, Registration.DllFileName);
        if (!File.Exists(dll))
        {
            Console.Error.WriteLine($"找不到 {Registration.DllFileName}（应该在 {appDirectory} 里）。");
            Console.Error.WriteLine("绿色版文件夹被拆散了；请重新解压完整压缩包。");
            return 2;
        }

        var arguments = new List<string>
        {
            "-StagedApp", appDirectory,
            "-StagedDll", dll,
            // The window collects the password itself, in its own elevated dialog. install.ps1's
            // built-in enroll reads the console a character at a time, which cannot work under a
            // hidden window with no console - and would paste a password into a transcript if it did.
            "-SkipEnroll",
            "-KeepPid", Merge(keepPids),
        };

        if (options.Contains("--headless")) arguments.Add("-Headless");
        if (options.Contains("--no-autostart")) arguments.Add("-NoAutostart");

        return RunPowerShell(script, arguments);
    }

    /// <summary>
    /// The caller's <c>--keep-pid</c> list plus this process, because this process is a btwu too:
    /// every "stop the running agent" step matches on the process name, and this one is the child
    /// doing the work. Both sides have to be excluded or the job kills itself mid-run.
    /// </summary>
    static string Merge(string? keepPids)
    {
        var mine = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(keepPids) ? mine : $"{keepPids},{mine}";
    }

    /// <summary>
    /// Starts Windows PowerShell with stdout and stderr pumped into our own <see cref="Console"/>,
    /// which in a job process is the tee that writes the transcript.
    ///
    /// The child is told to emit UTF-8 only when its output is redirected (see the prelude in both
    /// scripts), so a person running the same script by hand still gets their console's own code page
    /// and readable Chinese, while a redirected run produces bytes this side can decode.
    /// </summary>
    static int RunPowerShell(string script, List<string> arguments)
    {
        var info = new ProcessStartInfo(Host.PowerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = AppContext.BaseDirectory,
        };

        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(script);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        Console.WriteLine($"> powershell -File \"{script}\" {string.Join(' ', arguments)}");
        Console.WriteLine();

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                Console.Error.WriteLine("无法启动 PowerShell。");
                return 2;
            }

            process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            Console.WriteLine();
            Console.WriteLine($"脚本退出码: {process.ExitCode}");
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"运行 PowerShell 失败: {ex.Message}");
            return 2;
        }
    }
}
