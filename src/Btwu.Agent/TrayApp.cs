using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Btwu.Core;

namespace Btwu.Agent;

/// <summary>
/// Notification-area shell for the agent: same engine as <c>btwu run</c>, plus a visible state and
/// the actions worth having at hand (lock now, pause, open the data directory).
///
/// This is what the logon scheduled task starts, so the user can always see whether the unlock is
/// actually armed instead of guessing.
/// </summary>
static class TrayApp
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Runs the message loop, and the engine inside it. Deliberately synchronous: see the comment
    /// on the startup ordering below.
    ///
    /// The console is dealt with by <see cref="Program.Tray"/> before this is called — the process
    /// releases it rather than hiding the window, so there is nothing on the desktop to keep open and
    /// nothing that can be closed to kill the agent.
    /// </summary>
    public static int Run(AppConfig cfg)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // The engine must NOT be started before the message loop exists.
        //
        // Creating a Control - which TrayContext does for cross-thread marshalling - installs a
        // WindowsFormsSynchronizationContext on this thread. Every await in the agent then captures
        // that context, so its continuations are queued as window messages. With no pump running yet
        // the first WinRT await (BluetoothLEDevice.FromIdAsync) never resumes: the process stays
        // alive, never arms, and never writes presence.dat. Starting from Application.Idle is the
        // first moment the pump is actually turning.
        var context = new TrayContext(cfg);

        Application.Run(context);

        context.Shutdown();
        return 0;
    }

    sealed class TrayContext : ApplicationContext
    {
        readonly AppConfig _cfg;
        readonly PresenceAgent _agent;
        readonly Control _marshal = new();
        readonly NotifyIcon _icon = new();
        readonly ToolStripMenuItem _statusItem;
        readonly ToolStripMenuItem _toggleItem;
        readonly System.Windows.Forms.Timer _recheck = new() { Interval = 30_000 };

        Icon? _currentIcon;
        Color? _lastColour;
        string _lastTooltip = "";
        string _lastStatus = "";
        bool _started;

        public TrayContext(AppConfig cfg)
        {
            _cfg = cfg;
            _agent = new PresenceAgent(cfg);

            // Forces the handle to exist so background threads can marshal through it.
            _ = _marshal.Handle;

            _statusItem = new ToolStripMenuItem("正在启动…") { Enabled = false };
            _toggleItem = new ToolStripMenuItem("暂停");
            var openItem = new ToolStripMenuItem("打开管理面板…");
            var exitItem = new ToolStripMenuItem("退出");

            openItem.Click += (_, _) => OpenPanel();
            _toggleItem.Click += (_, _) => SetPaused(!File.Exists(Paths.DisableCpFile));
            exitItem.Click += (_, _) =>
            {
                _icon.Visible = false;
                ExitThread();
            };

            // Four things, and that is the whole menu. Everything else - editing config.json, running
            // doctor or verify, opening the logs, the lock now action - lives in the panel, which is
            // one click away at the top. A tray menu that mirrors a settings dialog item by item ends
            // up being a worse settings dialog.
            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(openItem);
            menu.Items.Add(_toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _icon.Visible = true;
            _icon.ContextMenuStrip = menu;
            _icon.Text = "Btwu";
            _icon.DoubleClick += (_, _) => OpenPanel();

            _agent.Changed += () => Post(Refresh);
            _agent.Monitor.DeviceEvent += e => Log.Info(e);

            // Presence transitions cover the interesting changes, but the registered / credential /
            // pause state can also be changed from the CLI while the tray is running. A slow tick is
            // enough for those, and is far cheaper than refreshing once per heartbeat.
            _recheck.Tick += (_, _) => Refresh();

            Application.Idle += OnFirstIdle;
        }

        void OnFirstIdle(object? sender, EventArgs e)
        {
            Application.Idle -= OnFirstIdle;
            if (_started) return;
            _started = true;

            // Start on a pool thread. There is no SynchronizationContext there, so the agent's awaits
            // cannot be captured by the WinForms context at all - the engine never depends on the UI
            // thread being responsive.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _agent.StartAsync();
                    Post(AfterStart);
                }
                catch (Exception ex)
                {
                    Log.Error($"tray: agent failed to start: {ex}");
                    Post(() => ShowBalloon("启动失败", ex.Message, ToolTipIcon.Error));
                }
            });

            _recheck.Start();
        }

        void AfterStart()
        {
            Refresh();

            if (!Registration.IsRegistered)
                ShowBalloon("凭据提供程序尚未注册", "免密解锁不会生效。打开管理面板，点「安装」。", ToolTipIcon.Warning);
            else if (!CredentialStore.Exists)
                ShowBalloon("还没有录入口令", "打开管理面板，在「账户口令」里录入一次。", ToolTipIcon.Warning);

            Log.Info($"tray started (apartment={Thread.CurrentThread.GetApartmentState()})");
        }

        /// <summary>Called after the message loop has returned.</summary>
        public void Shutdown()
        {
            _recheck.Stop();
            _recheck.Dispose();

            // Dispose on a pool thread. By now Application.Run has returned and nothing is pumping any
            // more, so a synchronous wait on a task whose continuation was captured by the WinForms
            // context would deadlock.
            try { Task.Run(() => _agent.DisposeAsync().AsTask()).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Warn($"tray: dispose failed: {ex.Message}"); }

            _icon.Visible = false;
            _currentIcon?.Dispose();
            _icon.Dispose();
            _marshal.Dispose();
        }

        void Post(Action action)
        {
            if (_marshal.IsDisposed) return;
            try { _marshal.BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        /// <summary>
        /// Turns the whole tool off or back on, exactly like the panel's 暂停 / 启用: the marker file
        /// is read by the credential provider itself, so the lock screen is back to Windows' own as
        /// soon as it exists - no agent restart, no elevation, effective at the next lock.
        ///
        /// This used to write a timed pause (`pause.until`) instead, which only suppressed the
        /// lock-on-absence feature. That feature is off by default now (this build unlocks, it does
        /// not lock), so there was nothing left for a one-hour pause to suppress.
        /// </summary>
        void SetPaused(bool paused)
        {
            try
            {
                if (paused)
                {
                    Paths.EnsureDataDir();
                    File.WriteAllText(Paths.DisableCpFile, "");
                }
                else if (File.Exists(Paths.DisableCpFile))
                {
                    File.Delete(Paths.DisableCpFile);
                }

                Log.Info(paused ? "disabled from tray" : "re-enabled from tray");
            }
            catch (Exception ex)
            {
                ShowBalloon("操作失败", ex.Message, ToolTipIcon.Error);
            }
            Refresh();
        }

        /// <summary>
        /// Repaints the tray. Every write to <see cref="NotifyIcon"/> goes through Shell_NotifyIcon, and
        /// a fresh icon means a GDI+ bitmap plus an HICON round trip, so each field is only touched when
        /// its value actually changed.
        /// </summary>
        void Refresh()
        {
            var present = _agent.Present;
            var registered = Registration.IsRegistered;
            var hasCredential = CredentialStore.Exists;

            // Same one-liner as the panel's banner, on purpose: the tray and the window must not
            // disagree about whether password-free unlocking is available right now.
            var paused = File.Exists(Paths.DisableCpFile);

            var state = !registered ? "凭据提供程序未注册 —— 免密不可用"
                : !hasCredential ? "还没有录入口令 —— 免密不可用"
                : paused ? "已暂停 —— 免密不可用"
                : present ? "已连接 —— 免密可用"
                : "未连接 —— 免密不可用";

            if (state != _lastStatus)
            {
                _statusItem.Text = state;
                _lastStatus = state;
            }

            var toggle = paused ? "启用" : "暂停";
            if (toggle != _toggleItem.Text) _toggleItem.Text = toggle;

            var tip = $"btwu — {state}\n触发设备: {string.Join(", ", _cfg.Triggers.Select(t => t.Name))}";
            if (tip.Length > 127) tip = tip[..124] + "...";
            if (tip != _lastTooltip)
            {
                _icon.Text = tip;
                _lastTooltip = tip;
            }

            var colour = !registered || !hasCredential ? Color.FromArgb(191, 135, 0)
                : paused ? Color.FromArgb(120, 120, 120)
                : present ? Color.FromArgb(35, 145, 70)
                : Color.FromArgb(150, 60, 60);

            if (_lastColour != colour)
            {
                var icon = MakeIcon(colour);
                _icon.Icon = icon;
                _currentIcon?.Dispose();
                _currentIcon = icon;
                _lastColour = colour;
            }
        }

        void ShowBalloon(string title, string text, ToolTipIcon icon) =>
            _icon.ShowBalloonTip(8000, title, text, icon);

        /// <summary>
        /// Opens the management window as its own process, detached from the agent.
        ///
        /// The menu's whole design is "the panel is where you change things", so this has to be one
        /// click and it has to be reliable. A separate process, not a window in this one: the panel
        /// runs device enumeration, elevated child processes and job dialogs, and none of that
        /// belongs in the process that has to keep publishing presence for the lock screen.
        ///
        /// <c>CreateNoWindow</c> because btwu.exe is a console-subsystem binary - going through the
        /// shell would give the panel a console window that then has to hide itself again, with a
        /// visible flash.
        /// </summary>
        static void OpenPanel()
        {
            try
            {
                var exe = Environment.ProcessPath;
                if (exe is null) return;

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "gui")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = AppContext.BaseDirectory,
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"cannot open the panel: {ex.Message}");
            }
        }

        static Icon MakeIcon(Color colour)
        {
            using var bitmap = new Bitmap(16, 16);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using var fill = new SolidBrush(colour);
                graphics.FillEllipse(fill, 1, 1, 13, 13);
                using var outline = new Pen(Color.FromArgb(150, 0, 0, 0));
                graphics.DrawEllipse(outline, 1, 1, 13, 13);
            }

            var handle = bitmap.GetHicon();
            try
            {
                using var fromHandle = Icon.FromHandle(handle);
                return (Icon)fromHandle.Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
    }
}
