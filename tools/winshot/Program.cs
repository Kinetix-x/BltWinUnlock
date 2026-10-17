using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Btwu.WinShot;

/// <summary>
/// Grabs a picture of ONE window, by title.
///
/// Why this exists: the management panel's layout cannot be reasoned about from the source. Fixed
/// pixel heights and Dock stacking look right on paper and clip in reality, and "the buttons are cut
/// off" is not something a build log can tell you. Add-Type and UI Automation are both blocked in this
/// environment, so the only way to actually look at the window is a separate program — which is the
/// same escape hatch <c>tools/statetest</c> uses for the same reason.
///
/// It uses PrintWindow with PW_RENDERFULLCONTENT rather than a screen capture on purpose:
///
///   * it renders the window's OWN content into an off-screen bitmap, so whatever else happens to be
///     on the desktop cannot end up in the image (a screen grab of the window rectangle would capture
///     any window stacked on top of it);
///   * it works even when the window is occluded or partially off-screen.
///
/// Usage:
///   winshot &lt;title-substring&gt; &lt;output.png&gt; [--pid N] [--redraw]
///
/// --redraw asks the window to repaint first (WM_PAINT / RedrawWindow) when the captured image looks
/// stale. PrintWindow normally forces a redraw itself; the flag is here because a hidden or minimized
/// window sometimes returns its last cached frame.
/// </summary>
static class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, which is the value -4 as a pseudo-handle.</summary>
    static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")]
    static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    const uint PwRenderFullContent = 0x00000002;
    const uint RdwInvalidate = 0x0001;
    const uint RdwAllChildren = 0x0080;
    const uint RdwUpdateNow = 0x0100;

    static int Main(string[] args)
    {
        // MUST be the first thing. A DPI-unaware process is handed *virtualised* coordinates by
        // Windows, so GetWindowRect on a 200%-scaled display reports half the real size and
        // PrintWindow renders into a bitmap that is half resolution — which would hide exactly the
        // one-or-two-pixel clipping this tool exists to reveal. (Measured: it reported 480x380 for a
        // 960x760 window before this call was added.)
        try { SetProcessDpiAwarenessContext(PerMonitorAwareV2); } catch { }

        Console.OutputEncoding = Encoding.UTF8;

        // --windows exists for one question that a screenshot cannot answer: does a process still own a
        // console window? A console window belongs to conhost.exe, not to the program, so it never
        // shows up in Process.MainWindowHandle - and text like "the window must be kept open for this
        // to work" is otherwise unverifiable.
        if (args.Length > 0 && args[0] == "--windows")
        {
            ListWindows();
            return 0;
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: winshot <title-substring> <output.png> [--pid N] [--redraw] [--foreground]");
            Console.Error.WriteLine("       winshot --windows");
            return 2;
        }

        var title = args[0];
        var output = Path.GetFullPath(args[1]);
        var redraw = args.Contains("--redraw");
        var foreground = args.Contains("--foreground");

        var explicitPid = 0;
        var pidIndex = Array.IndexOf(args, "--pid");
        if (pidIndex >= 0 && pidIndex + 1 < args.Length) int.TryParse(args[pidIndex + 1], out explicitPid);

        var (window, pid, caption, processName) = FindWindow(title, explicitPid);
        if (window == IntPtr.Zero)
        {
            Console.Error.WriteLine($"找不到标题包含「{title}」的可见窗口。");
            ListCandidates();
            return 1;
        }

        if (!GetWindowRect(window, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            Console.Error.WriteLine("GetWindowRect 失败或窗口尺寸为空。");
            return 1;
        }

        if (foreground)
        {
            SetForegroundWindow(window);
            Thread.Sleep(400);
        }

        if (redraw)
        {
            RedrawWindow(window, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwAllChildren | RdwUpdateNow);
            Thread.Sleep(300);
        }

        var dpi = GetDpiForWindow(window);
        Console.WriteLine($"窗口:      {processName}.exe (pid {pid})");
        Console.WriteLine($"标题:      {caption}");
        Console.WriteLine($"位置尺寸:  {rect.Width} x {rect.Height}  @ ({rect.Left},{rect.Top})");
        Console.WriteLine($"DPI:       {dpi}  (缩放 {dpi * 100.0 / 96.0:F0}%)");
        Console.WriteLine($"可见:      {IsWindowVisible(window)}   最小化: {IsIconic(window)}");

        // The bitmap is sized in PHYSICAL pixels, because that is what PrintWindow fills. Using the
        // window rect as-is avoids introducing a scaling step that would silently resample the image
        // and hide exactly the kind of one-or-two-pixel clipping this tool is meant to reveal.
        using var bitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(window, hdc, PwRenderFullContent))
                {
                    Console.Error.WriteLine($"PrintWindow 失败 (Win32 错误 {Marshal.GetLastWin32Error()})。");
                    return 1;
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        bitmap.Save(output, ImageFormat.Png);
        Console.WriteLine($"已写出:    {output}  ({new FileInfo(output).Length / 1024} KB)");
        return 0;
    }

    /// <summary>Every top-level window, with its class and owning pid. Console windows included.</summary>
    static void ListWindows()
    {
        var rows = new List<string>();
        EnumWindows((window, _) =>
        {
            var className = new System.Text.StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            var text = new System.Text.StringBuilder(256);
            GetWindowText(window, text, text.Capacity);
            GetWindowThreadProcessId(window, out var pid);

            var processName = "?";
            try { processName = Process.GetProcessById((int)pid).ProcessName; } catch { }

            rows.Add($"0x{window.ToInt64():X8}  {className,-28} pid {pid,-6} {processName,-20} "
                     + $"visible={IsWindowVisible(window)} {(text.Length > 0 ? text.ToString() : "")}");
            return true;
        }, IntPtr.Zero);

        foreach (var row in rows) Console.WriteLine(row);
        Console.WriteLine($"共 {rows.Count} 个顶层窗口，其中 ConsoleWindowClass "
                          + $"{rows.Count(r => r.Contains("ConsoleWindowClass"))} 个");
    }

    /// <summary>
    /// Picks the best window for a search string by enumerating top-level windows directly.
    ///
    /// NOT via <c>Process.MainWindowTitle</c>: that reports the process's *main* window, so a modal
    /// dialog - which is what a job transcript is shown in - is invisible to it. Measured: asking for
    /// the transcript dialog's title returned "not found" while the dialog was plainly on screen.
    /// <c>EnumWindows</c> sees every top-level window, dialogs included, and yields the owning process
    /// id straight from the window.
    ///
    /// The scoring keeps the caller from having to know pids: an exact process name beats an exact
    /// caption, which beats a substring, and a restored window beats a minimised one.
    /// </summary>
    static (IntPtr Window, uint Pid, string Caption, string ProcessName) FindWindow(string title, int explicitPid)
    {
        IntPtr best = IntPtr.Zero;
        uint bestPid = 0;
        var bestCaption = "";
        var bestProcess = "";
        var bestScore = int.MinValue;

        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;

            GetWindowThreadProcessId(window, out var pid);
            if (explicitPid != 0 && pid != (uint)explicitPid) return true;

            var caption = ReadText(window, GetWindowText);
            if (explicitPid == 0 && caption.Length == 0) return true;

            string processName;
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { processName = "?"; }

            int score;
            if (explicitPid != 0) score = 3;
            else if (processName.Equals(title, StringComparison.OrdinalIgnoreCase)) score = 4;
            else if (caption.Equals(title, StringComparison.OrdinalIgnoreCase)) score = 3;
            else if (caption.Contains(title, StringComparison.OrdinalIgnoreCase)) score = 2;
            else return true;

            if (!IsIconic(window)) score++;
            if (score <= bestScore) return true;

            best = window;
            bestPid = pid;
            bestCaption = caption;
            bestProcess = processName;
            bestScore = score;
            return true;
        }, IntPtr.Zero);

        return (best, bestPid, bestCaption, bestProcess);
    }

    static string ReadText(IntPtr window, Func<IntPtr, System.Text.StringBuilder, int, int> read)
    {
        var buffer = new System.Text.StringBuilder(512);
        read(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>Printing what IS on screen beats printing "not found" and leaving it at that.</summary>
    static void ListCandidates()
    {
        Console.Error.WriteLine("当前有可见顶层窗口：");
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;

            var caption = ReadText(window, GetWindowText);
            if (caption.Length == 0) return true;

            GetWindowThreadProcessId(window, out var pid);
            string processName;
            try { processName = Process.GetProcessById((int)pid).ProcessName; }
            catch { processName = "?"; }

            var className = new System.Text.StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            Console.Error.WriteLine($"  pid {pid,-8} {processName,-22} {className,-34} {caption}");
            return true;
        }, IntPtr.Zero);
    }
}
