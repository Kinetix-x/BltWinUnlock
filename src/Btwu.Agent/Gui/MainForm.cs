using System.Globalization;
using Btwu.Core;

namespace Btwu.Agent;

/// <summary>
/// The management window: one page, no tabs, in the order a person actually needs things.
///
/// <b>This is a release build and it is deliberately small.</b> The page shows the current state and
/// offers exactly eight things: the presence verdict, which devices count as the key, the account
/// password, pause/enable, install/update, and diagnostics. Text appears
/// only where the value beside it is not self-explanatory; everything else lives in the README and in
/// the package's 使用说明.txt.
///
/// Two deliberate omissions worth knowing about before "fixing" them:
///
///   * <b>No device-category gate.</b> Every paired device can be ticked, and whatever is ticked
///     counts as presence while it is connected - a headset included. The gate still exists in the
///     CLI (<c>btwu enroll --require-hid</c>) and <c>doctor</c> reports it; the window writes
///     <c>Any</c> so that what you tick is what you get. The device list still shows each device's HID
///     identity and greys out the ones that are not input devices, and the note under the list says
///     so in one line - see <see cref="RefreshDeviceNote"/>.
///   * <b>No unlock-mode switch, no auto-unlock / lock-on-absence toggle.</b> Those defaults (Enter
///     mode, both on) are what the release is for; <c>config.json</c> and <c>btwu autosubmit</c> still
///     change them, and the README says where.
///
/// What reads state does so through <see cref="Btwu.Core"/> in this process. What changes state that
/// needs elevation goes through a child process (see <see cref="Host"/> and <see cref="Scripts"/>),
/// because a process cannot raise its own token.
///
/// <b>Layout rules for anyone editing this file</b> (all were real bugs; see docs/技术说明.md 第 6 节): sizes come
/// from content - containers <c>AutoSize</c>, buttons <c>AutoSize</c>, and the page is a
/// <see cref="TableLayoutPanel"/> with AutoSize rows around one 100% content row; the numbers that
/// remain are design pixels and go through <see cref="Ui.Px"/>; never stack <c>Dock=Fill</c> under
/// <c>Dock=Top</c>/<c>Dock=Bottom</c>.
/// </summary>
sealed class MainForm : Form
{
    // ------------------------------------------------------------------- header
    Label _banner = new();
    Label _detail = new();

    // ----------------------------------------------------------------- settings
    ListView _devices = new();
    Label _devicesNote = new();
    Label _credentialState = new();

    // -------------------------------------------------------------------- tools
    Label _installState = new();
    Button _installButton = new();

    /// <summary>Kept for the lifetime of the window: GDI+ reads from it after the first paint.</summary>
    MemoryStream? _qrStream;

    ToolStripStatusLabel _statusText = new();
    ToolStripStatusLabel _statusRights = new();
    readonly System.Windows.Forms.Timer _poll_timer = new() { Interval = 3000 };

    public MainForm()
    {
        Text = "Btwu —— 蓝牙在场免密解锁";

        // Scaling is done by hand through Ui.Px, so the built-in auto-scale stays out of the way -
        // if it ever decided to fire, every number would be doubled twice.
        AutoScaleMode = AutoScaleMode.None;

        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(Ui.Px(520), Ui.Px(420));
        // Narrower than the tabbed version (which had a device list plus six pages of room to fill) and
        // tall enough that the single column fits without scrolling on a normal desktop. FitToScreen
        // clamps it on a smaller one, and the page scrolls from there.
        Size = new Size(Ui.Px(700), Ui.Px(810));
        Font = new Font("Microsoft YaHei UI", 9f);

        // One page, no TabControl: the whole window is a header band and a scrolling column, with the
        // status bar as the last row. See §8.7 - stacking Dock=Fill under Dock=Top/Dock=Bottom is what
        // let the header cover the first line of content once.
        _banner = new Label
        {
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = Ui.Pad(12, 8, 12, 8),
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            BackColor = SystemColors.ControlLight,
            Text = "正在读取…",
        };
        _detail = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Padding = Ui.Pad(12, 6, 12, 4),
        };

        var header = Ui.Stack();
        Ui.Add(header, _banner);
        Ui.Add(header, _detail);

        var content = Ui.Stack();
        // 顺序 = 第一次用这个工具时该做的事的顺序：先装，再选设备，再录口令。
        // 「安装」放在最上面，是为了让第一次打开的人按直觉从上往下走一遍就能用起来。
        Ui.Add(content, BuildInstallSection());
        Ui.Add(content, BuildDeviceSection());
        Ui.Add(content, BuildCredentialSection());
        Ui.Add(content, BuildSwitchSection());
        Ui.Add(content, BuildDiagnosticsSection());
        Ui.Add(content, BuildAboutSection());

        var host = new Panel { AutoScroll = true };
        host.Controls.Add(content);

        var errors = new StatusStrip();
        _statusRights.Spring = true;
        _statusRights.TextAlign = ContentAlignment.MiddleLeft;
        _statusRights.ForeColor = SystemColors.GrayText;
        errors.Items.Add(_statusText);
        errors.Items.Add(_statusRights);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // header
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // content
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // status bar
        root.RowCount = 3;
        header.Dock = DockStyle.Fill;
        host.Dock = DockStyle.Fill;
        errors.Dock = DockStyle.Fill;
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(host, 0, 1);
        root.Controls.Add(errors, 0, 2);

        Controls.Add(root);

        _poll_timer.Tick += (_, _) => RefreshStatus();
        Shown += (_, _) =>
        {
            Ui.FitToScreen(this);
            LoadSettings();
            RefreshStatus();
            _ = LoadDevicesAsync();
            _poll_timer.Start();
        };
    }

    // ==================================================================== sections

    Control BuildDeviceSection()
    {
        _devices.View = View.Details;
        _devices.CheckBoxes = true;
        _devices.FullRowSelect = true;
        _devices.GridLines = true;
        _devices.Columns.Add("设备", Ui.Px(260));
        _devices.Columns.Add("类型", Ui.Px(90));
        _devices.Columns.Add("身份", Ui.Px(90));
        _devices.Columns.Add("此刻", Ui.Px(80));
        _devices.ItemChecked += (_, _) => RefreshDeviceNote();

        _devicesNote = Ui.Note("");

        var children = Ui.Stack();
        Ui.Add(children, Ui.Fixed(_devices, 160));
        Ui.Add(children, _devicesNote);
        Ui.Add(children, Ui.ButtonRow(
            Ui.Action("重新枚举", () => _ = LoadDevicesAsync()),
            Ui.Action("保存", SaveSettings)));

        return Ui.Section("触发设备", children);
    }

    Control BuildCredentialSection()
    {
        _credentialState = Ui.Note("");

        var children = Ui.Stack();
        Ui.Add(children, _credentialState);
        Ui.Add(children, Ui.ButtonRow(
            Ui.Action("录入 / 更新口令", EnrollElevated),
            Ui.Action("删除口令", DeleteCredential)));

        return Ui.Section("账户口令", children);
    }

    Control BuildSwitchSection()
    {
        var children = Ui.Stack();
        Ui.Add(children, Ui.ButtonRow(
            Ui.Action("暂停", () => SetEnabled(paused: true)),
            Ui.Action("启用", () => SetEnabled(paused: false))));

        return Ui.Section("暂停与启用", children);
    }

    Control BuildInstallSection()
    {
        _installState = Ui.Note("");
        _installButton = Ui.Action("安装", InstallOrUpdate);

        var children = Ui.Stack();
        Ui.Add(children, _installState);
        Ui.Add(children, Ui.ButtonRow(_installButton));
        Ui.Add(children, Ui.Note(
            "卸载：双击程序目录下的 uninstall-admin.cmd（会弹一次 UAC），加 removedata 参数连数据目录一起删。"));

        return Ui.Section("安装", children);
    }

    /// <summary>
    /// The footer: who wrote it, and the donation QR.
    ///
    /// The last block of the page, not a bar pinned to the bottom of the window: the page is a
    /// scrolling column, and a pinned footer would eat 110 logical pixels of every screen height for
    /// something read once.
    /// </summary>
    Control BuildAboutSection()
    {
        var text = new Label
        {
            AutoSize = true,
            Margin = Ui.Pad(12, 6, 0, 0),
            Text = "（酷安）@Kinetix_x\n← 支持作者（支付宝）",
        };

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Ui.Pad(0, 2, 0, 0),
        };

        var qr = MakeQrCode();
        if (qr is not null) row.Controls.Add(qr);
        row.Controls.Add(text);

        var children = Ui.Stack();
        Ui.Add(children, row);
        return Ui.Section("关于", children);
    }

    PictureBox? MakeQrCode()
    {
        try
        {
            var assembly = typeof(MainForm).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("alipay.jpg", StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                Log.Warn("donation QR is missing from the assembly");
                return null;
            }

            using var raw = assembly.GetManifestResourceStream(name);
            if (raw is null) return null;

            // The stream has to OUTLIVE the Image: GDI+ decodes lazily and reads from it on later
            // paints, so a `using` here would blow up with "parameter is not valid" on the next
            // redraw. Keeping the MemoryStream in a field is the cheap way to guarantee that.
            _qrStream = new MemoryStream();
            raw.CopyTo(_qrStream);
            _qrStream.Position = 0;

            var size = Ui.Px(110);
            return new PictureBox
            {
                Image = Image.FromStream(_qrStream),
                Size = new Size(size, size),
                SizeMode = PictureBoxSizeMode.Zoom,
                Margin = Ui.Pad(0, 0, 0, 0),
            };
        }
        catch (Exception ex)
        {
            // A missing QR is not worth failing a release over.
            Log.Warn($"cannot load the donation QR: {ex.Message}");
            return null;
        }
    }

    Control BuildDiagnosticsSection()
    {
        var children = Ui.Stack();
        Ui.Add(children, Ui.ButtonRow(
            Ui.Action("运行自检", () => RunJob("doctor", Array.Empty<string>(), true,
                "全链路自检",
                "注册、ACL、agent、设备的完整检查（需要一次 UAC 授权）。")),
            Ui.Action("验证口令", () => RunJob("verify", new[] { "--compare" }, elevated: true,
                "验证保管的口令",
                "用锁屏同样的打包方式把口令交给 Windows：一次成功即证明加密、打包和口令都对。")),
            Ui.Action("复制诊断信息", CopyDiagnostics),
            Ui.Action("打开数据目录", Shell.OpenDataDirectory)));

        return Ui.Section("诊断", children);
    }

    // ====================================================================== state

    void RefreshStatus()
    {
        try
        {
            var cfg = AppConfig.Load();
            var now = DateTime.UtcNow;
            var installed = File.Exists(Path.Combine(Host.InstallDirectory, "btwu.exe"));
            var registered = Registration.IsRegistered;
            var credentialExists = CredentialStore.Exists;
            var disabled = File.Exists(Paths.DisableCpFile);
            var pauseUntil = PresenceChannel.ReadSuppressUntil();
            var paused = pauseUntil > now.Ticks;

            var haveState = PresenceChannel.TryRead(out var snapshot);
            var age = haveState && snapshot.HeartbeatTicks > 0
                ? now - new DateTime(snapshot.HeartbeatTicks, DateTimeKind.Utc)
                : TimeSpan.MaxValue;

            // ------------------------------------------------------------------ banner
            // One short verdict, in the words the state is actually about: is the key connected, and
            // therefore is password-free unlocking available. Everything that used to be a second and
            // third line of detail is either this sentence or the status bar at the bottom.
            (string text, Color back) verdict;
            if (!installed)
                verdict = ("尚未安装 —— 免密不可用。", Color.FromArgb(232, 232, 232));
            else if (disabled)
                verdict = ("已暂停 —— 免密不可用。", Color.FromArgb(255, 214, 165));
            else if (!registered)
                verdict = ("凭据提供程序未注册 —— 免密不可用。", Color.FromArgb(255, 214, 165));
            else if (!credentialExists)
                verdict = ("还没有录入口令 —— 免密不可用。", Color.FromArgb(255, 214, 165));
            else if (cfg.Triggers.Count == 0)
                verdict = ("还没有选择触发设备 —— 免密不可用。", Color.FromArgb(255, 214, 165));
            else if (!haveState)
                verdict = ("后台 agent 从未启动过 —— 免密不可用。", Color.FromArgb(255, 199, 199));
            // Staleness alone does NOT mean the agent is broken: the heartbeat deliberately stops
            // re-stamping while the device is away (there is nothing the lock screen could act on), so
            // "absent two hours ago and never restamped" is the normal shape after walking away with
            // the keyboard. What is a real problem is a *stale file that still says present* - that is
            // an agent that died while the device was there, and the lock screen will refuse to unlock
            // with no explanation at all. Those two must not share a message.
            else if (age > PresenceChannel.MaxAge && snapshot.Present)
                verdict = ("后台 agent 停止发布，而停下时设备在场 —— 免密不可用。请重启后台 agent。", Color.FromArgb(255, 199, 199));
            else if (snapshot.Present)
                verdict = ("已连接 —— 免密可用。", Color.FromArgb(198, 232, 198));
            else
                verdict = ("未连接 —— 免密不可用（设备回来即可用）。", Color.FromArgb(255, 242, 204));

            _banner.Text = verdict.text;
            _banner.BackColor = verdict.back;

            // ------------------------------------------------------------------ detail
            var devices = cfg.Triggers.Count == 0
                ? "未选择"
                : string.Join("、", cfg.Triggers.Select(t => t.Name));
            _detail.Text = $"触发设备：{devices}";

            var cooldownLeft = PresenceChannel.ReadUnlockCooldownUntil() - now.Ticks;
            if (cooldownLeft > 0)
                _detail.Text += $"　　（刚解锁，{cooldownLeft / TimeSpan.TicksPerSecond} 秒内不重复解锁）";

            _statusText.Text = haveState
                ? $"状态文件 {Describe(age)}写入　|　数据目录 {Paths.DataDir}"
                : $"没有状态文件　|　数据目录 {Paths.DataDir}";
            _statusRights.Text = Registration.IsElevated ? "管理员" : "普通用户";

            RefreshInstallState(installed);
            RefreshCredentialState();
        }
        catch (Exception ex)
        {
            // A status view that throws is worse than one that is momentarily wrong.
            _banner.Text = $"读取状态失败：{ex.Message}";
            _banner.BackColor = Color.FromArgb(255, 199, 199);
        }
    }

    void RefreshInstallState(bool installed)
    {
        var installedExe = Path.Combine(Host.InstallDirectory, "btwu.exe");
        _installState.Text = installed
            ? $"已安装到 {Host.InstallDirectory}"
              + $"（{new FileInfo(installedExe).LastWriteTime:yyyy-MM-dd HH:mm}）"
              + (Host.RunningFromInstallDirectory
                  ? "\n本程序正运行在安装目录里：只能重做注册，不会更新文件。"
                  : "")
            : "未安装。";

        // 装过之后这个按钮只会做一件事：更新。文案跟着状态走，不要让人猜它这次会干什么。
        _installButton.Text = installed ? "更新" : "安装";
    }

    void RefreshCredentialState()
    {
        // Only loaded when the file is actually readable. TryLoad() on a file whose ACL excludes us
        // fails inside DPAPI and writes an ERR line to agent.log - harmless, but this runs on a timer
        // and a diagnostic that fills the log it is meant to help read is worse than none.
        //
        // The unreadable case used to explain the ACL in a parenthesis. It does not need to: the
        // window is running unelevated, and every button that touches cred.bin raises UAC by itself.
        var unreadable = CredentialStore.ExistsButUnreadable();
        _credentialState.Text = unreadable
            ? "已保存"
            : CredentialStore.Exists
                ? DescribeCredential(CredentialStore.TryLoad())
                : "未保存";
    }

    static string LocalTime(long ticks) => ticks <= 0
        ? "-"
        : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    static string Describe(TimeSpan age)
    {
        if (age == TimeSpan.MaxValue) return "从未";
        if (age.TotalSeconds < 1.5) return "刚刚";
        if (age.TotalSeconds < 90) return $"{age.TotalSeconds:F0} 秒前";
        if (age.TotalMinutes < 90) return $"{age.TotalMinutes:F1} 分钟前";
        return $"{age.TotalHours:F1} 小时前";
    }

    static string DescribeCredential(StoredCredential? credential) =>
        credential is null
            ? "文件存在但解不开（DPAPI 失败，或换了机器 / 账户）"
            : $"已保存 —— {credential.UserName}@{credential.DomainOrLocalMachine}";

    // =================================================================== devices

    async Task LoadDevicesAsync()
    {
        try
        {
            _devicesNote.Text = "正在枚举已配对的蓝牙设备…";
            _devices.Enabled = false;
            Log.Info("gui: 开始枚举已配对设备");

            var found = await Task.Run(() => DeviceInventory.ListAsync());
            Log.Info($"gui: 枚举完成，{found.Count} 台");

            var configured = AppConfig.Load().Triggers
                .Select(t => t.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            _devices.BeginUpdate();
            _devices.Items.Clear();
            foreach (var device in found)
            {
                var item = new ListViewItem(new[]
                {
                    device.Name,
                    device.Kind,
                    device.Identity.KindText,
                    device.Connected ? "已连接" : "未连接",
                })
                {
                    Tag = device,
                    Checked = configured.Contains(device.Id),
                };

                // Pure black for every row, deliberately. The previous version greyed out anything that
                // was not a HID keyboard to hint at the category gate - and with the gate gone, grey
                // text just reads as "disabled/untickable", which is the opposite of the rule now.
                // The identity is still in its own column, and the note says which picks are not input
                // devices.
                _devices.Items.Add(item);
            }
            _devices.EndUpdate();

            if (_devices.Items.Count == 0)
            {
                _devicesNote.Text = "没有找到已配对的蓝牙设备。请先在 Windows 设置里配对那只键盘。";
                return;
            }

            if (!_devices.Items.Cast<ListViewItem>().Any(i => i.Checked))
                Check((_, device) => device.Identity.Kind == HidKind.Keyboard);
            RefreshDeviceNote();
        }
        catch (Exception ex)
        {
            _devicesNote.Text = $"枚举失败：{ex.Message}";
        }
        finally
        {
            _devices.Enabled = true;
        }
    }

    void Check(Func<ListViewItem, DeviceItem, bool> predicate)
    {
        _devices.BeginUpdate();
        foreach (ListViewItem item in _devices.Items)
            if (item.Tag is DeviceItem device) item.Checked = predicate(item, device);
        _devices.EndUpdate();
        RefreshDeviceNote();
    }

    /// <summary>
    /// The one line that replaces the category gate: what you tick is what counts, so the list says
    /// which of the ticked devices are not input devices. Not a confirmation dialog - just a fact, in
    /// the place where the decision is made.
    /// </summary>
    void RefreshDeviceNote()
    {
        if (_devices.Items.Count == 0) return;

        var selected = _devices.CheckedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<DeviceItem>().ToList();
        if (selected.Count == 0)
        {
            _devicesNote.Text = "还没有勾选。没有触发设备时，锁屏上的凭据只会锁屏、永远不会解锁。";
            return;
        }

        var notInput = selected.Where(d => !d.Identity.HidPresent).ToList();
        _devicesNote.Text = notInput.Count == 0
            ? $"已勾选 {selected.Count} 台。不检查设备类别：勾上的设备连着就算你在场。"
            : $"已勾选 {selected.Count} 台。注意 {string.Join("、", notInput.Select(d => d.Name))} 不是输入设备"
              + "（手机 / 耳机 / 音箱都算不清），它连着也就等于你在场。";
    }

    void SaveSettings()
    {
        var selected = _devices.CheckedItems.Cast<ListViewItem>()
            .Select(i => i.Tag).OfType<DeviceItem>().ToList();
        if (selected.Count == 0)
        {
            Shell.Error(this, "Btwu", "至少要勾选一只触发设备。");
            return;
        }

        try
        {
            var cfg = AppConfig.Load();
            cfg.Triggers = selected.Select(TriggerDevice.From).ToList();

            // The window does not offer a category gate (see the class comment). Writing it
            // explicitly keeps config.json consistent with what the list shows: whatever is ticked
            // counts. `doctor` prints this, and the CLI can still set it to Keyboard.
            cfg.RequiredHidKind = "Any";

            // This build is unlock-only: it never locks the screen on its own. That is why the grace
            // period is gone from the window - it only ever answered "how long after the device leaves
            // before we lock", and there is no longer a lock to delay. The agent still understands the
            // flag (and `btwu pause`), so anyone who wants the old behaviour can turn it back on in
            // config.json; the panel just does not offer it.
            cfg.LockOnAbsence = false;
            cfg.Save();
        }
        catch (Exception ex)
        {
            Shell.Error(this, "Btwu", $"保存失败：{ex.Message}");
            return;
        }

        RefreshStatus();

        // Saving without restarting is the one thing that makes this window lie: the agent reads
        // config.json exactly once, at startup. So the save does the restart too, and reports it in
        // the status bar instead of asking.
        var (ok, message) = AgentControl.Exists
            ? AgentControl.Restart()
            : (false, $"计划任务 {AgentControl.TaskName} 不存在 —— 先点「安装」。");
        _statusText.Text = ok ? "已保存并重启后台 agent，新设置已生效。" : $"已保存，但重启失败：{message}";

        if (!ok && Shell.Confirm(this, "需要管理员", message + "\n\n要以管理员身份重试一次吗？（会弹出 UAC）"))
            RunJob("agent-restart", Array.Empty<string>(), elevated: true,
                "重启后台 agent", "结束后台 agent 并重新启动计划任务，让它读到新的配置。");
    }

    // ================================================================ credential

    async void EnrollElevated()
    {
        if (await EnrollAsync())
            Shell.Info(this, "Btwu", "口令已保存。\n\n按 Win+L 实测一次：设备在场时应当能免密回到桌面。");
    }

    /// <summary>
    /// Opens the elevated enrollment dialog and waits for it to finish. Returns whether a credential
    /// exists afterwards, so the first-install path can chain into it instead of telling the user to
    /// come back and do this later.
    /// </summary>
    async Task<bool> EnrollAsync()
    {
        var logPath = Host.NewLogPath("enroll");
        var command = $"--enroll-gui --log {Host.Quote(logPath)}";

        var process = Host.Start(command, elevated: true, out var error);
        if (process is null)
        {
            Shell.Error(this, "Btwu", error ?? "无法启动录入对话框。");
            return false;
        }

        _statusText.Text = "请在刚打开的窗口里完成录入…";
        UseWaitCursor = true;
        Enabled = false;

        // Watch for either signal: the exit code of a runas child is not dependable through
        // ShellExecute, and the dialog writes its own completion marker into the transcript.
        await Task.Run(() =>
        {
            for (var i = 0; i < 1800; i++)
            {
                try
                {
                    if (process.WaitForExit(500)) return;
                }
                catch
                {
                    return;
                }

                if (TranscriptHasMarker(logPath)) return;
            }
        });

        process.Dispose();
        Enabled = true;
        UseWaitCursor = false;

        LoadSettings();
        RefreshStatus();
        return CredentialStore.Exists;
    }

    static bool TranscriptHasMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains(GuiEntry.JobDonePrefix);
        }
        catch
        {
            return false;
        }
    }

    void DeleteCredential()
    {
        if (!CredentialStore.Exists)
        {
            Shell.Info(this, "Btwu", "本来就没有保管的口令。");
            return;
        }
        if (!Shell.Confirm(this, "删除保管的口令",
                $"删除 {Paths.CredentialFile} 之后不会再有免密解锁，触发设备与其余配置保留。\n\n确定删除吗？"))
            return;

        RunJob("delete-credential", Array.Empty<string>(), elevated: true,
            "删除保管的口令", "删除 cred.bin（只有 SYSTEM 与 Administrators 能读写，需要一次 UAC）。");

        RefreshStatus();
    }

    // ============================================================ pause / enable

    /// <summary>
    /// Pause and resume the whole feature: the marker file is read by the DLL itself, so the lock
    /// screen is back to Windows' own the moment it exists - no agent, no elevation, no unlock/logout.
    ///
    /// This used to be two separate things (a timed "don't lock when I leave" pause, and this kill
    /// switch). With lock-on-absence off - this build is unlock-only - a lock suppression has nothing
    /// left to suppress, so the pair collapsed into one button each.
    /// </summary>
    void SetEnabled(bool paused)
    {
        try
        {
            if (paused)
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllText(Paths.DisableCpFile, "");
            }
            else if (File.Exists(Paths.DisableCpFile))
            {
                File.Delete(Paths.DisableCpFile);
            }
        }
        catch (Exception ex)
        {
            Shell.Error(this, "Btwu", ex.Message);
            return;
        }

        RefreshStatus();
    }

    // ====================================================================== install

    void InstallOrUpdate()
    {
        var installed = File.Exists(Path.Combine(Host.InstallDirectory, "btwu.exe"));

        // 顺序是「先录口令、再安装」：先有口令，装完就能直接用。第一次安装把两步合成**一次授权** ——
        // 面板把 --enroll-first 交给那个已经提权的作业进程，由它在同一个令牌下先弹录入对话框，
        // 再跑安装脚本。否则首次要连着弹两次 UAC，第二次会让人以为第一次没生效。
        var args = new List<string> { "--keep-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) };
        if (!installed && !CredentialStore.Exists)
        {
            if (!Shell.Confirm(this, "先录入口令",
                    "第一次安装要先录入一次 Windows 账户口令 —— 免密解锁用的就是它。\n\n现在一起做完吗？"))
                return;
            args.Add("--enroll-first");
        }

        var description = "复制到 Program Files、注册凭据提供程序与登录自启，最后跑一次自检。一次 UAC 授权。";
        if (Host.RunningFromInstallDirectory)
            description += "\n\n⚠ 本程序正运行在安装目录里，这一步只会重做注册，不会更新文件。";

        RunJob("install", args.ToArray(), elevated: true,
            installed ? "更新到 Program Files" : "安装到 Program Files", description);

        RefreshStatus();
    }

    // ================================================================ diagnostics

    /// <summary>Everything a support conversation would ask for, on the clipboard in one shot.</summary>
    void CopyDiagnostics()
    {
        var text = new System.Text.StringBuilder();
        var area = Screen.FromControl(this).WorkingArea;
        text.AppendLine("=== Btwu 诊断快照 ===");
        text.AppendLine($"时间:       {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"版本目录:   {AppContext.BaseDirectory}");
        text.AppendLine($"安装目录:   {Host.InstallDirectory}（{(File.Exists(Path.Combine(Host.InstallDirectory, "btwu.exe")) ? "已安装" : "不存在")}）");
        text.AppendLine($"数据目录:   {Paths.DataDir}");
        text.AppendLine($"权限:       {(Registration.IsElevated ? "管理员" : "普通用户")} / {Environment.UserName}");
        text.AppendLine($"系统:       {Environment.OSVersion} / {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
        text.AppendLine($"显示:       DPI {DeviceDpi}（缩放 {Ui.Scale * 100:F0}%）；窗口 {Width}x{Height}；工作区 {area.Width}x{area.Height}");
        text.AppendLine();

        var cfg = AppConfig.Load();
        text.AppendLine($"凭据提供程序: {(Registration.IsRegistered ? "已注册" : "未注册")}");
        text.AppendLine($"  DLL:        {Registration.RegisteredDllPath ?? "-"}");
        text.AppendLine($"  Threading:  {Registration.RegisteredThreadingModel ?? "-"}");
        text.AppendLine($"  CLSID 已列入 Winlogon: {Registration.IsListedWithWinlogon}");
        text.AppendLine($"保管口令:     {(CredentialStore.Exists ? "存在" : "不存在")}"
                        + (CredentialStore.ExistsButUnreadable() ? "（当前权限读不到）" : ""));
        text.AppendLine($"解锁方式:     {(File.Exists(Paths.AutoSubmitFile) ? "全自动" : "回车模式")}");
        text.AppendLine($"应急开关:     {(File.Exists(Paths.DisableCpFile) ? "已停用凭据提供程序" : "正常")}");
        text.AppendLine();
        text.AppendLine($"触发设备 ({cfg.Triggers.Count}):");
        foreach (var trigger in cfg.Triggers)
            text.AppendLine($"  - {trigger.Name} | {trigger.Kind} | {trigger.HidClass} | {trigger.Id}");
        text.AppendLine($"身份闸门:     {cfg.RequiredHidKind}");
        text.AppendLine($"账户:         {cfg.UserName} @ {cfg.Domain}");
        text.AppendLine($"行为:         lockOnAbsence={cfg.LockOnAbsence} autoUnlock={cfg.AutoUnlock} "
                        + $"requireSeen={cfg.RequireSeenPresentBeforeLocking}");
        text.AppendLine($"时序:         grace={cfg.GraceSeconds}s startupGrace={cfg.StartupGraceSeconds}s "
                        + $"unlockWindow={cfg.UnlockWindowSeconds}s heartbeat={cfg.HeartbeatMilliseconds}ms poll={cfg.PollSeconds}s");
        text.AppendLine();
        text.AppendLine($"计划任务 {AgentControl.TaskName}: {(AgentControl.Exists ? "存在" : "不存在")}");
        text.AppendLine();

        if (PresenceChannel.TryRead(out var snapshot))
        {
            var age = DateTime.UtcNow - new DateTime(snapshot.HeartbeatTicks, DateTimeKind.Utc);
            text.AppendLine("presence.dat:");
            text.AppendLine($"  present={snapshot.Present} autoUnlock={snapshot.AutoUnlock} "
                            + $"unlockWindow={snapshot.UnlockWindowSeconds} 心跳在 {age.TotalSeconds:F1}s 前");
            var (unlock, reason) = PresenceChannel.Evaluate(snapshot, DateTime.UtcNow);
            text.AppendLine($"  判定: {(unlock ? "会解锁" : "不会解锁")} —— {reason}");
        }
        else
        {
            text.AppendLine("presence.dat: 不存在或无法解析（agent 没在运行）");
        }

        var cooldown = PresenceChannel.ReadUnlockCooldownUntil() - DateTime.UtcNow.Ticks;
        text.AppendLine($"解锁静默期:   {(cooldown > 0 ? $"{cooldown / TimeSpan.TicksPerSecond}s" : "无")}");
        text.AppendLine();
        text.AppendLine("--- agent.log 尾部 40 行 ---");
        text.AppendLine(Tail(Paths.LogFile, 40));
        text.AppendLine("--- credprov.log 尾部 20 行 ---");
        text.AppendLine(Tail(Paths.CredProvLogFile, 20));

        try
        {
            Clipboard.SetText(text.ToString());
            Shell.Info(this, "Btwu", "诊断快照已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            Shell.Error(this, "Btwu", $"复制失败：{ex.Message}");
        }
    }

    static string Tail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "(没有这个文件)";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n');
            var take = all.Skip(Math.Max(0, all.Length - lines));
            return string.Join("\n", take).TrimEnd();
        }
        catch (Exception ex)
        {
            return $"(读不到: {ex.Message})";
        }
    }

    // ===================================================================== plumbing

    /// <summary>
    /// Nothing to load into controls any more: the window's only inputs are the device checkboxes
    /// (filled by <see cref="LoadDevicesAsync"/>) and the buttons. Kept because the enrollment dialog
    /// and the save path both call it, and because a page that reads nothing and writes nothing is
    /// exactly what "unlock-only, no settings" means.
    /// </summary>
    void LoadSettings()
    {
        RefreshCredentialState();
    }

    /// <summary>
    /// Runs one <c>btwu</c> verb in a child process and streams its transcript. Always a child
    /// process, even for verbs that would work in-process: the transcript mechanism is the only thing
    /// that behaves the same elevated and unelevated, and a diagnostic that behaves differently
    /// depending on how it was started is a diagnostic you cannot trust.
    /// </summary>
    JobResult RunJob(string verb, string[] options, bool elevated, string title, string description)
    {
        var logPath = Host.NewLogPath(verb);
        var arguments = new List<string> { "--job", verb, "--log", logPath };
        arguments.AddRange(options);

        var line = string.Join(' ', arguments.Select(Host.Quote));
        var result = JobDialog.Show(this, title, description, logPath, () =>
        {
            var process = Host.Start(line, elevated, out var error);
            return (process, error);
        });

        RefreshStatus();
        _statusText.Text = $"{title}：{result.Describe()}";
        return result;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll_timer.Stop();
        _poll_timer.Dispose();
        base.OnFormClosed(e);
    }
}
