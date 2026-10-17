using System.Runtime.InteropServices;
using Btwu.Core;

namespace Btwu.Agent;

/// <summary>
/// Takes the resident modes off the console entirely.
///
/// <c>btwu.exe</c> is a console-subsystem binary on purpose: the whole CLI depends on being pipeable,
/// and <c>btwu enroll</c> needs a real console to read a password a character at a time. But `tray`,
/// `run` and `gui` are not command-line programs — they are background/resident processes — and for
/// them the console is pure liability:
///
///   * it puts a window on the desktop that has to be kept open or minimised;
///   * **closing that window kills the process**, because Windows sends CTRL_CLOSE_EVENT and then
///     terminates a console app. That is a trap with no warning: the user tidies up the taskbar and
///     the unlock silently stops working.
///
/// Hiding the window (what `tray` used to do on its own) fixes the first problem and leaves the second:
/// a hidden window can still be closed from Task Manager's "End task", and it still shows up as an
/// entry that looks like something to clean up. <see cref="FreeConsole"/> removes the console itself,
/// so there is no window, nothing to close, and no CTRL_CLOSE_EVENT to receive.
///
/// <b>Only when this process owns the console.</b> Started from a terminal, the process shares the
/// shell's console — detaching then would hide the output the person deliberately asked for, and it
/// would also take the console away from the shell for every subsequent command. <c>GetConsoleProcessList</c>
/// answers "how many processes are attached"; this process is the only one exactly when it created the
/// console itself (double-click, shortcut, scheduled task), which is precisely the case to detach in.
/// </summary>
static class ConsoleWindow
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetConsoleProcessList([Out] uint[] processList, uint count);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int command);

    const int SwHide = 0;

    /// <summary>
    /// True once the console has actually been released. Callers need this to know that
    /// <c>Console.Out</c> is now a broken writer and must not be handed to a <c>TeeTextWriter</c>.
    /// </summary>
    public static bool Detached { get; private set; }

    /// <summary>True when this process is the only one attached to its console, i.e. it created it.</summary>
    public static bool OwnsConsole
    {
        get
        {
            try
            {
                var list = new uint[4];
                var count = GetConsoleProcessList(list, (uint)list.Length);

                // 0 means "no console at all" (already launched with CREATE_NO_WINDOW or DETACHED_PROCESS).
                // Anything above 1 means a shell shares it, so it is not ours to take away.
                return count == 1;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Hides and then releases this process's console.
    /// </summary>
    /// <param name="force">
    /// Detach even when the console is shared with a shell. Only <c>tray</c> asks for this, and the
    /// reason is worth stating: it never writes to the console at all (<c>Log.Echo</c> is already off
    /// for it), so the console is pure liability. Left attached, `btwu tray` typed into a terminal
    /// keeps the process tied to that window — and the observed behaviour is exactly the complaint
    /// that started this: the window "has to be kept open or minimised" for the unlock to work, and
    /// closing it kills the agent. Detaching costs nothing and makes the window disposable.
    ///
    /// A mode whose output somebody may be reading (<c>run</c>, and <c>gui</c> for its errors) leaves
    /// this false, so a terminal launch keeps its output while a shortcut or scheduled-task launch —
    /// the case where the process created the console itself — still detaches.
    /// </param>
    /// <returns>
    /// <c>true</c> when the console was detached, which means <b>there is no console any more</b>:
    /// the caller must stop echoing log lines to it (see <see cref="Log.Echo"/>) — writes to a
    /// detached console fail, and <see cref="Log"/>'s contract is that logging never breaks the caller.
    /// </returns>
    public static bool DetachIfOwned(bool force = false)
    {
        try
        {
            if (!force && !OwnsConsole) return false;

            // Hide first purely to avoid a visible flicker between "console shown" and "console gone".
            var window = GetConsoleWindow();
            if (window != IntPtr.Zero) ShowWindow(window, SwHide);

            FreeConsole();
            Detached = true;

            // Worth a line in the log: "the agent is tied to a console window" is otherwise invisible
            // from the outside, and it is the difference between a window the user must keep open and
            // one they can close whenever they like.
            Log.Info($"console released (forced={force})");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The whole sequence a resident mode wants: leave the console, and stop writing to it.
    ///
    /// Doing these together is deliberate. Detaching without turning the echo off leaves a logging path
    /// that throws on every line — which, in a heartbeat loop, is the difference between "runs for
    /// weeks" and "dies on the first log write after startup".
    /// </summary>
    public static void DetachForResidentMode(bool force = false)
    {
        if (DetachIfOwned(force))
        {
            Log.Echo = false;
        }
    }
}
