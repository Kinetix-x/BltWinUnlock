using System.Globalization;
using System.Text;
using Btwu.Core;

namespace Btwu.Agent;

/// <summary>
/// The enrollment dialog: choose the trigger devices, confirm the account form, type the password
/// once.
///
/// It runs elevated and in its own process, which is not a stylistic choice. Two hard constraints put
/// it here:
///
///   * <b>cred.bin is writable only by SYSTEM and Administrators</b> after <c>install.ps1</c> has run,
///     so an ordinary-privilege save would fail - after the user had already typed their password.
///   * <b>a password cannot be collected through a pipe.</b> The CLI's <c>enroll</c> reads the console
///     one character at a time precisely so it never appears in a transcript; that requires a real
///     window with a real keyboard, which is what this form is.
///
/// The validations mirror <c>btwu enroll</c> one for one - the HID gate, the <c>LogonUser</c> probe,
/// the account-SID check - because they exist to stop the same two silent failures: a phone enrolled
/// as a presence token, and a credential LogonUI cannot attach to a user tile.
///
/// Layout follows the same rules as <see cref="MainForm"/>: content-sized rows, no stacking of
/// <c>Dock=Fill</c> under <c>Dock=Top</c>/<c>Dock=Bottom</c>, and design pixels through
/// <see cref="Ui.Px"/> (see <see cref="Ui"/> for what happens otherwise).
/// </summary>
sealed class EnrollForm : Form
{
    readonly ListView _devices = new();
    readonly TextBox _user = new();
    readonly TextBox _domain = new();
    readonly TextBox _password = new();
    readonly TextBox _confirm = new();
    readonly CheckBox _showPassword = new();
    readonly CheckBox _restartAgent = new();
    readonly Label _accountHint = new();
    readonly Label _status = new();
    readonly Button _save = new();
    readonly string? _logPath;

    bool _loaded;

    /// <summary>True once the credential and the configuration were both written.</summary>
    public bool Saved { get; private set; }

    public EnrollForm(string? logPath)
    {
        _logPath = logPath;

        Text = "Btwu —— 录入口令";

        // See Ui: hand-written sizes are what this form runs on, so the built-in auto-scale must stay
        // out of the way rather than doubling them a second time.
        AutoScaleMode = AutoScaleMode.None;

        StartPosition = FormStartPosition.CenterScreen;
        // 这两个数字是「设计像素」，乘以缩放之后才是物理像素。860 这个高度在小屏上是拿不到的
        // （1920×1080 @200% 的工作区只有 540 逻辑像素），所以默认值降到 700：内容比窗口高时
        // 上半个区可滚动，按钮固定在最下面 —— 见 root 那段注释。
        MinimumSize = new Size(Ui.Px(560), Ui.Px(380));
        Size = new Size(Ui.Px(840), Ui.Px(700));
        Font = new Font("Microsoft YaHei UI", 9f);

        // ------------------------------------------------------------------ device list
        _devices.View = View.Details;
        _devices.CheckBoxes = true;
        _devices.FullRowSelect = true;
        _devices.GridLines = true;
        _devices.MultiSelect = true;
        _devices.Dock = DockStyle.Fill;
        _devices.Columns.Add("设备", Ui.Px(260));
        _devices.Columns.Add("连接方式", Ui.Px(90));
        _devices.Columns.Add("HID 身份", Ui.Px(90));
        _devices.Columns.Add("此刻", Ui.Px(90));

        var deviceButtons = Ui.ButtonRow(
            Ui.Action("重新枚举", LoadDevices),
            Ui.Action("只勾选键盘", () => Check((_, device) => device.Identity.Kind == HidKind.Keyboard)),
            Ui.Action("全部取消", () => Check((_, _) => false)));

        // 触发设备这一块照主面板的长相搭：Ui.Section（AutoSize + 真实底部间隔）+ Ui.Stack +
        // 「列表行高由代码钉死」。别自己手搭 GroupBox ——2026-10-15 那次「设备列表只剩列头」就是把
        // 手搭的 GroupBox 从「按比例分配」的行里挪出来之后发生的：列表的高度变成由一串首选高度测量
        // 决定，同一份代码在不同尺寸下量出不同结果（压矮时正常、默认尺寸塌掉）。
        var deviceChildren = Ui.Stack();
        Ui.AddFixed(deviceChildren, _devices, 160);
        // 列表自己再兜一层：MinimumSize 会被布局引擎钳住，壳被压小也塌不到看不见。
        _devices.MinimumSize = new Size(0, Ui.Px(120));
        Ui.Add(deviceChildren, deviceButtons);
        var deviceBox = Ui.Section("触发设备", deviceChildren);


        // --------------------------------------------------------------------- account
        var accountGrid = RowGrid(Ui.Px(150));
        AddRow(accountGrid, "登录名", _user, "");
        AddRow(accountGrid, "域", _domain, "");
        _accountHint.AutoSize = true;
        _accountHint.ForeColor = SystemColors.GrayText;
        _accountHint.Margin = Ui.Pad(3, 4, 3, 6);
        _accountHint.Dock = DockStyle.Fill;
        accountGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        accountGrid.RowCount = accountGrid.RowStyles.Count;
        accountGrid.Controls.Add(_accountHint, 1, accountGrid.RowCount - 1);
        var accountBox = Ui.Section("自动解锁使用的账户", accountGrid);

        // -------------------------------------------------------------------- password
        _password.UseSystemPasswordChar = true;
        _confirm.UseSystemPasswordChar = true;
        _showPassword.Text = "显示口令";
        _showPassword.AutoSize = true;
        _showPassword.CheckedChanged += (_, _) =>
        {
            _password.UseSystemPasswordChar = !_showPassword.Checked;
            _confirm.UseSystemPasswordChar = !_showPassword.Checked;
        };

        var passwordGrid = RowGrid(Ui.Px(150));
        AddRow(passwordGrid, "口令", _password, "");
        AddRow(passwordGrid, "确认", _confirm, "");
        passwordGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        passwordGrid.RowCount = passwordGrid.RowStyles.Count;
        passwordGrid.Controls.Add(_showPassword, 1, passwordGrid.RowCount - 1);

        // No explanatory paragraph here, and no tooltip either: this dialog is opened from a button
        // that already says what it does, and the one fact worth knowing ("账户口令，不是 PIN") is in
        // the README and in the package's 使用说明.txt, where it is read once instead of every time.
        var passwordBox = Ui.Section("账户口令", passwordGrid);

        // --------------------------------------------------------------------- buttons
        _restartAgent.Text = "保存后重启后台 agent";
        _restartAgent.AutoSize = true;
        _restartAgent.Checked = true;

        _save.Text = "保存";
        _save.AutoSize = true;
        _save.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _save.Padding = Ui.Pad(16, 4, 16, 4);
        _save.Enabled = false;
        _save.Click += (_, _) => Commit();

        var cancel = new Button
        {
            Text = "取消",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Ui.Pad(16, 4, 16, 4),
            DialogResult = DialogResult.Cancel,
        };

        // 显式关窗，不依赖 DialogResult 的副作用。2026-10-15 的报障就是它：本对话框曾用
        // `Application.Run(form)`（**非模态**）显示，而「非模态表单上 DialogResult 只会记下一个值、
        // 不会关窗」这件事是实测出来的（Click 事件确实收到，`Visible` 仍是 true）。
        // 现在两条打开路径都是 ShowDialog（模态），DialogResult 本来就能关；这一行是**双保险**——
        // 以后谁把对话框改成非模态，都会立刻得到一个仍然能用的「取消」，而不是一个哑按钮。
        cancel.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Margin = Ui.Pad(0, 6, 0, 0),
        };
        cancel.Margin = new Padding(0, 0, 0, Ui.Px(4));
        _save.Margin = new Padding(Ui.Px(8), 0, 0, Ui.Px(4));
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_save);

        _status.AutoSize = true;
        _status.Text = "正在枚举已配对的蓝牙设备…";

        // ------------------------------------------------------------------------ root
        //
        // 结构 = 「上面可滚动 + 下面三个固定行」，与主面板同一个思路。
        //
        // 为什么内容要能滚动（2026-10-15 用户截图里那半截按钮）：本对话框设计高度 860，而
        // FitToScreen 会把窗口压到工作区以内 —— 在 1920×1080、200% 缩放的机器上工作区只有
        // **960×540 逻辑像素**，于是根面板比客户区高一倍多，AutoSize 的几行（状态、勾选框、按钮）
        // 被推到客户区外面：看得见上半截、点不到（只有落在客户区里的那部分能接收鼠标）。
        // 现在只有「设备 / 账户 / 口令」三块进滚动区，状态行与按钮**固定在最下面**，
        // 任何屏幕高度下都够得着。
        var body = Ui.Stack();
        Ui.Add(body, deviceBox);
        Ui.Add(body, accountBox);
        Ui.Add(body, passwordBox);

        var host = new Panel { AutoScroll = true };
        host.Controls.Add(body);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(Ui.Px(12)),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var (control, percent) in new (Control, bool)[]
                 {
                     (host, true),          // 唯一的弹性行：内容比窗口高时它滚动
                     (_status, false),
                     (_restartAgent, false),
                     (buttons, false),
                 })
        {
            control.Dock = DockStyle.Fill;
            root.RowStyles.Add(percent ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            root.Controls.Add(control, 0, root.Controls.Count);
        }
        root.RowCount = root.Controls.Count;

        Controls.Add(root);

        AcceptButton = _save;
        CancelButton = cancel;

        // Preselect whatever is already configured, so an update is a confirmation rather than a
        // re-do. The configured device ids are the only stable handle - names change.
        var configured = AppConfig.Load();

        var detected = AccountIdentity.PreferredLogonForm();
        _user.Text = string.IsNullOrEmpty(configured.UserName) ? detected.User : configured.UserName;
        _domain.Text = string.IsNullOrEmpty(configured.Domain) ? detected.Domain : configured.Domain;
        _accountHint.Text = BuildAccountHint(detected.Domain);

        Shown += (_, _) =>
        {
            Ui.FitToScreen(this);
            LoadDevices();
        };
    }

    static TableLayoutPanel RowGrid(int keyWidth)
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, keyWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    static void AddRow(TableLayoutPanel grid, string key, Control control, string hint)
    {
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowCount = grid.RowStyles.Count;
        var row = grid.RowCount - 1;

        grid.Controls.Add(new Label
        {
            Text = key,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = Ui.Pad(3, 8, 8, 6),
        }, 0, row);

        control.Margin = new Padding(0, Ui.Px(3), 0, Ui.Px(3));
        grid.Controls.Add(Ui.Hint(control, hint), 1, row);
    }

    static string BuildAccountHint(string detectedDomain)
    {
        var signIn = AccountIdentity.MicrosoftAccountSignInAddress();
        if (signIn is null)
        {
            var session = AccountIdentity.SessionAccount();
            // 注册表里有邮箱、而登录身份是本地账户时，用户最自然的误解就是「那我要填那个邮箱」——
            // 那位 2026-10-15 的报障正是这样发生的，所以这句话必须出现在他眼前。
            var suffix = AccountIdentity.StaleIdentityWarning() is null
                ? ""
                : "（注册表里那个微软邮箱只是某个应用的登录记录，不是 Windows 登录身份）";
            return $"本机是本地账户：解锁用 {session.Domain}\\{session.User}（域填 . 或 {session.Domain}）。{suffix}";
        }

        return detectedDomain == AccountIdentity.MicrosoftAccountDomain
            ? $"检测到 Microsoft 账户，解锁名用 MicrosoftAccount\\{signIn}（不是本机同名的那个本地账户）"
            : "检测到 Microsoft 账户，但登记的登录形式看起来是本地账户。";
    }

    // ------------------------------------------------------------------------------ devices

    void Check(Func<ListViewItem, DeviceItem, bool> predicate)
    {
        foreach (ListViewItem item in _devices.Items)
            if (item.Tag is DeviceItem device) item.Checked = predicate(item, device);
    }

    async void LoadDevices()
    {
        _status.Text = "正在枚举已配对的蓝牙设备…";
        _save.Enabled = false;
        _loaded = false;

        // Enumerate on a pool thread. WinRT's await would otherwise need a pumping message loop, and
        // DeviceInformation.FindAllAsync is the slowest call in the tool.
        List<DeviceItem> found;
        try
        {
            found = await Task.Run(() => DeviceInventory.ListAsync());
        }
        catch (Exception ex)
        {
            _status.Text = "枚举失败。";
            Shell.Error(this, "Btwu", $"无法枚举蓝牙设备：{ex.Message}");
            return;
        }

        var wanted = AppConfig.Load().Triggers
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
                Checked = wanted.Contains(device.Id),
            };

            // A visible nudge: a device that cannot pass the gate is not silently equal to one that can.
            _devices.Items.Add(item);
        }
        _devices.EndUpdate();

        _status.Text = _devices.Items.Count == 0
            ? "没有找到已配对的蓝牙设备，请先在 Windows 设置里配对。"
            : $"共 {found.Count} 台已配对设备，勾选触发设备。";

        _save.Enabled = _devices.Items.Count > 0;
        _loaded = true;

        // A lock-screen credential with no trigger device can only ever lock, never unlock.
        if (_devices.Items.Count > 0 && !_devices.CheckedItems.Cast<ListViewItem>().Any())
            Check((_, device) => device.Identity.Kind == HidKind.Keyboard);
    }

    // --------------------------------------------------------------------------------- save

    void Commit()
    {
        if (!_loaded) return;

        var selected = _devices.CheckedItems.Cast<ListViewItem>()
            .Select(item => item.Tag)
            .OfType<DeviceItem>()
            .ToList();

        if (selected.Count == 0)
        {
            Shell.Error(this, "Btwu", "至少要勾选一只触发设备。");
            return;
        }

        var user = _user.Text.Trim();
        var domain = _domain.Text.Trim();

        if (user.Length == 0)
        {
            Shell.Error(this, "Btwu", "登录名不能为空。");
            return;
        }

        // ---- the password, verified before it is stored.
        var password = _password.Text;
        if (password.Length == 0)
        {
            Shell.Error(this, "Btwu", "口令为空。");
            return;
        }
        if (password != _confirm.Text)
        {
            Shell.Error(this, "Btwu", "两次输入的口令不一致。");
            return;
        }

        var logonDomain = LogonProbe.ToLogonDomain(domain);
        var isMsaForm = logonDomain.Equals(AccountIdentity.MicrosoftAccountDomain, StringComparison.OrdinalIgnoreCase);

        // Microsoft 账户的口令至少 8 位。短于 8 位几乎只可能是粘贴没粘全——而"粘不全"的症状与"口令错"
        // 完全一样（锁屏不解锁、没有任何提示），所以要在这里就问一句，别让人带着半个口令往下走。
        if (isMsaForm && password.Length < 8)
        {
            if (!Shell.Confirm(this, "口令看起来没输全",
                    $"Microsoft 账户的口令至少 8 位，而这里只有 {password.Length} 位。\n\n"
                    + "多半是粘贴时被截断了。仍要用这个口令继续吗？"))
                return;
        }

        _status.Text = "正在用 Windows 校验这个口令…";
        Application.DoEvents();

        // A stored-but-wrong password is the worst outcome: the lock screen shows no error at all, it
        // simply never unlocks, and there is no way to tell that apart from any other cause.
        var probe = LogonProbe.ProbeBothForms(user, domain, password);

        // 两种形式各问一次 Windows（见 LogonProbe.ProbeBothForms 的注释）：只有这样才分得清
        // 「口令错」和「账户形式错」。对话框以前只问一种，于是用户报「我输的就是账户密码」时，
        // 它只会说「口令被拒绝」——既没指出形式可能不对，也没法自动改过来。
        if (probe.AlternateWins)
        {
            // 口令是对的，错的只是账户形式。直接采用 Windows 接受的那种，别让用户自己去猜。
            user = probe.AlternateUser!;
            domain = probe.AlternateDomain!;
            logonDomain = LogonProbe.ToLogonDomain(domain);
            Shell.Info(this, "口令没问题，账户形式改了",
                $"这个账户在本机要用 {user} @ {domain} 这种形式，Windows 接受它了。\n\n"
                + "已经改用它继续保存——不用你再做别的。");
        }
        else if (!probe.Primary.Success)
        {
            // 不说「口令被拒绝」。0x52E 分不出「口令错 / 账户名错 / 域错」（见 LogonProbe.ExplainWin32），
            // 而 2026-10-15 那位用户的实际情况正是「账户名被工具的自动检测填错了」——如果对话框只说
            // 「口令不对」，排查方向会整个偏掉。这里把**试过什么**逐条列出来。
            var session = AccountIdentity.SessionAccount();
            var identity = AccountIdentity.InternetIdentity();
                if (!Shell.Confirm(this, "Windows 拒绝了这次校验",
                    $"0x{probe.Primary.Win32Error:X8}  {LogonProbe.ExplainWin32(probe.Primary.Win32Error)}\n\n"
                    + "试过的形式：\n" + LogonProbe.DescribeAttempts(probe, user, logonDomain) + "\n\n"
                    + $"本机登录的账户：{session.Domain}\\{session.User}"
                    + $"（Windows 报告 InternetIdentity={identity.IsInternetIdentity}）\n"
                    + $"口令长度：{password.Length} 个字符\n\n"
                    + "所以这一次最可能不是口令不对，而是**账户名/域**不对。可能是：\n"
                    + "· 账户名不是这个 —— 照上面「本机登录的账户」那一行填；\n"
                    + "· 录成了 PIN（PIN 只在本机解锁用，不是账户口令）；\n"
                    + "· 这是 Microsoft 账户，要录的是它在 account.microsoft.com 上的口令；\n"
                    + "· 口令最近改过，本机还没同步；或本机从未在线登录过这个账户。\n\n"
                    + "仍要保存吗？存下去之后，锁屏时不会解锁，而且没有任何提示。"))
                return;
        }

        // 「Windows 接受了」只证明这是一份**有效凭据**，没证明它是**本机这个人的**凭据：
        // LogonUser 只要账户名与口令对就成功。输成另一个账户会安静地存下去，然后在锁屏上永远失败。
        if (!AccountIdentity.IsSessionAccount(user, domain))
        {
            var sessionNow = AccountIdentity.SessionAccount();
            if (!Shell.Confirm(this, "这个账户不是本机登录的那个",
                    $"Windows 接受了这个口令，但它属于 {user} @ {domain}，"
                    + $"而本机登录的账户是 {sessionNow.Domain}\\{sessionNow.User}。\n\n"
                    + "锁屏要解锁的是**本机登录的那个账户**，所以这份凭据在锁屏上用不上 —— "
                    + "它会「按回车毫无反应，也不报任何错」。\n\n仍要保存吗？"))
                return;
        }

        // ---- the account SID. LogonUI attaches the credential to a user tile through it, and a
        // credential with a SID no one at the lock screen owns took the whole sign-in UI away once.
        var sid = Program.ResolveInteractiveUserSid();
        if (!Program.IsAccountSid(sid))
        {
            if (!Shell.Confirm(this, "账户 SID 不可用",
                    (sid.Length == 0
                        ? "解析不出当前交互用户的账户 SID。"
                        : $"解析出的 SID 不是账户 SID：{sid}")
                    + "\n\n锁屏靠这个 SID 把凭据挂到用户磁贴上；挂不上时登录界面可能整个失去 UI。\n"
                    + "请在要解锁的那个账户、未切换用户的会话里重做。\n\n仍要保存吗？"))
                return;
        }

        try
        {
            CredentialStore.Save(new StoredCredential(user, domain, password, sid));
        }
        catch (Exception ex)
        {
            Shell.Error(this, "Btwu", $"写 {Paths.CredentialFile} 失败：{ex.Message}");
            return;
        }

        var cfg = AppConfig.Load();
        cfg.Triggers = selected.Select(TriggerDevice.From).ToList();
        // 界面不再提供类别闸门：勾上的设备就是钥匙。CLI 仍可用 btwu enroll --require-hid 收紧。
        cfg.RequiredHidKind = "Any";
        cfg.UserName = user;
        cfg.Domain = domain;
        try
        {
            cfg.Save();
        }
        catch (Exception ex)
        {
            Shell.Error(this, "Btwu",
                $"口令已经保存，但配置写不进去：{ex.Message}\n\n"
                + $"请手动检查 {Paths.ConfigFile} 的权限。");
            return;
        }

        Saved = true;

        AppendLog("enroll: 已保存");
        AppendLog($"  触发设备: {string.Join(", ", selected.Select(d => d.Name))}");
        AppendLog("  身份闸门: Any（不检查类别）");
        AppendLog($"  登录名:   {user} @ {domain}（喂给 LSA 的形式是 {user} @ {logonDomain}）");
        AppendLog($"  账户 SID: {sid}");
        AppendLog($"  口令校验: {(probe.Primary.Success
            ? "Windows 接受"
            : probe.AlternateWins
                ? $"Windows 接受（改用了 {probe.AlternateUser} @ {probe.AlternateDomain} 形式）"
                : "Windows 拒绝（用户选择强制保存）")}");

        var note = "已保存。";
        if (_restartAgent.Checked)
        {
            var (_, message) = AgentControl.Restart();
            note = $"{note}\n\n{message}";
            AppendLog($"restart: {message}");
        }
        else
        {
            note = $"{note}\n\n配置改动要等后台 agent 重启后才生效（agent 只在启动时读一次 config.json）。";
        }

        Shell.Info(this, "Btwu", note);
        DialogResult = DialogResult.OK;
        Close();
    }

    void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(_logPath)) return;
        try
        {
            File.AppendAllText(_logPath, line + Environment.NewLine, new UTF8Encoding(false));
        }
        catch
        {
            // A transcript that cannot be written is not a reason to lose the enrollment.
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // The parent window watches for this line to know the elevated dialog is done - it cannot read
        // the child's exit code reliably through ShellExecute, and it must not guess.
        AppendLog($"{GuiEntry.JobDonePrefix}{(Saved ? 0 : 1)} =====");
        AppendLog($"结束时间: {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}");
        base.OnFormClosed(e);
    }
}
