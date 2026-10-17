using System.Diagnostics;
using Btwu.Core;

namespace Btwu.Agent;

/// <summary>
/// Starts, stops and restarts the logon scheduled task that runs the agent.
///
/// The agent reads <c>config.json</c> once at startup, so every setting the GUI writes is inert until
/// the agent is restarted. Saying that in a tooltip is not enough - people change the grace period,
/// watch nothing happen, and conclude the setting does not work. Hence a button that actually does it.
///
/// <c>schtasks.exe</c> rather than the ScheduledTasks cmdlets: this has to run from the GUI's own
/// process, and the cmdlets would need a PowerShell child and a module import for two commands.
///
/// Only exit codes are used, never the tool's text. <c>InvariantGlobalization</c> is on for this
/// project, so decoding the OEM code page that <c>schtasks</c> writes in would throw - and its message
/// is a translated string anyway, which is exactly the kind of thing that must not end up in a branch.
/// </summary>
static class AgentControl
{
    public const string TaskName = "BtwuPresenceUnlock";

    public static bool Exists => Run("query") == 0;

    public static (bool ok, string message) Start() =>
        Run("run") == 0
            ? (true, "已启动后台 agent（计划任务已运行）")
            : (false, $"没能启动计划任务 {TaskName}。它可能还没安装——先跑一次「安装」。");

    public static (bool ok, string message) Stop() =>
        Run("end") == 0
            ? (true, "已结束后台 agent")
            : (false, $"没能结束计划任务 {TaskName}（可能本来就没在跑）。");

    /// <summary>
    /// End then start. The task is registered with <c>MultipleInstances IgnoreNew</c>, so a bare
    /// "run" against a task that is already running is silently ignored - the old process with the old
    /// configuration would keep going and the restart would look like it did nothing.
    /// </summary>
    public static (bool ok, string message) Restart()
    {
        if (!Exists)
            return (false, $"计划任务 {TaskName} 不存在。用「安装」装上它，"
                + "或者手动在管理员 PowerShell 里启动 btwu tray。");

        var stopped = Run("end") == 0;
        Thread.Sleep(600);
        var started = Run("run") == 0;

        return started
            ? (true, stopped ? "已重启后台 agent" : "已启动后台 agent（原先没在跑）")
            : (false, $"结束成功但重新启动失败。手动试： schtasks /Run /TN {TaskName}");
    }

    static int Run(string verb)
    {
        try
        {
            var info = new ProcessStartInfo("schtasks.exe")
            {
                Arguments = $"/{verb} /TN {Host.Quote(TaskName)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            using var process = Process.Start(info);
            if (process is null) return -1;

            // Drain both pipes before waiting: a full pipe buffer deadlocks a child that is not read.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Log.Warn($"schtasks /{verb} failed: {ex.Message}");
            return -1;
        }
    }
}
