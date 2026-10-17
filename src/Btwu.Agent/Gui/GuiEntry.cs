using System.Runtime.InteropServices;
using System.Text;
using Btwu.Core;

namespace Btwu.Agent;

/// <summary>
/// Entry point for the graphical face of btwu.
///
/// Three modes share this one binary, and which one you get is decided by the first argument:
///
///   <c>btwu gui</c>                  the management window (what the shortcut in the package starts);
///   <c>btwu --job &lt;verb&gt; --log F</c>  a headless helper that runs one command with its transcript
///                                    redirected to <c>F</c>, started by the window - elevated for the
///                                    privileged verbs, ordinary otherwise;
///   <c>btwu --enroll-gui --log F</c> the elevated enrollment dialog, which has to be a real window
///                                    because a password cannot be collected from a hidden process.
///
/// The GUI is a *mode*, not a second executable. <c>Btwu.Agent.csproj</c> already sets
/// <c>UseWindowsForms</c> because of the tray, so the whole UI stack is in the binary regardless -
/// adding the window on top costs nothing, while a separate GUI project would mean shipping the .NET
/// runtime twice in a self-contained package and a second copy of every command.
/// </summary>
static class GuiEntry
{
    /// <summary>
    /// The last line a job writes. The window watches for this instead of waiting on the process,
    /// because a <c>runas</c>-launched child does not offer a reliable exit code - and because a
    /// crashed child would otherwise leave the window waiting forever with no explanation.
    /// </summary>
    public const string JobDonePrefix = "===== btwu-job-done ";

    public static int Run(string[] args)
    {
        // Every mode reached from here is a UI or background mode, never a command whose output
        // somebody is reading. Hand the console back rather than merely hiding its window - hiding
        // leaves a window that still has to be kept open, and that kills the process if it is closed.
        // Started from a terminal, this is a no-op and the output stays visible, which is what someone
        // debugging a command line wants.
        ConsoleWindow.DetachForResidentMode();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var jobIndex = Array.IndexOf(args, "--job");
        if (jobIndex >= 0) return RunJob(args, jobIndex);

        if (args.Contains("--enroll-gui")) return RunEnrollGui(args);

        // A window that dies silently is the worst possible outcome for a tool whose entire purpose
        // is making a silently-failing feature visible. Route every unhandled exception to the log and
        // a dismissible dialog instead of the crash dialog nobody can act on.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Report("界面线程", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report("后台线程", e.ExceptionObject as Exception);

        try
        {
            Application.Run(new MainForm());
            return 0;
        }
        catch (Exception ex)
        {
            Report("启动", ex);
            return 1;
        }
    }

    static void Report(string where, Exception? ex)
    {
        Log.Error($"gui: 未处理的异常（{where}）: {ex}");
        try
        {
            MessageBox.Show(
                $"Btwu 管理面板遇到未处理的错误（{where}）。\n\n{ex?.Message}\n\n"
                + $"详细堆栈已写入：\n{Paths.LogFile}",
                "Btwu", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // Nothing left to report with.
        }
    }

    static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    // ------------------------------------------------------------------------ headless job

    /// <summary>
    /// First install, in one elevation. <c>--enroll-first</c> makes this job show the enrollment dialog
    /// here - inside the process that already has the administrator token - instead of the panel
    /// starting a second elevated child for it. Otherwise the first run of the tool would ask for UAC
    /// twice in a row, and the second prompt is exactly the kind of thing that makes a user think the
    /// first one did not work.
    ///
    /// The order matters and is deliberate: the password comes first, the install second, so that the
    /// moment the provider is registered there is already something for it to unlock with.
    /// </summary>
    static int VerbInstall(string? keepPids, IReadOnlyCollection<string> options, string logPath)
    {
        if (options.Contains("--enroll-first") && !CredentialStore.Exists)
        {
            Console.WriteLine("先录入口令 - 免密解锁用的就是它。");
            Console.WriteLine();

            using var form = new EnrollForm(logPath);
            if (form.ShowDialog() != DialogResult.OK)
            {
                Console.WriteLine("没有录入口令，安装没有开始。");
                return 3;
            }

            Console.WriteLine();
        }

        return Scripts.RunInstall(AppContext.BaseDirectory, keepPids, options);
    }

    static int RunJob(string[] args, int jobIndex)
    {
        if (jobIndex + 1 >= args.Length)
        {
            Console.Error.WriteLine("--job 需要一个动词");
            return 2;
        }

        var verb = args[jobIndex + 1].ToLowerInvariant();
        var logPath = Arg(args, "--log") ?? Host.NewLogPath(verb);
        var keepPids = Arg(args, "--keep-pid");

        // Everything that is not framing is handed to the verb unchanged: `doctor`, `verify
        // --compare`, `install-cp --dll X` all keep their own switches. The verb itself is the first
        // element, because what gets dispatched below is an ordinary argv.
        var forwarded = new List<string> { verb };
        for (var i = 0; i < args.Length; i++)
        {
            if (i == jobIndex || i == jobIndex + 1) continue;
            if (args[i] is "--log" or "--keep-pid") { i++; continue; }
            forwarded.Add(args[i]);
        }

        var originalOut = Console.Out;
        var originalError = Console.Error;
        StreamWriter? transcript = null;
        var exit = 1;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            transcript = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };

            // A detached console leaves Console.Out pointing at a closed handle, and TeeTextWriter does
            // not guard its writes (by design - unlock-probe relies on a write failure being loud).
            // So the "also show it on the console" half is dropped when there is no console to show it on.
            var console = ConsoleWindow.Detached ? TextWriter.Null : originalOut;
            var tee = new TeeTextWriter(console, transcript);
            Console.SetOut(tee);
            Console.SetError(tee);
            Log.Echo = true;

            Console.WriteLine($"btwu {verb}");
            Console.WriteLine($"时间:   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"身份:   {(Registration.IsElevated ? "管理员" : "普通用户")}（{Environment.UserName}）");
            Console.WriteLine($"程序目录: {AppContext.BaseDirectory}");
            Console.WriteLine(new string('-', 72));
            Console.WriteLine();

            exit = verb switch
            {
                // Verbs that exist only for the GUI. They are not CLI commands because they would be
                // either redundant or misleading there: `agent-restart` duplicates two schtasks calls
                // the window can make itself, and `delete-credential` is a privileged file delete.
                "install" => VerbInstall(keepPids, forwarded, logPath),
                "agent-restart" => VerbAgentRestart(),
                "delete-credential" => VerbDeleteCredential(),

                // Everything else is a real btwu command, run with the same code path the command
                // line uses - so whatever the window reports can be reproduced by hand.
                _ => Program.MainAsync(forwarded.ToArray()).GetAwaiter().GetResult(),
            };
        }
        catch (Exception ex)
        {
            exit = 1;
            try { Console.Error.WriteLine($"错误: {ex}"); } catch { }
        }
        finally
        {
            try { Console.WriteLine(); Console.WriteLine($"{JobDonePrefix}{exit} ====="); } catch { }
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            transcript?.Dispose();
        }

        return exit;
    }

    static int VerbAgentRestart()
    {
        if (!AgentControl.Exists)
        {
            Console.Error.WriteLine($"计划任务 {AgentControl.TaskName} 不存在——先跑「安装」。");
            return 1;
        }

        var (ok, message) = AgentControl.Restart();
        Console.WriteLine(message);
        return ok ? 0 : 1;
    }

    static int VerbDeleteCredential()
    {
        if (!CredentialStore.Exists)
        {
            Console.WriteLine("本来就没有保管的口令。");
            return 0;
        }

        CredentialStore.Delete();

        if (File.Exists(Paths.CredentialFile))
        {
            Console.Error.WriteLine($"没能删除 {Paths.CredentialFile}。");
            return 1;
        }

        Console.WriteLine($"已删除 {Paths.CredentialFile}");
        Console.WriteLine("锁屏从此不再免密解锁；触发设备与其余配置保留。");
        return 0;
    }

    // ----------------------------------------------------------------------- gui self-test

    /// <summary>
    /// 图形对话框的自检：**不需要管理员**，但需要一个有人登录着的桌面（它会真的把「录入口令」窗口
    /// 弹出来一两秒）。
    ///
    /// 存在的理由是一次真实报障（2026-10-15）：那个窗口的「取消」按钮点下去什么都不发生。
    /// 根因是它当时用 `Application.Run(form)`（**非模态**）显示，而按钮上只写了
    /// `DialogResult = DialogResult.Cancel` —— 非模态表单上这个属性只会记下一个值，**不会关窗**
    /// （实测：Click 事件确实收到了、`Visible` 仍是 true）。同一个窗口在另一条路径
    /// （安装时先录口令）是 `ShowDialog` 显示的，所以那边一直是好的 —— 一个窗口两条打开路径，
    /// 一条模态一条非模态，就这么躲过了所有人工测试：它不报错、不崩溃，只是「点了没反应」。
    ///
    /// 每个尺寸下量五条，其中两条是「必须有」的硬判据：
    ///   1. **设备列表必须真的有高度** —— 2026-10-15 第二次报障：列表塌成一条只剩列头，
    ///      设备全看不见，而当时已有的断言全是绿的（列表恰好在压矮那一态被量对了）。
    ///      教训落在这里：**只在一种窗口尺寸下量过的断言等于没有断言**，所以两种尺寸各量一遍；
    ///   2. 窗口压矮时内容能滚动 —— 否则矮屏上按钮够不着（第一次报障截图里那个「半截按钮」）；
    ///   3. 「取消」完整落在客户区内 —— 固定在最下面，不随内容滚走；
    ///   4. 真点一次「取消」，窗口必须关掉 —— 哑按钮的直接判据；
    ///   5. （不分尺寸）复刻旧写法，要求它**关不掉** —— 「允许失败的检查」的那一半。
    /// </summary>
    public static int SelfTest(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Console.WriteLine("图形对话框自检（每个尺寸会短暂弹出一次「录入口令」窗口）");
        Console.WriteLine();

        var passed = 0;
        var failed = 0;
        void Check(bool ok, string what, string meaning)
        {
            Console.WriteLine(ok ? $"  [ok]   {what}" : $"  [FAIL] {what} —— {meaning}");
            if (ok) passed++; else failed++;
        }

        // --naive：用**旧行为**（非模态 Application.Run）跑同一条断言，要求它红。
        // 同 tools\statetest --naive 的理由：不允许只有「只能通过」的检查 —— 谁也没法从一句
        // 「5 passed」看出这些断言到底测没测东西。
        var naive = args.Contains("--naive");
        if (naive) Console.WriteLine("（--naive：用非模态 Application.Run 显示，那条断言应当失败）");
        Console.WriteLine();

        // 同一个对话框在**两种窗口尺寸**下各量一遍：默认尺寸（用户看到的那一种）与压矮（矮屏）。
        // 2026-10-15 的第二次报障（设备列表塌成一条）只在默认尺寸下出现 —— 当时只测压矮那一态，
        // 它测出来是绿的（列表恰好量对了）。**一个只在一个状态下成立的断言等于没有断言。**
        DialogChecks("默认尺寸", null, Check, naive);
        DialogChecks("压到 300 逻辑像素（矮屏）", 300, Check, naive);

        // 最后一条把**机制**量出来，不依赖上面的窗口：忠实复刻 2026-10-15 之前的写法
        // （非模态 Application.Run + 只设 DialogResult + 没有显式 Close），要求它**关不掉**。
        // 这是「允许失败的检查」里那一半：如果哪天 WinForms 改了语义、非模态下也能关，这条会红 ——
        // 那时该重新评估「显式 Close()」这层保险是否还需要，而不是默默留着一段谁也不知道在防什么的代码。
        Console.WriteLine();
        var legacyClosed = LegacyButtonProbe();
        Check(!legacyClosed,
            "复刻旧写法（非模态 + 只设 DialogResult）→ 点「取消」关不掉窗（这就是那个 bug 的机制）",
            "WinForms 的语义变了：请重新评估「取消」上那句显式 Close() 还有没有必要");

        Console.WriteLine();
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>在一种窗口尺寸下把对话框的版面量一遍，最后真点一次「取消」。</summary>
    static void DialogChecks(string label, int? forcedLogicalHeight, Action<bool, string, string> check, bool naive)
    {
        using var form = new EnrollForm(null);
        form.Shown += (_, _) =>
        {
            if (forcedLogicalHeight is { } h) form.Size = new Size(Ui.Px(840), Ui.Px(h));
            Application.DoEvents();

            // 等枚举真的把设备填进列表再量：用户看到的是「列表里已经有 9 台」的那一刻，
            // 而枚举是异步的。**在空列表上量出来的高度说明不了问题** —— 这正是上一版自检
            // 漏掉那次塌陷的原因之一。
            var deadline = Environment.TickCount64 + 6000;
            while (Environment.TickCount64 < deadline)
            {
                var current = FindControl<ListView>(form);
                if (current is { Items.Count: > 0 }) { Application.DoEvents(); break; }
                Application.DoEvents();
                Thread.Sleep(50);
            }
            Application.DoEvents();

            var root = form.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            var scroll = root?.GetControlFromPosition(0, 0) as Panel;
            var content = scroll?.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            var list = FindControl<ListView>(form);
            var box = FindControl<GroupBox>(form, g => g.Text == "触发设备");
            var minList = Ui.Px(120);

            // 把整条尺寸链打出来（不只报一个通过/失败）：万一还有哪个尺寸下塌掉，这行数字就是证据，
            // 用户把控制台那行贴回来就能定位是哪一环 —— 比一张界面截图有用得多。
            Console.WriteLine($"  [info] [{label}] 客户区={form.ClientSize} 内容首选={content?.PreferredSize.Height}"
                              + $" 滚动区={scroll?.ClientSize.Height} 设备框={box?.Size} 列表={list?.Bounds}"
                              + $" 台数={list?.Items.Count}");

            // 设备列表必须真的看得见。2026-10-15 的报障（列表只剩列头）**两条判据缺一不可**：
            //   ① 列表自己得够高；
            //   ② **装它的那个框得比列表高** —— 塌掉的从来不是列表自己（实测它一直是 320），
            //      而是那个手搭的 GroupBox（实测塌到 100），列表被框裁到只剩列头。
            // A/B 实测：只写 ① 时旧搭法照样全绿 —— 这就是「断言量错了对象」。
            check(list is not null && list.Height >= minList,
                $"[{label}] 设备列表高度 {list?.Height} ≥ {minList}（列表里 {list?.Items.Count} 台）",
                "列表塌成一条：设备看不见");

            check(list is not null && box is not null && box.Height >= list.Height + Ui.Px(20),
                $"[{label}] 设备框装得下列表（框 {box?.Height} ≥ 列表 {list?.Height} + 间距）",
                "框塌了、把列表裁到只剩列头 —— 设备全看不见（旧搭法实测：框 100 / 列表 320）");

            if (forcedLogicalHeight is not null)
            {
                check(scroll?.VerticalScroll.Visible == true,
                    $"[{label}] 出现滚动条（内容首选 {content?.PreferredSize.Height} > 滚动区 {scroll?.ClientSize.Height}）",
                    "矮屏上内容够不着，用户只能看到半截按钮");
            }

            var cancel = FindButton(form, "取消");
            var clientRect = form.RectangleToScreen(form.ClientRectangle);
            var cancelRect = cancel?.RectangleToScreen(cancel.ClientRectangle) ?? Rectangle.Empty;
            check(cancel is not null && clientRect.Contains(cancelRect),
                $"[{label}] 「取消」完整落在客户区内 {cancelRect}",
                "按钮被挤出客户区，只有一小条能接收鼠标");

            if (cancel is null) { form.Close(); return; }
            var center = new Point(cancel.Width / 2, cancel.Height / 2);
            PostMessage(cancel.Handle, 0x0201, (IntPtr)1, MakeLParam(center));
            PostMessage(cancel.Handle, 0x0202, IntPtr.Zero, MakeLParam(center));
        };

        var result = naive ? RunNonModally(form) : form.ShowDialog();
        check(result == DialogResult.Cancel,
            $"[{label}] 真实鼠标点击「取消」→ 窗口关闭（返回 {result}）",
            "按钮是哑的：非模态窗口上 DialogResult 不会关窗（见 docs/技术说明.md 第 7 节）");
    }

    static DialogResult RunNonModally(Form form)
    {
        Application.Run(form);
        return form.DialogResult;
    }

    /// <summary>
    /// 复刻 2026-10-15 之前的写法并真点一次：非模态 `Application.Run` + 按钮只设 `DialogResult`、
    /// **没有**显式 `Close()`。返回「窗口是被这次点击关掉的吗」。
    ///
    /// 必须有看门狗：非模态下点击不会关窗，`Application.Run` 就永远不返回 —— 没有定时器兜底的话
    /// 这个自检会挂死在那个窗口上，而不是报一条失败。
    /// </summary>
    static bool LegacyButtonProbe()
    {
        var watchdogFired = false;
        var closedByClick = false;

        var probe = new Form
        {
            AutoScaleMode = AutoScaleMode.None,
            Size = new Size(Ui.Px(360), Ui.Px(160)),
            Text = "自检：旧写法（非模态 + 只设 DialogResult）",
            StartPosition = FormStartPosition.CenterScreen,
        };
        var cancel = new Button
        {
            Text = "取消",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
            Location = new Point(Ui.Px(20), Ui.Px(20)),
        };
        probe.Controls.Add(cancel);

        var watchdog = new System.Windows.Forms.Timer { Interval = 1500 };
        watchdog.Tick += (_, _) => { watchdogFired = true; probe.Close(); };

        probe.FormClosing += (_, _) => closedByClick = !watchdogFired;
        probe.Shown += (_, _) =>
        {
            watchdog.Start();
            var center = new Point(cancel.Width / 2, cancel.Height / 2);
            PostMessage(cancel.Handle, 0x0201, (IntPtr)1, MakeLParam(center));
            PostMessage(cancel.Handle, 0x0202, IntPtr.Zero, MakeLParam(center));
        };

        Application.Run(probe);
        watchdog.Dispose();
        return closedByClick;
    }

    static Button? FindButton(Control parent, string text) => FindControl<Button>(parent, b => b.Text == text);

    static T? FindControl<T>(Control parent, Func<T, bool>? predicate = null) where T : Control
    {
        foreach (Control child in parent.Controls)
        {
            if (child is T match && (predicate is null || predicate(match))) return match;
            var found = FindControl(child, predicate);
            if (found is not null) return found;
        }

        return null;
    }

    [DllImport("user32.dll")]
    static extern IntPtr PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    static IntPtr MakeLParam(Point p) => (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));

    // ----------------------------------------------------------------------- enroll dialog

    static int RunEnrollGui(string[] args)
    {
        // Enrollment writes cred.bin, whose ACL is SYSTEM + Administrators only, so an unelevated
        // attempt would fail at the last step after the user had already typed their password. Say so
        // up front rather than at the end.
        if (!Registration.IsElevated)
        {
            MessageBox.Show(
                "录入账户口令需要管理员权限。\n\n请从管理面板里点「录入 / 更新口令」，"
                + "它会以管理员身份重新打开这个对话框。",
                "Btwu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }

        using var form = new EnrollForm(Arg(args, "--log"));

        // ShowDialog, NOT Application.Run —— 这里曾经写的是 Application.Run(form)，于是「取消」按钮点不动
        // （2026-10-15 用户报上来）。原因很硬：**非模态**显示的表单，按钮上那句
        // `DialogResult = DialogResult.Cancel` 只会把一个值记下来，**不会关窗**——
        // 实测（dist/gui-test/dlgtest，真实鼠标点击 + PerformClick 两条路都试过）：
        // Click 事件确实收到了，表单 `Visible` 仍是 true、`DialogResult` 变成 Cancel 而已。
        // 同一个对话框在 `--enroll-first`（安装时先录口令，VerbInstall）那条路上是 ShowDialog 显示的，
        // 所以那边一直是好的 —— **同一个窗口、两条打开路径、一条模态一条非模态**，这就是它躲过所有
        // 人工测试的原因（它也只是「点了没反应」，不报错、不崩溃）。
        // 连带修掉的是 Esc：CancelButton 也是走 PerformClick → 同一条死路。
        form.ShowDialog();
        return form.Saved ? 0 : 1;
    }
}
