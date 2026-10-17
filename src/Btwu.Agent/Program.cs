using System.Diagnostics;
using System.Text;
using Btwu.Core;

namespace Btwu.Agent;

static class Program
{
    // Must not be an async Main: [STAThread] is silently ignored when the entry point is
    // `async Task<int> Main`, and the runtime then starts the process in MTA. WinForms wants STA, and
    // `tray` is what the logon scheduled task starts. Blocking here is safe - the tray path returns
    // synchronously, and every other command's awaits resume on the thread pool.
    [STAThread]
    static int Main(string[] args) => MainAsync(args).GetAwaiter().GetResult();

    internal static async Task<int> MainAsync(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected console */ }

        // The graphical modes are intercepted before anything else, and before the command table
        // below. `gui` is the management window; `--job` and `--enroll-gui` are how that window
        // re-enters this same executable in a child process - hidden, and elevated when it has to be.
        // Intercepting here rather than inside the switch keeps them out of the command list, which
        // stays the honest description of what the CLI offers.
        if (args.Length > 0 && (args[0] is "gui" or "--gui" or "--job" or "--enroll-gui"))
            return GuiEntry.Run(args);

        // 图形对话框的自检：不需要管理员，需要有人登录着的桌面。见 GuiEntry.SelfTest。
        if (args.Length > 0 && args[0] == "--gui-selftest") return GuiEntry.SelfTest(args);

        if (args.Length == 0) { Usage(); return 1; }

        var command = args[0].ToLowerInvariant();

        // Commands whose stdout is a report should not be mixed with log chatter. `tray` runs with its
        // console hidden, so echoing there is pure waste.
        if (command is "list" or "list-devices" or "status" or "doctor" or "verify" or "tray") Log.Echo = false;

        try
        {
            switch (command)
            {
                case "list" or "list-devices": return await ListAsync(args);
                case "monitor": return await MonitorAsync(args);
                case "enroll": return await EnrollAsync(args);
                case "run": return await RunAsync();
                case "tray": return Tray();
                case "status": return Status();
                case "doctor": return await DoctorAsync();
                case "verify": return Verify(args);
                case "unlock-probe": return UnlockProbe(args);
                case "install-cp": return InstallCredProv(args);
                case "uninstall-cp": return UninstallCredProv();
                case "pause": return Pause(args);
                case "resume": return Resume();
                case "disable-cp": return KillSwitch(true);
                case "enable-cp": return KillSwitch(false);
                case "autosubmit": return SetAutoSubmit(args.Length > 1 && args[1] is "on" or "enable" or "true");
                case "lock": return LockNow();
                case "-h" or "--help" or "help": Usage(); return 0;
                default:
                    Console.Error.WriteLine($"unknown command: {args[0]}");
                    Usage();
                    return 1;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"权限不足: {ex.Message}");
            Console.Error.WriteLine("请用管理员身份打开 PowerShell 后重试。");
            return 2;
        }
        catch (Exception ex)
        {
            Log.Error(ex.ToString());
            Console.Error.WriteLine($"错误: {ex.Message}");
            return 1;
        }
    }

    static void Usage()
    {
        Console.WriteLine("""
            btwu - 蓝牙在场免密解锁（Windows）

            日常使用
              btwu gui                        打开图形管理面板（绿色版里双击快捷方式走的就是这个）
              btwu list                       列出已配对的蓝牙设备，并标注每台的 HID 身份
              btwu monitor [--minutes N] [--all]
                                            观察连接状态跃迁，用来测你那只键盘的休眠断连时长
              btwu enroll [--user NAME] [--domain NAME] [--device INDEX|NAME]...
                          [--require-hid Keyboard|Hid|Any] [--force]
                                            绑定触发设备并录入账户口令（DPAPI 加密保存）
                                            默认只接受 HID 键盘；手机/耳机/手环会被拒绝
              btwu run                        常驻：持续发布在场状态（默认只解锁不锁屏；LockOnAbsence=true 才锁屏）
              btwu tray                       同上，但带托盘图标（开机自启用的就是这个）
              btwu lock                       立即锁屏
            诊断
              btwu status                     当前在场状态与解锁判定
              btwu doctor                     全链路自检（注册、ACL、agent、设备）
              btwu verify [--compare]         把保管的口令喂给 LSA 验证，确认序列化打包与口令都正确
              btwu unlock-probe [--apply] [--log PATH]
                                              复现锁屏按回车那一次提交，逐个试登录名形式与消息类型，
                                              找出 LSA 真正接受的那一个（需要管理员；--apply 直接采用；
                                              --log 让工具自己把输出写成 UTF-8 文件）
              btwu pause --minutes N          暂时停用免密解锁（锁屏不再提供免密磁贴，密码/PIN 照常；
                                              N 分钟后自动恢复）—— 要真正走开时用它
              btwu resume                     恢复免密解锁
              btwu disable-cp                 停用凭据提供程序（应急开关，锁屏立刻恢复原样）
              btwu enable-cp                  重新启用凭据提供程序
              btwu autosubmit on|off          解锁方式：on=全自动（Win+L 直接回桌面），
                                              off=回车模式（默认，锁屏 UI 照常显示，回车即解锁）
            安装（需要管理员）
              btwu install-cp [--dll PATH]    把凭据提供程序装进 System32 并注册
              btwu uninstall-cp               注销并删除凭据提供程序

            数据目录: %ProgramData%\BtwuUnlock
            """);
    }

    static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        Usage();
        return 1;
    }

    // ------------------------------------------------------------------ list

    static async Task<int> ListAsync(string[] args)
    {
        var devices = await DeviceInventory.ListAsync();
        var cfg = AppConfig.Load();
        var showIds = args.Contains("--ids");
        Console.WriteLine($"{devices.Count} 个已配对蓝牙设备:");
        for (var i = 0; i < devices.Count; i++)
        {
            Console.WriteLine(devices[i].ToLine(i));
            Console.WriteLine($"       {devices[i].Identity.Detail()}");
            if (showIds) Console.WriteLine($"       id: {devices[i].Id}");
        }
        Console.WriteLine();
        Console.WriteLine("  * 可作触发设备     ~ 是输入外设但不是键盘     （空）不是输入设备");
        Console.WriteLine($"  当前要求: {HidGate.RequirementText(cfg.RequiredHidKind)}");
        Console.WriteLine("  判定来自 Windows 自己的 PnP 树，按蓝牙地址精确匹配——手机/耳机/手环不会命中。");
        return 0;
    }

    // --------------------------------------------------------------- monitor

    static async Task<int> MonitorAsync(string[] args)
    {
        var minutes = ArgInt(args, "--minutes") ?? 30;
        var all = args.Contains("--all");
        var cfg = AppConfig.Load();

        if (all || cfg.Triggers.Count == 0)
        {
            var devices = await DeviceInventory.ListAsync();
            cfg.Triggers = devices.Select(TriggerDevice.From).ToList();
            Console.WriteLine($"(观察全部 {cfg.Triggers.Count} 个已配对设备)");
        }
        Console.WriteLine($"触发判定要求: {HidGate.RequirementText(cfg.RequiredHidKind)}");
        Console.WriteLine("（所有设备的状态跃迁都会打印；只有通过身份闸门的设备才会改变在场判定）");
        Console.WriteLine("  设备断开/连上的那一行是**发布给锁屏的严格事实**：断开就意味着免密解锁立刻失效，");
        Console.WriteLine($"  不等宽限期。下面的『在场判定』则要等 GraceSeconds（当前 {cfg.GraceSeconds}s）去抖，");
        Console.WriteLine("  它只决定**何时锁屏**，不会让锁屏继续放行。");
        Console.WriteLine();

        var monitor = new PresenceMonitor(cfg);
        monitor.PresenceChanged += present => Console.WriteLine($"  >>> 在场判定（仅用于锁屏）: {(present ? "PRESENT" : "ABSENT")}");

        var started = DateTime.Now;
        var deadline = started.AddMinutes(minutes);
        var stats = new Dictionary<string, (int Disconnects, TimeSpan Disconnected, DateTime? Since)>();
        monitor.DeviceEvent += e =>
        {
            var name = e[..e.IndexOf(" ->", StringComparison.Ordinal)];
            var connected = e.EndsWith("connected", StringComparison.Ordinal);
            if (!stats.TryGetValue(name, out var s)) s = (0, TimeSpan.Zero, null);
            if (connected && s.Since is { } since)
            {
                s.Disconnected += DateTime.Now - since;
                s.Since = null;
            }
            else if (!connected)
            {
                s = (s.Disconnects + 1, s.Disconnected, DateTime.Now);
            }
            stats[name] = s;
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        await monitor.StartAsync();

        Console.WriteLine($"观察 {minutes} 分钟，到 {deadline:HH:mm:ss}（Ctrl+C 提前结束）");
        foreach (var line in monitor.DeviceSummaries()) Console.WriteLine($"  {line}");
        Console.WriteLine();
        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }

        await monitor.DisposeAsync();

        Console.WriteLine();
        Console.WriteLine($"--- 汇总（共观察 {DateTime.Now - started:hh\\:mm\\:ss}）---");
        foreach (var (name, s) in stats.OrderByDescending(kv => kv.Value.Disconnects))
        {
            var offline = s.Disconnected + (s.Since is { } since ? DateTime.Now - since : TimeSpan.Zero);
            Console.WriteLine($"  {name}: 断连 {s.Disconnects} 次，累计离线 {offline:hh\\:mm\\:ss}" +
                              (s.Disconnects > 0 ? $"，平均 {TimeSpan.FromTicks(offline.Ticks / s.Disconnects):hh\\:mm\\:ss}" : ""));
        }
        if (stats.Count == 0)
            Console.WriteLine("  没有观察到任何状态跃迁——设备一直在线，或者根本没连上过");
        else
            Console.WriteLine($"\n提示：把最长的一次离线时长再留一点余量，写进 config.json 的 GraceSeconds（当前 {cfg.GraceSeconds}s）。");
        return 0;
    }

    // ---------------------------------------------------------------- enroll

    static async Task<int> EnrollAsync(string[] args)
    {
        var cfg = AppConfig.Load();
        var devices = await DeviceInventory.ListAsync();
        if (devices.Count == 0) { Console.Error.WriteLine("没有找到已配对的蓝牙设备"); return 1; }

        var wanted = ArgList(args, "--device");
        var selected = new List<DeviceItem>();
        foreach (var w in wanted)
        {
            var match = await DeviceInventory.ResolveAsync(w);
            if (match is null) { Console.Error.WriteLine($"没有已配对设备匹配 '{w}'"); return 1; }
            selected.Add(match);
        }

        if (selected.Count == 0)
        {
            Console.WriteLine($"{devices.Count} 个已配对蓝牙设备:");
            for (var i = 0; i < devices.Count; i++) Console.WriteLine(devices[i].ToLine(i));
            Console.Write("把哪些设备绑成触发设备？(逗号分隔序号，例如 0,3): ");
            foreach (var part in (Console.ReadLine() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, out var index) && index >= 0 && index < devices.Count) selected.Add(devices[index]);
                else Console.Error.WriteLine($"忽略 '{part}'");
            }
        }

        if (selected.Count == 0) { Console.Error.WriteLine("没有选择任何触发设备"); return 1; }

        // ------------------------------------------------- HID 身份闸门
        // A phone, headset or band pairs with the machine just like a keyboard does, and a presence
        // check that only looks at "is it connected" cannot tell them apart. The gate below asks
        // Windows what the device actually is, keyed on its Bluetooth address.
        var required = NormalizeHidRequirement(Arg(args, "--require-hid") ?? cfg.RequiredHidKind);
        if (required.Length == 0)
        {
            Console.Error.WriteLine("--require-hid 只能是 Keyboard / Hid / Any");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"触发设备身份检查（要求：{HidGate.RequirementText(required)}）");
        Console.WriteLine("判定依据是 Windows 的 PnP 设备树，不是设备名字，也不是信号强度。");
        var rejected = new List<DeviceItem>();
        foreach (var device in selected)
        {
            var ok = HidGate.Satisfies(required, device.Identity);
            if (!ok) rejected.Add(device);
            Console.WriteLine($"  {(ok ? "✓" : "✗")} {device.Name}");
            Console.WriteLine($"       {device.Identity.Detail()}");
        }

        if (rejected.Count > 0)
        {
            Console.Error.WriteLine();
            foreach (var device in rejected)
                Console.Error.WriteLine($"✗ {device.Name} 不符合要求：{device.Identity.Detail()}");
            if (!args.Contains("--force"))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("这是为了保护你：这类设备（手机、耳机、手环、音箱、另一台电脑）不能");
                Console.Error.WriteLine("证明你人在机器旁边，用它们当触发等于把解锁交给一个放在桌上的东西。");
                Console.Error.WriteLine("换一只蓝牙键盘，或者 --require-hid Any 显式关掉检查。");
                return 3;
            }
            Console.Error.WriteLine("（已指定 --force，继续绑定）");
        }

        // Which FORM of the account gets replayed at the lock screen. A Microsoft account is logged
        // on through MicrosoftAccount\<sign-in address>, NOT through the local SAM name: the local
        // entry still exists under the same friendly name, but Windows owns its password, so replaying
        // the Microsoft account password against it fails. MSV1_0 reports that failure identically to
        // "no such account", so a wrong form is invisible from the outside. See AccountIdentity.
        var detected = AccountIdentity.PreferredLogonForm();
        var explicitUser = Arg(args, "--user");
        var explicitDomain = Arg(args, "--domain");
        var user = explicitUser ?? detected.User;
        var domain = explicitDomain ?? detected.Domain;

        Console.WriteLine();
        Console.WriteLine($"自动解锁使用的账户: {AccountIdentity.Describe(user, domain)}");
        if (detected.Domain == AccountIdentity.MicrosoftAccountDomain
            && explicitUser is null && explicitDomain is null)
        {
            var identity = AccountIdentity.InternetIdentity();
            Console.WriteLine($"  这是 Windows 自己说的：NetUserGetInfo(24) 的 InternetIdentity="
                              + $"{identity.IsInternetIdentity}，provider='{identity.ProviderName}'。");
            Console.WriteLine($"  登录地址取自 Windows 登记的身份（{AccountIdentity.IdentityRegistryKey}）。");
            Console.WriteLine("  必须用 MicrosoftAccount\\<登录地址> 这种形式：本机也有一个同名的本地账户，");
            Console.WriteLine("  但那个账户的口令由 Windows 自己管（net user 会显示「需要密码: No」），");
            Console.WriteLine("  把 Microsoft 账户口令喂给它只会得到 STATUS_LOGON_FAILURE，而且和「没有这个账户」");
            Console.WriteLine("  报的是同一个错误，从外面完全分不出来。");
            // 这里原来写的是作者本机的账户地址与用户名当例子。它会被编译进二进制，随包发给所有人——
            // 一句「解释」把作者的账号泄漏给了每个下载者。例子用运行时的变量就够，既准确又不带个人信息。
            Console.WriteLine($"  两者是不同的主体：同一台机器上 MicrosoftAccount\\{user} 与本机同名的本地账户"
                              + "解析出的 SID 并不相同。");
        }

        // 注册表里有微软邮箱、而本机账户其实是本地账户 —— 这是 2026-10-15 那位用户录错形式的全部原因。
        // 默认形式已经不会被它带偏了（AccountIdentity 会先去问 Windows），但用户手里可能还留着那份
        // 用 --user/--domain 写死的旧配置，所以这里仍然要说一句。
        var stale = AccountIdentity.StaleIdentityWarning();
        if (stale is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"！注意: {stale}");
            Console.WriteLine("  （本工具的默认形式不会用它；但如果你手工传了 --domain MicrosoftAccount，就是在用它。）");
        }
        Console.WriteLine("口令会用 DPAPI（machine scope）加密后存盘，凭据提供程序在锁屏界面复用它。");
        Console.WriteLine("这意味着：本机管理员可以解密它。做不到既全自动又不存口令。");
        var password = ReadPassword($"请输入 {user} 的账户口令（不是 PIN）: ");
        if (password.Length == 0) { Console.Error.WriteLine("口令为空——中止"); return 1; }
        if (ReadPassword("再输一次确认: ") != password) { Console.Error.WriteLine("两次输入不一致——中止"); return 1; }

        // 存之前先验一次。存错口令的代价特别高：锁屏界面不会报错，只是不解锁，从外面完全看不出来
        // 是口令不对还是别的地方不对；而 LogonUser 一次调用就能问清楚。
        // 这一步会消耗一次登录尝试（口令错时计入失败计数），换来的是不用带着错口令装完再去猜。
        var probeDomain = LogonProbe.ToLogonDomain(domain);

        // 两种形式各问一次：ERROR_LOGON_FAILURE 分不出「口令错」和「账户形式错」（实测：不存在的用户名
        // 也返回同一个 0x52E），只有「A 被拒 / B 被接受」这个组合才是结论。见 LogonProbe.ProbeBothForms。
        var probe = LogonProbe.ProbeBothForms(user, domain, password);
        var check = probe.Primary;

        // 另一种形式被接受 = 口令没问题，错的只是账户形式。采用它，而不是在下面报「也一样」然后中止——
        // 那正是用户遇到的情况（他说「我输的就是账户密码」，而他确实没输错）。
        if (probe.AlternateWins)
        {
            Console.WriteLine();
            Console.WriteLine($"! {user} @ {probeDomain} 被 Windows 拒绝，但 "
                              + $"{probe.AlternateUser} @ {probe.AlternateDomain} 通过了 —— "
                              + "口令没问题，错的是账户形式，已改用它。");
            user = probe.AlternateUser!;
            domain = probe.AlternateDomain!;
            probeDomain = LogonProbe.ToLogonDomain(domain);
        }
        else if (!check.Success && !args.Contains("--force"))
        {
            // 说「账户名、域、口令三者至少有一个不对」，不说「口令错了」：0x52E 分不出这三者，
            // 而从 2026-10-15 那次报障看，最可能错的恰恰不是口令（是账户形式/名字）。
            Console.Error.WriteLine();
            Console.Error.WriteLine($"✗ Windows 拒绝了这次校验：0x{check.Win32Error:X8}  {LogonProbe.ExplainWin32(check.Win32Error)}");
            Console.Error.WriteLine("  试过的形式:");
            foreach (var line in LogonProbe.DescribeAttempts(probe, user, probeDomain).Split('\n'))
                Console.Error.WriteLine("  " + line);
            Console.Error.WriteLine($"  本机登录的账户: {AccountIdentity.Describe(detected.User, detected.Domain)}"
                                    + $"（Windows 报告 InternetIdentity={AccountIdentity.InternetIdentity().IsInternetIdentity}）");
            Console.Error.WriteLine();
            Console.Error.WriteLine("没有保存，什么都没改。可能是：");
            Console.Error.WriteLine("  · 账户名/域不对 —— 上面「本机登录的账户」那一行才是锁屏时要解锁的主体；");
            Console.Error.WriteLine("  · 录成了 PIN —— PIN 只在本机解锁用，不是账户口令；");
            Console.Error.WriteLine("  · 账户是 Microsoft 账户（邮箱/手机号注册的那种），要录的是它在");
            Console.Error.WriteLine("    account.microsoft.com 上使用的口令；");
            Console.Error.WriteLine("  · 机器离线且从没在线登录过这个账户，本地没有可校验的口令副本；");
            Console.Error.WriteLine("  · 改过 Windows 口令之后没同步过来。");
            Console.Error.WriteLine();
            Console.Error.WriteLine("重跑 btwu enroll 再输一次（**不要**手工传 --user/--domain，让工具去问 Windows）。");
            Console.Error.WriteLine("确有把握（例如账户锁定、域控不可达）可以加 --force 跳过一次校验。");
            return 4;
        }

        if (!check.Success)
        {
            Console.WriteLine();
            Console.WriteLine($"！口令被 Windows 拒绝（0x{check.Win32Error:X8}），但指定了 --force，照常保存。");
            Console.WriteLine("  装好之后 btwu verify 还会再问一次。");
        }

        // 「Windows 接受了」只证明这是一份**有效凭据**，没证明它是**本机这个人的**凭据 ——
        // LogonUser 只要账户名与口令对就成功，而锁屏要解锁的是会话的那个主体。
        // 输成另一个账户（例如顺手输了自己在别的账户上的口令）会安静地存下去，然后在锁屏上永远失败。
        if (check.Success && !AccountIdentity.IsSessionAccount(user, domain))
        {
            var sessionAccount = AccountIdentity.SessionAccount();
            Console.Error.WriteLine();
            Console.Error.WriteLine($"！Windows 接受了这个口令，但它属于 {user} @ {domain}，"
                                    + $"而本机登录的账户是 {sessionAccount.Domain}\\{sessionAccount.User}。");
            Console.Error.WriteLine("  锁屏要解锁的是**本机登录的那个账户**，所以这份凭据在锁屏上用不上"
                                    + "（按回车不会有反应，也不报任何错）。");
            if (!args.Contains("--force"))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("没有保存，什么都没改。重跑 btwu enroll，**不要**手工传 --user/--domain。");
                return 5;
            }
            Console.Error.WriteLine("  --force：照常保存。");
        }

        // Two different things are stored, and they must not be confused - see
        // ResolveInteractiveUserSid. `user`/`domain` is the logon name replayed to LSA;
        // the SID is the local account LogonUI attaches the credential to.
        //
        // The SID is not decoration. It is what ICredentialProviderCredential2::GetUserSid returns,
        // and LogonUI uses it to decide which user tile the credential belongs to. A credential
        // carrying a SID that no user at this lock screen owns is not merely useless: on 2026-10-14
        // it took the entire sign-in UI away - no user tile, no password box, no PIN, only Windows
        // Hello still worked. So refuse to write an unusable one, for the same reason the credential
        // provider refuses to offer it, and make the refusal loud here rather than silent later.
        var sid = ResolveInteractiveUserSid();
        var sidUsable = IsAccountSid(sid);
        if (!sidUsable && !args.Contains("--force"))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(sid.Length == 0
                ? "✗ 解析不出当前交互用户的账户 SID —— 没有保存，什么都没改。"
                : $"✗ 解析出的 SID 不是账户 SID：{sid} —— 没有保存，什么都没改。");
            Console.Error.WriteLine("  锁屏界面靠这个 SID 把凭据挂到用户磁贴上；挂不上时不只是「不解锁」，");
            Console.Error.WriteLine("  而是整个登录界面可能失去 UI，只能用 Windows Hello 进去。");
            Console.Error.WriteLine("  请在**要解锁的那个账户**、未切换用户的交互会话里重跑 btwu enroll。");
            Console.Error.WriteLine("  （管理员提权仍是同一个用户的会话上下文，所以正常提权执行不会触发这一条。）");
            Console.Error.WriteLine("  确有把握可以加 --force 强制保存，但请先用 btwu doctor 复核。");
            return 3;
        }
        if (!sidUsable)
            Console.WriteLine($"！SID 不可用（{(sid.Length == 0 ? "空" : sid)}），但指定了 --force，照常保存。");

        try
        {
            CredentialStore.Save(new StoredCredential(user, domain, password, sid));
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"{Paths.CredentialFile} 写不进去——安装脚本已经把它的 ACL 收紧到 SYSTEM + Administrators。");
            Console.Error.WriteLine("请用管理员身份运行 scripts\\enroll.ps1，或者在管理员 PowerShell 里重跑 btwu enroll。");
            return 2;
        }

        cfg.Triggers = selected.Select(TriggerDevice.From).ToList();
        cfg.RequiredHidKind = required;
        cfg.UserName = user;
        cfg.Domain = domain;
        cfg.Save();

        Console.WriteLine();
        Console.WriteLine("已保存。触发设备:");
        foreach (var d in selected) Console.WriteLine($"  - {d.Name} ({d.Kind}, {d.Identity.KindText})");
        Console.WriteLine($"身份闸门: {HidGate.RequirementText(required)}");
        Console.WriteLine($"登录名:   {user} @ {domain}   （喂给 LSA 的账户形式）");
        Console.WriteLine($"账户 SID: {sid}   （锁屏用它把凭据挂到用户磁贴上）");
        Console.WriteLine($"配置:   {Paths.ConfigFile}");
        Console.WriteLine($"凭据:   {Paths.CredentialFile}");
        Console.WriteLine();
        Console.WriteLine("下一步：以管理员身份运行 scripts\\install.ps1（收紧凭据 ACL、注册凭据提供程序、");
        Console.WriteLine("        设置开机自启），装完再跑 `btwu verify` 确认口令与序列化都没问题。");
        return 0;
    }

    // ------------------------------------------------------------------- run

    static async Task<int> RunAsync()
    {
        // 常驻模式：先把控制台交还出去（见 ConsoleWindow 的说明）。
        // 必须在任何输出之前做——否则窗口会先出现在桌面上闪一下；之后也不再往控制台写，
        // 包括下面那两条失败信息：它们改走 Log（写文件），因为已经没有控制台可给人看了。
        ConsoleWindow.DetachForResidentMode();

        var cfg = AppConfig.Load();
        if (cfg.Triggers.Count == 0)
        {
            Log.Error("run: 还没有配置触发设备——先跑 `btwu enroll`。btwu run 退出。");
            return 1;
        }
        if (!CredentialStore.Exists)
            Log.Warn("没有保管口令：只会做「离开即锁屏」，自动解锁不会生效");

        await using var agent = new PresenceAgent(cfg);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        await agent.StartAsync();
        Log.Info("run: agent 已启动（无界面常驻）");

        try { await Task.Delay(Timeout.Infinite, cts.Token); }
        catch (OperationCanceledException) { }
        return 0;
    }

    static int Tray()
    {
        // 托盘从不往控制台写东西（Log.Echo 早就关了），所以它的控制台纯粹是个负担：
        // 一个必须一直开着、而且一关进程就死的窗口。**无条件**交还出去，包括从终端启动的情况——
        // 这正是用户遇到的那个形态（agent 是在 Windows Terminal 里手动跑起来的）：
        // 那种情况下控制台是和 shell 共享的，「只在独占时脱离」的规则不会生效，窗口照样绑着进程。
        if (!ConsoleWindow.OwnsConsole)
        {
            Console.WriteLine("btwu tray：正在脱离这个控制台——之后关闭本窗口不会结束后台 agent。");
            Console.WriteLine("           想让它完全不占窗口：登录时注册的计划任务会自动拉起它，");
            Console.WriteLine("           也可以在管理面板里点一次「保存」（它会顺手重启后台 agent）。");
        }
        ConsoleWindow.DetachForResidentMode(force: true);

        var cfg = AppConfig.Load();
        if (cfg.Triggers.Count == 0)
        {
            Log.Error("tray: 还没有配置触发设备——先跑 `btwu enroll`。btwu tray 退出。");
            return 1;
        }
        return TrayApp.Run(cfg);
    }

    // ---------------------------------------------------------------- status

    static int Status()
    {
        var cfg = AppConfig.Load();
        Console.WriteLine($"配置: {Paths.ConfigFile}");
        Console.WriteLine($"  触发设备:      {string.Join(", ", cfg.Triggers.Select(t => t.Name))}");
        Console.WriteLine($"  账户:          {cfg.Domain}\\{cfg.UserName}");
        // 这个组合在结构上就不可能通过，而它的症状是「锁屏什么都不说、只是不解锁」，所以先说。
        if (AccountIdentity.IsImpossibleMsaForm(cfg.UserName, cfg.Domain))
            Console.WriteLine("  ⚠ 这个形式不可能通过：本机账户不是 internet identity（见 btwu doctor）"
                + " —— 锁屏会一直解锁失败且没有任何提示，重跑 enroll 即可");
        // grace 只在 lockOnAbsence=true 时才有意义，所以不在一起打印——否则读的人会以为 45s 在起作用。
        Console.WriteLine($"  lockOnAbsence: {cfg.LockOnAbsence}"
            + (cfg.LockOnAbsence ? $"（设备离开超过 grace {cfg.GraceSeconds}s 就锁屏）" : "（关：只解锁不锁屏，锁屏交给 Windows）")
            + $"   autoUnlock: {cfg.AutoUnlock}");
        Console.WriteLine($"  unlockWindow:  {(cfg.UnlockWindowSeconds == 0 ? "0（只要在场就解锁）" : cfg.UnlockWindowSeconds + "s")}");

        var credential = CredentialStore.TryLoad();
        Console.WriteLine($"  保管的口令:    {(credential is null ? "未保存" : $"已保存（{credential.UserName}，{credential.Password.Length} 字符）")}");
        Console.WriteLine($"  凭据提供程序:  {(Registration.IsRegistered ? "已注册" : "未注册")}");

        if (!PresenceChannel.TryRead(out var snapshot))
        {
            Console.WriteLine();
            Console.WriteLine("在场状态: 没有状态文件——agent 没在运行");
            return 0;
        }

        var age = DateTime.UtcNow - new DateTime(snapshot.HeartbeatTicks, DateTimeKind.Utc);
        Console.WriteLine();
        // This value is the strict one - "is the trigger device connected right now" - because that is
        // the bit the credential provider gates an unlock on. The agent's GraceSeconds debounce is a
        // lock-side device and never reaches this line.
        Console.WriteLine($"触发设备: {(snapshot.Present ? "已连接（在场）" : "未连接（不在场）")}，状态文件已写入 {age.TotalSeconds:F1}s 前");
        if (snapshot.Present)
            Console.WriteLine($"  已连接起始:  {new DateTime(snapshot.SinceTicks, DateTimeKind.Utc).ToLocalTime():HH:mm:ss}");
        Console.WriteLine($"  状态文件:    {Paths.PresenceFile}");

        var (unlock, reason) = PresenceChannel.Evaluate(snapshot, DateTime.UtcNow);
        Console.WriteLine($"解锁判定: {(unlock ? "会立即自动解锁" : "不会解锁")} —— {reason}");

        // The lock side has its own reason to stay quiet for a while after an unlock, and it is the
        // one people notice ("I just unlocked, why did it lock itself?"), so it belongs here too.
        var cooldownLeft = PresenceChannel.ReadUnlockCooldownUntil() - DateTime.UtcNow.Ticks;
        Console.WriteLine(cooldownLeft > 0
            ? $"锁屏判定: 暂不锁屏 —— 刚解锁过，还有 {cooldownLeft / TimeSpan.TicksPerSecond}s 的静默期" +
              "（键盘休眠断连不会被当成离开）"
            : cfg.LockOnAbsence
                ? "锁屏判定: 正常 —— 设备离开超过宽限期就会锁屏"
                : "锁屏判定: 本工具不会主动锁屏（lockOnAbsence=false）—— 合盖/超时/Win+L 由 Windows 决定");
        return 0;
    }

    // ---------------------------------------------------------------- doctor

    static async Task<int> DoctorAsync()
    {
        var problems = 0;
        Console.WriteLine("btwu 自检");
        Console.WriteLine();

        var cfg = AppConfig.Load();
        var credential = CredentialStore.TryLoad();

        // 说清楚哪些 ✗ 只是「权限不够，看不到」，否则非管理员跑出来会看到两个假故障，
        // 然后去重跑 enroll —— 而 enroll 又会因为写不进 cred.bin 而失败，白折腾一圈。
        var elevated = Registration.IsElevated;
        if (!elevated)
        {
            Console.WriteLine("注：当前进程不是管理员，[2] 和 [3] 读不到 cred.bin（它的 ACL 只允许");
            Console.WriteLine("    SYSTEM 和 Administrators）。下面标 ? 的是「没法判断」，不是「不合格」。");
            Console.WriteLine("    要看完整结果：以管理员身份重跑 btwu doctor。");
            Console.WriteLine();
        }

        // Which logon-name form this machine's account needs. It is the one piece of configuration
        // that fails completely silently when it is wrong - the lock screen simply never unlocks and
        // reports nothing - so it is worth printing unconditionally, before any of the checks below.
        //
        // 打印的依据必须是 Windows 自己的答案（NetUserGetInfo(24)），不是注册表里那个邮箱：
        // 2026-10-15 那位用户的注册表里有别人的邮箱、登录身份却是本地账户，旧版本就据此选错了形式。
        var signInAddress = AccountIdentity.MicrosoftAccountSignInAddress();
        var windowsIdentity = AccountIdentity.InternetIdentity();
        var session = AccountIdentity.SessionAccount();
        Console.WriteLine(signInAddress is null
            ? $"账户类型: 本地账户 {session.Domain}\\{session.User} —— 解锁用 . 或 {session.Domain} 作域"
            : $"账户类型: Microsoft 账户 {signInAddress} —— 解锁用 MicrosoftAccount\\{signInAddress} 形式");
        Console.WriteLine($"  Windows 的判据: NetUserGetInfo(24) InternetIdentity="
            + (windowsIdentity.IsInternetIdentity switch { true => "True", false => "False", _ => "查不出来" })
            + (string.IsNullOrEmpty(windowsIdentity.ProviderName) ? "" : $"，provider='{windowsIdentity.ProviderName}'")
            + $"；交互会话用户 = {session.Domain}\\{session.User}");

        var staleIdentity = AccountIdentity.StaleIdentityWarning();
        if (staleIdentity is not null) Console.WriteLine($"  ！{staleIdentity}");

        // 存下来的形式如果从结构上就不可能通过（MSA 形式 + 本机其实是本地账户），锁屏会「什么都不说」
        // 地永远不解锁 —— 这正是那位用户报的故障。让它变成一个有名有姓的 problem，而不是让人去猜。
        if (AccountIdentity.IsImpossibleMsaForm(cfg.UserName, cfg.Domain))
        {
            problems++;
            Console.WriteLine($"  ✗ 配置里存的是 MicrosoftAccount\\{cfg.UserName}，但本机账户不是 internet identity"
                + " —— 锁屏会永远解锁失败，而且不给任何提示。");
            Console.WriteLine($"    修法: 重跑 {Environment.ProcessPath} enroll（**不要**传 --user/--domain）。");
        }

        // The unlock mode is the second thing that changes what the lock screen looks like, so it is
        // printed next to the account form rather than buried in the details below.
        Console.WriteLine(File.Exists(Paths.AutoSubmitFile)
            ? $"解锁方式: 全自动 —— {Paths.AutoSubmitFile} 存在；凭据一被枚举就提交，Win+L 会直接回桌面"
            : "解锁方式: 回车模式（默认）—— 锁屏显示原本的 UI，凭据是默认磁贴，按回车即免密解锁"
              + $"（想全自动: btwu autosubmit on）");

        var dwellLeft = PresenceChannel.ReadUnlockCooldownUntil() - DateTime.UtcNow.Ticks;
        if (dwellLeft > 0)
            Console.WriteLine($"解锁静默期: 还有 {dwellLeft / TimeSpan.TicksPerSecond}s —— 这期间设备离开不会锁屏"
                + "（键盘刚解锁完就休眠是被预期的情况）");

        // Two conditions make the credential provider invisible no matter what else is configured, and
        // both are things a person turns on deliberately while troubleshooting. Printing them first
        // stops "why is nothing unlocking" from being chased through the rest of the report.
        if (File.Exists(Paths.DisableCpFile))
            Console.WriteLine($"总开关:   ✗ 已停用 —— {Paths.DisableCpFile} 存在，凭据提供程序在所有场景都返回 0 个凭据");
        // 看的是文件里那个**截止时刻**，不是文件在不在：`btwu resume` 是把值写成 0（不删文件），
        // 所以「存在即暂停」会把已经恢复的状态报成暂停中。
        var suppressUntil = PresenceChannel.ReadSuppressUntil();
        if (suppressUntil > DateTime.UtcNow.Ticks)
            Console.WriteLine($"免密开关: · 暂时停用（到 "
                + $"{new DateTime(suppressUntil, DateTimeKind.Utc).ToLocalTime():HH:mm:ss}）—— "
                + $"锁屏不再提供免密磁贴，用密码/PIN 照常登录；btwu resume 立刻恢复");
        Console.WriteLine();

        // 1. 凭据提供程序
        Console.WriteLine("[1] 凭据提供程序注册");
        if (!Registration.IsRegistered)
        {
            problems++;
            Console.WriteLine("    ✗ 未注册 —— 运行 scripts\\install.ps1");
        }
        else
        {
            Console.WriteLine($"    ✓ CLSID {Registration.ClsidText} 已注册");
            var dllPath = Registration.RegisteredDllPath;
            Console.WriteLine($"      InprocServer32: {dllPath}");
            if (!string.Equals(dllPath, Registration.SystemDllPath, StringComparison.OrdinalIgnoreCase))
            {
                problems++;
                Console.WriteLine($"      ✗ 指向的不是 {Registration.SystemDllPath}；LogonUI 只应加载 System32 里的那份");
            }
            else if (!File.Exists(dllPath))
            {
                problems++;
                Console.WriteLine("      ✗ 文件不存在 —— 重跑 scripts\\install.ps1");
            }
            else
            {
                Console.WriteLine("      ✓ 文件存在");
            }
            if (Registration.RegisteredThreadingModel != "Apartment")
            {
                problems++;
                Console.WriteLine($"      ✗ ThreadingModel 应为 Apartment，实际为 '{Registration.RegisteredThreadingModel}'");
            }
        }
        Console.WriteLine();

        // 2. 保管道具
        Console.WriteLine("[2] 保管的口令 / 触发设备");
        if (credential is null && CredentialStore.ExistsButUnreadable())
        {
            Console.WriteLine("    ? cred.bin 存在，但这个权限读不到它 —— 不是「没录过」。");
            Console.WriteLine("      用管理员身份重跑 btwu doctor 才能检查这一项。");
        }
        else if (credential is null)
        {
            problems++;
            Console.WriteLine("    ✗ cred.bin 不存在或解不开 —— 运行 btwu enroll");
        }
        else
        {
            Console.WriteLine($"    ✓ 口令可解密：账户 {credential.UserName}@{credential.DomainOrLocalMachine}，" +
                              $"{credential.Password.Length} 字符");
            // 能解开只说明 DPAPI 往返没问题，不说明这个口令就是账户当前的口令 ——
            // 那件事只有 LogonUser 能回答，见 btwu verify。
            Console.WriteLine("      （只证明 DPAPI 往返正常；口令是否等于账户当前口令要用 btwu verify 查）");

            // The name form decides which principal the password is offered to, and getting it wrong
            // fails identically to a wrong password - so check it on every run rather than leaving it
            // to be discovered at the lock screen, where nothing is reported at all.
            if (signInAddress is null)
            {
                Console.WriteLine($"      · 账户形式: {credential.DomainOrLocalMachine}\\{credential.UserName}（本机登录的是本地账户）");
            }
            else if (credential.Domain.Equals(AccountIdentity.MicrosoftAccountDomain, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"      ✓ 账户形式 MicrosoftAccount\\{credential.UserName} 与本机的 Microsoft 账户身份一致");
            }
            else
            {
                problems++;
                Console.WriteLine($"      ✗ 账户形式不对：本机登录的是 Microsoft 账户 {signInAddress}，");
                Console.WriteLine($"        存的是本地账户形式 {credential.UserName}@{credential.DomainOrLocalMachine}。");
                Console.WriteLine("        两者在本机解析出的 SID 不同，是两个主体，口令不会被接受。");
                Console.WriteLine("        修法：不带 --user/--domain 重跑 btwu enroll（管理员身份）。");
            }

            // The SID is a *different* thing from the name form, and a wrong one breaks the lock screen
            // outright rather than merely failing to unlock: LogonUI cannot attach the credential to a
            // user. The credential provider refuses to hand over a non-account SID for exactly this
            // reason, so a stale credential from before that check existed shows up here.
            var storedSid = credential.Sid;
            var interactiveSid = ResolveInteractiveUserSid();
            if (storedSid.Length == 0)
            {
                Console.WriteLine("      ? cred.bin 里没有 SID —— 锁屏上不会出现对应的用户磁贴");
                Console.WriteLine("        （普通锁屏解锁仍可用，但合并场景下会缺一个磁贴）。重跑 enroll 补上。");
            }
            else if (!IsAccountSid(storedSid))
            {
                problems++;
                Console.WriteLine($"      ✗ 账户 SID 不是本地/AD 账户的 SID：{storedSid}");
                Console.WriteLine("        这是「身份」SID（Microsoft 账户的登录地址会解析成 S-1-11-96-…），");
                Console.WriteLine("        不是 LogonUI 能挂凭据的账户 SID。它会让锁屏界面直接没有可用 UI");
                Console.WriteLine("        （没有用户磁贴、没有密码框、没有 PIN，只有 Windows Hello 还能用）。");
                Console.WriteLine("        修法：以管理员身份、不带 --user/--domain 重跑 btwu enroll。");
            }
            else if (!string.Equals(storedSid, interactiveSid, StringComparison.OrdinalIgnoreCase))
            {
                // The SID being an account SID is not enough: the provider matches it against the SID of
                // the user sitting at *this* lock screen and stays invisible when they differ. That is
                // the check that replaced "trust whatever enroll wrote".
                problems++;
                Console.WriteLine($"      ✗ 账户 SID {storedSid}");
                Console.WriteLine($"        与当前交互用户的 SID {interactiveSid} 不一致。");
                Console.WriteLine("        凭据提供程序只在两者相同时才提供凭据，所以现在锁屏不会免密解锁。");
                Console.WriteLine("        修法：以这个用户的身份、管理员权限重跑 btwu enroll。");
            }
            else
            {
                Console.WriteLine($"      ✓ 账户 SID {storedSid}");
                Console.WriteLine("        与当前交互用户一致 —— 锁屏可以把凭据挂到你的用户磁贴上。");
            }
        }
        if (cfg.Triggers.Count == 0)
        {
            problems++;
            Console.WriteLine("    ✗ 没有配置触发设备 —— 运行 btwu enroll");
        }
        else
        {
            Console.WriteLine($"    ✓ 触发设备: {string.Join(", ", cfg.Triggers.Select(t => t.Name))}");
        }
        if (!cfg.AutoUnlock)
        {
            problems++;
            Console.WriteLine("    ✗ config.json 里 AutoUnlock=false，凭据提供程序不会自动提交口令");
        }
        Console.WriteLine();

        // 3. cred.bin 的 ACL
        Console.WriteLine("[3] cred.bin 的访问控制");
        if (!File.Exists(Paths.CredentialFile))
        {
            Console.WriteLine("    - 还没有 cred.bin");
        }
        else if (CredentialStore.ExistsButUnreadable())
        {
            // Detecting this ourselves instead of grepping icacls output for a localized
            // "access is denied" string, which would break on a non-Chinese Windows.
            Console.WriteLine("    ? 当前权限打不开 cred.bin，列不出 ACL —— 用管理员身份重跑");
        }
        else
        {
            var acl = RunTool("icacls", $"\"{Paths.CredentialFile}\"");
            if (acl is null) Console.WriteLine("    ? 读不到 ACL（icacls 不可用）");
            else foreach (var line in acl.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("      " + line.TrimEnd());
            Console.WriteLine("    期望：只出现 SYSTEM / Administrators（以及可信的本地管理员组），不应有 Users / Authenticated Users");
        }
        Console.WriteLine();

        // 4. agent 是否在跑
        Console.WriteLine("[4] agent 运行状态");
        if (PresenceChannel.TryRead(out var snapshot))
        {
            var age = DateTime.UtcNow - new DateTime(snapshot.HeartbeatTicks, DateTimeKind.Utc);
            if (age <= PresenceChannel.MaxAge)
            {
                Console.WriteLine($"    ✓ 状态新鲜（{age.TotalSeconds:F1}s 前），触发设备已连接={snapshot.Present}");
            }
            else if (snapshot.Present)
            {
                // 这是真正要修的那种：文件还说着「在场」，心跳却停了。锁屏会拒绝解锁，
                // 而且完全没有提示——用户只会看到「密码不正确」。
                problems++;
                Console.WriteLine($"    ✗ presence.dat 已 {age.TotalMinutes:F0} 分钟没更新，而它最后写的是 present=1"
                                  + " —— agent 在设备在场时停掉了，免密解锁已失效。重启后台 agent。");
            }
            else
            {
                // 「过期 + present=0」是正常形态，不是故障：心跳在设备离开后刻意不再重打时间戳
                // （锁屏对 present=0 本来就什么也不能做）。以前这里报「agent 没在运行」，
                // 于是「带着键盘走开两小时」看起来和「agent 挂了」一模一样。
                //
                // 这一节不猜 agent 进程在不在：doctor 自己就是 btwu 进程，按名字查必然查到自己。
                // 真正的判据在下面第 5 节——那里读的是射频的当前状态，而不是历史记录。
                Console.WriteLine($"    · 状态 {age.TotalMinutes:F0} 分钟没更新，最后写的是 present=0");
                Console.WriteLine("      心跳在设备离开后不再重打时间戳，这是正常的（锁屏对 present=0 无事可做）。");
                Console.WriteLine("      要确认 agent 是否还活着，看第 5 节：那里是设备的当前状态，不是历史。");
            }
        }
        else
        {
            problems++;
            Console.WriteLine("    ✗ 没有 presence.dat —— agent 从未启动，或数据目录不可写");
        }
        Console.WriteLine();

        // 5. 设备
        Console.WriteLine("[5] 触发设备当前连接状态与身份");
        try
        {
            var devices = await DeviceInventory.ListAsync();
            foreach (var trigger in cfg.Triggers)
            {
                var live = devices.FirstOrDefault(d =>
                    string.Equals(d.Id, trigger.Id, StringComparison.OrdinalIgnoreCase));
                if (live is null)
                {
                    problems++;
                    Console.WriteLine($"    ✗ {trigger.Name}: 已不在配对列表里（删了配对或换了设备）");
                    continue;
                }

                Console.WriteLine($"    {(live.Connected ? "✓" : "-")} {trigger.Name} ({live.Kind}) 当前 {(live.Connected ? "已连接" : "未连接")}");

                // The gate is re-evaluated here rather than read back from config.json: a device can be
                // re-paired, re-flashed, or swapped for a same-id clone after enrollment.
                var identity = live.Identity;
                if (HidGate.Satisfies(cfg.RequiredHidKind, identity))
                {
                    Console.WriteLine($"      ✓ 身份通过: {identity.Detail()}");
                }
                else
                {
                    problems++;
                    Console.WriteLine($"      ✗ 身份不通过: {identity.Detail()}");
                    Console.WriteLine($"        要求 {HidGate.RequirementText(cfg.RequiredHidKind)}；" +
                                      "该设备的连接不会让 agent 判定为在场");
                    if (trigger.HidClass.Length > 0 && trigger.HidClass != identity.KindText)
                        Console.WriteLine($"        注意：绑定时记录的是 {trigger.HidClass}，现在变成了 {identity.KindText}");
                }
            }
        }
        catch (Exception ex)
        {
            problems++;
            Console.WriteLine($"    ✗ 枚举设备失败: {ex.Message}");
        }
        Console.WriteLine();

        Console.WriteLine(problems == 0
            ? "结论：各项检查通过。再跑一次 `btwu verify` 确认口令，然后 Win+L 实测一次。"
            : $"结论：{problems} 项需要处理（见上面的 ✗）。");
        return problems == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- verify

    static int Verify(string[] args)
    {
        var credential = CredentialStore.TryLoad();
        if (credential is null)
        {
            if (CredentialStore.ExistsButUnreadable())
            {
                Console.Error.WriteLine($"{Paths.CredentialFile} 存在，但这个进程读不到它——");
                Console.Error.WriteLine("它的 ACL 只允许 SYSTEM 和 Administrators（这是安装脚本故意收紧的）。");
                Console.Error.WriteLine("用管理员身份重新运行这条命令即可，不需要重跑 enroll。");
            }
            else
            {
                Console.Error.WriteLine("cred.bin 不存在或解不开——先跑 btwu enroll");
            }
            return 1;
        }

        var compare = args.Contains("--compare");
        var domain = credential.DomainOrLocalMachine;

        Console.WriteLine("两步独立校验：先问 Win32 官方 API「这个口令对不对」，再问 LSA「我们的打包对不对」。");
        Console.WriteLine("两步都用 SECURITY_LOGON_TYPE.Network：和真正的解锁走同一套 MSV1_0 校验，");
        Console.WriteLine("但不需要 SeTcbPrivilege，所以普通权限就能跑，不需要管理员。");
        Console.WriteLine();
        Console.WriteLine($"账户: {credential.UserName}@{domain}   口令 {credential.Password.Length} 字符");
        Console.WriteLine();

        // The name FORM is a first-class suspect, and nothing about a rejection points at it. If this
        // machine is signed in with a Microsoft account but the credential stores the local SAM name,
        // the password is being offered to a different principal and can never be accepted. Say so
        // before spending a LogonUser attempt on it.
        var signInAddress = AccountIdentity.MicrosoftAccountSignInAddress();
        if (signInAddress is not null &&
            !credential.Domain.Equals(AccountIdentity.MicrosoftAccountDomain, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"⚠ 账户形式可疑：本机登录的是 Microsoft 账户 {signInAddress}，");
            Console.WriteLine($"  但 cred.bin 里存的是本地账户形式 {credential.UserName}@{domain}。");
            Console.WriteLine("  这两种形式在本机解析出的 SID 并不相同（本地 SAM 是 S-1-5-21-...，");
            Console.WriteLine("  Microsoft 账户是 S-1-11-96-...），是**两个不同的主体**。");
            Console.WriteLine("  Microsoft 账户的口令由 Windows 自己管，本地同名账户的口令不是它");
            Console.WriteLine("  （net user 会显示「需要密码: No」），所以下面两步很可能都会失败——");
            Console.WriteLine($"  而这不代表口令录错了。修法：不带 --user/--domain 重跑 btwu enroll，");
            Console.WriteLine($"  它会自动改用 MicrosoftAccount\\{signInAddress}。");
            Console.WriteLine();
        }

        // ---------------------------------------------------------------- 第一步
        // This is the ground truth, and it has to come first. Our own LsaLogonUser probe cannot tell
        // "wrong password" from "wrong packing" - MSV1_0 answers STATUS_LOGON_FAILURE for both, and
        // for a user name that does not exist at all.
        //
        // LogonUser is the documented API, but it is NOT the clean oracle the first draft of this
        // tool assumed: measured on the development machine, LOGON32_LOGON_NETWORK answers
        // ERROR_LOGON_FAILURE (0x52E) for a wrong password AND for a user name that does not exist
        // AND for a nonexistent Microsoft account. So the Win32 error alone cannot separate a wrong
        // password from a wrong account-name FORM - which is exactly the failure mode that matters
        // here, because a Microsoft account and its same-named local SAM entry are different
        // principals with different SIDs. The only honest way to separate them is to ask about the
        // other form as well and let Windows answer.
        Console.WriteLine("[1] LogonUser —— 「cred.bin 里的账户形式 + 口令，Windows 接受吗」");
        Console.WriteLine($"    形式 A（cred.bin 里存的）: {credential.UserName} @ {domain}");
        // 两种形式一次问完（GUI / CLI 与这里共用同一份实现）：
        // ERROR_LOGON_FAILURE 分不出「口令错」和「账户形式错」，只有「A 被拒 / B 被接受」才是结论。
        var probe = LogonProbe.ProbeBothForms(credential.UserName, credential.Domain, credential.Password);
        var ground = probe.Primary;
        Console.WriteLine($"    {ground.Label}");
        if (ground.Success)
        {
            Console.WriteLine("    ✅ 是。LogonUser 拿到了令牌 —— 账户形式和口令都对。");
        }
        else
        {
            Console.WriteLine($"    ❌ 否  0x{ground.Win32Error:X8}  {LogonProbe.ExplainWin32(ground.Win32Error)}");
        }

        // 形式 B 只在形式 A 失败时才问（每失败一次都计入账户的失败计数）。形式 B 成功就是一个**确定的**
        // 诊断：口令没问题，错的是 cred.bin 里记的形式。
        if (probe.Alternate is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"    形式 B（本机的另一种）: {probe.AlternateUser} @ {probe.AlternateDomain}");
            Console.WriteLine($"    {probe.Alternate.Label}");
            Console.WriteLine(probe.AlternateWins
                ? "    ✅ 是。所以口令是对的，错的是 cred.bin 里记的账户形式。"
                : $"    ❌ 否  0x{probe.Alternate.Win32Error:X8}");
        }

        Console.WriteLine();

        // ---------------------------------------------------------------- 第二步
        // msg=2 是有意的，而且它正是锁屏场景该用的那一个：
        //     CPUS_UNLOCK_WORKSTATION(2) -> msg=7 (KerbWorkstationUnlockLogon)
        //     CPUS_LOGON(1)              -> msg=2 (KerbInteractiveLogon)
        // msg=7 告诉 LSA「去解锁 LogonId 指明的那个会话」，而这个字段是留给上层填的；在探针里
        // 它就是 0，等于指了一个不存在的会话，LSA 在比较口令之前就失败了（0xC000006D/0xC00000E5）。
        //
        // 教训（2026-10-14）：这里原本写着「打包格式对错与消息类型无关」，并据此认为 [2] 通过
        // 就代表锁屏能过。实际上 [2] 与真实锁屏有三处不同，任何一处都能翻转结论：
        //     消息类型 msg=2 vs msg=7；登录类型 Network vs Interactive；认证包 MSV1_0 vs Negotiate。
        // 要验证锁屏本身，请用 btwu unlock-probe —— 它复现的就是锁屏那一份提交。
        // The package name matters: "MSV1_0" does not resolve on Windows 11 (see LogonProbe).
        //
        // Step 2 asks "is our packing right", so it has to run under a name form Windows accepts;
        // otherwise it re-tests the form and its answer means nothing either way.
        var lsaUser = credential.UserName;
        var lsaDomain = domain;
        if (probe.AlternateWins)
        {
            Console.WriteLine($"    注：[2] 改用被 Windows 接受的**形式 B** 来测（{probe.AlternateUser} @ {probe.AlternateDomain}），");
            Console.WriteLine("    否则测到的是账户形式对不对，而不是打包对不对。");
            lsaUser = probe.AlternateUser!;
            lsaDomain = probe.AlternateDomain!;
        }

        Console.WriteLine($"[2] LsaLogonUser + 本项目的打包（{LogonProbe.Msv1_0PackageName}）—— 「序列化约定对不对」");
        var primary = LogonProbe.Run(lsaDomain, lsaUser, credential.Password,
            LogonProbe.InteractiveMessageType, LogonBlobPacking.ByteOffsets, LogonProbe.Msv1_0PackageName);
        Print(primary);

        LogonProbeResult? control = null;
        if (compare)
        {
            control = LogonProbe.Run(lsaDomain, lsaUser, credential.Password,
                LogonProbe.InteractiveMessageType, LogonBlobPacking.AbsolutePointers, LogonProbe.Msv1_0PackageName);
            Print(control);
        }

        LogonProbeResult? fallback = null;
        if (primary.Status != LogonProbe.StatusSuccess)
        {
            fallback = LogonProbe.Run(lsaDomain, lsaUser, credential.Password,
                LogonProbe.InteractiveMessageType, LogonBlobPacking.ByteOffsets, "Negotiate");
            Print(fallback);
        }

        Console.WriteLine();
        var packingOk = primary.Status == LogonProbe.StatusSuccess
                        || (fallback is not null && fallback.Status == LogonProbe.StatusSuccess);

        // ---------------------------------------------------------------- 结论
        //
        // "Form A rejected, form B accepted" is the single decisive verdict, so it is checked first.
        // Windows itself named the correct account form, and the password is proven right because it
        // produced a token. Nothing else needs to be guessed at or re-diagnosed.
        if (probe.AlternateWins)
        {
            Console.WriteLine("⇒ 结论：口令没问题，错的是 cred.bin 里记的**账户形式**。");
            Console.WriteLine($"   Windows 接受: {probe.AlternateUser} @ {probe.AlternateDomain}");
            Console.WriteLine($"   Windows 拒绝: {credential.UserName} @ {domain}");
            Console.WriteLine("   两者在本机是不同的主体 —— 本地 SAM 的 SID 是 S-1-5-21-…，");
            Console.WriteLine("   Microsoft 账户的 SID 是 S-1-11-96-…，同一个口令不会同时命中。");
            Console.WriteLine();
            Console.WriteLine($"   序列化打包（[2]，已改用形式 B 测）: {(packingOk ? "通过 ✅" : "被拒 ❌，打包还要再查")}");
            Console.WriteLine();
            Console.WriteLine("   修法：以管理员身份、**不要**带 --user / --domain 重跑 btwu enroll。");
            Console.WriteLine("   enroll 会从本机登记的 Microsoft 账户身份里读出登录地址，自动选对形式；");
            Console.WriteLine("   重录之后再跑一次 btwu verify，那时 [1] 的形式 A 应当直接通过。");
            return 1;
        }

        if (ground.Success && packingOk)
        {
            Console.WriteLine("✅ 全通：账户形式与口令被 Win32 接受，本项目的打包也被 LSA 接受。");
            Console.WriteLine("   DPAPI 往返、打包格式、账户形式、口令都对了，剩下的变量只有锁屏场景本身——");
            Console.WriteLine("   现在按 Win+L 实测。失败了也能看出原因：看 credprov.log 里");
            Console.WriteLine("   SetUsageScenario(1) 后面是 GetSerialization: submitted … 还是 ReportResult: logon failed。");
            return 0;
        }

        if (ground.Success && !packingOk)
        {
            Console.WriteLine("⚠️ 账户形式与口令都对，错的是**我们**：LogonUser 接受了它，但我们自己的");
            Console.WriteLine("   LsaLogonUser 打包被拒。这不是重新录入能解决的，重录只会把同一个正确答案再存一遍。");
            Console.WriteLine("   要查的是 LogonProbe.cs 的 BuildBlob / 消息类型 / 包名选择，或者订阅 CP 那边");
            Console.WriteLine("   BtwuCredProv.cpp 的打包，两边必须一致。请把这段输出保留下来。");
            Console.WriteLine();
            Console.WriteLine("   顺带一提：写死的 \"MSV1_0\" 这个包名在 Windows 11 上查不到（STATUS_NO_SUCH_PACKAGE），");
            Console.WriteLine("   已改成 MICROSOFT_AUTHENTICATION_PACKAGE_V1_0。见 LogonProbe.Msv1_0PackageName 的注释。");
            return 1;
        }

        Console.WriteLine("❌ 两种账户形式都被拒。注意这只说明「这些形式 + 这个口令」没被接受，");
        Console.WriteLine("   不能就此断定是口令录错了：MSV1_0 把「口令错」和「账户形式/名字不对」");
        Console.WriteLine("   报成同一个错误码（本机实测：根本不存在的用户名也返回同样的 0x52E）。");
        Console.WriteLine("   常见原因：");
        Console.WriteLine("     · 录的是 PIN 而不是账户口令——PIN 只在本机解锁用，不是账户口令；");
        Console.WriteLine("     · 账户是 Microsoft 账户，录的却是别的口令（或改过口令）；");
        Console.WriteLine("     · 机器离线，且本地没有可校验的口令副本；");
        Console.WriteLine("     · 账户是 Entra ID / 域账户，本工具还没覆盖这种形式。");
        Console.WriteLine();
        Console.WriteLine("   重跑： btwu enroll   （安装脚本收紧过 ACL，所以要用管理员身份）");
        return 1;
    }

    static void Print(LogonProbeResult result)
    {
        Console.WriteLine($"  {result.Label}");
        Console.WriteLine($"    NTSTATUS 0x{result.Status:X8}  substatus 0x{result.SubStatus:X8}");
        Console.WriteLine($"    {result.Explanation}");
    }

    // ---------------------------------------------------------- unlock probe

    /// <summary>
    /// Reproduces, as exactly as an elevated console can, the ONE submission LogonUI hands to LSA
    /// when the user presses Enter on our tile - and then repeats it for every plausible spelling of
    /// the account name, stopping at the first form LSA accepts.
    ///
    /// Why this exists next to <c>btwu verify</c>:
    ///
    ///   verify [2] drives LsaLogonUser with message type 2, logon type Network, package MSV1_0.
    ///   The lock screen submits message type 7, logon type Interactive, package Negotiate.
    ///
    /// Three differences, and every one of them can flip the answer. So "verify passed" says nothing
    /// about whether the lock screen will accept the credential - which is exactly the trap this
    /// machine fell into: verify was green while the lock screen answered "user name or password is
    /// incorrect" and credprov.log recorded 0xC000006D / 0xC00000E5.
    ///
    /// The first attempt is a calibration: it re-submits cred.bin verbatim, so it must reproduce the
    /// lock screen's failure pair. If it does, the probe is faithful and a later success is
    /// trustworthy; if it does not, the probe is measuring something else and says so out loud.
    /// </summary>
    static int UnlockProbe(string[] args)
    {
        // Optional self-logging via --log PATH. The TOOL owns this file rather than a shell capturing
        // its output, and that is deliberate - see TeeTextWriter for the two Windows PowerShell 5.1
        // traps that cost this project two debugging rounds. The strings are already correct here, so
        // whatever the shell would have done to the console code page cannot reach the file.
        var logPath = Arg(args, "--log");
        var realOut = Console.Out;
        var realError = Console.Error;
        StreamWriter? logWriter = null;

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            try
            {
                logWriter = new StreamWriter(logPath!,
                    append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                Console.SetOut(new TeeTextWriter(realOut, logWriter));
                Console.SetError(new TeeTextWriter(realError, logWriter));
            }
            catch (Exception ex)
            {
                logWriter = null;
                realError.WriteLine($"无法写入日志文件 {logPath}：{ex.Message}");
            }
        }

        try
        {
            return UnlockProbeCore(args.Contains("--apply"));
        }
        finally
        {
            if (logWriter is not null)
            {
                Console.SetOut(realOut);
                Console.SetError(realError);
                logWriter.Flush();
                logWriter.Dispose();
                realOut.WriteLine();
                realOut.WriteLine($"----- 以上内容已写入 {logPath} -----");
            }
        }
    }

    static int UnlockProbeCore(bool apply)
    {
        if (!Registration.IsElevated)
        {
            Console.Error.WriteLine("这条命令要驱动 LSA 完成一次真正的交互式登录，需要管理员权限");
            Console.Error.WriteLine("（LSA 要求 SeTcbPrivilege 与 SeIncreaseQuotaPrivilege，管理员持有它们但默认关闭）。");
            Console.Error.WriteLine();
            Console.Error.WriteLine("请以管理员身份重跑： " + (apply ? "btwu unlock-probe --apply" : "btwu unlock-probe"));
            return 2;
        }

        var credential = CredentialStore.TryLoad();
        if (credential is null)
        {
            if (CredentialStore.ExistsButUnreadable())
            {
                Console.Error.WriteLine($"{Paths.CredentialFile} 存在但读不出来——它的 ACL 只允许 SYSTEM 和 Administrators。");
                Console.Error.WriteLine("请确认是在管理员窗口里运行的。");
            }
            else
            {
                Console.Error.WriteLine("cred.bin 不存在或解不开——先跑 btwu enroll");
            }
            return 1;
        }

        if (!LogonProbe.EnableLogonPrivileges(out var privilegeFailure))
        {
            Console.Error.WriteLine($"无法启用交互式登录所需的特权：{privilegeFailure}");
            Console.Error.WriteLine("请确认是以管理员身份运行（普通管理员令牌就够，不需要 SYSTEM）。");
            return 2;
        }

        var machine = Environment.MachineName;
        var localUser = Environment.UserName;

        // Ordered by likelihood, and de-duplicated below. Attempt 1 must stay first: it is the
        // calibration against the lock screen's known answer. Attempt 2 is what the credential
        // provider submits now that the message type follows the scenario, so in the expected case
        // the sweep stops after two attempts instead of five.
        var forms = new List<(int Message, string Domain, string User, string Note)>
        {
            (LogonProbe.UnlockMessageType, credential.Domain, credential.UserName,
                "校准：消息类型 7 + 原样的名字 —— 这是 2026-10-14 之前凭据提供程序提交的那一份，"
                + "应当重现锁屏上那一对 0xC000006D/0xC00000E5"),
            (LogonProbe.InteractiveMessageType, credential.Domain, credential.UserName,
                "修复后的提交：同样的名字，消息类型改为交互登录 2（scenario 1 = CPUS_LOGON 该用的）"),
            (LogonProbe.InteractiveMessageType, machine, localUser,
                "消息类型 2 + 本机 SAM 账户形式 —— 与磁贴声明的账户 SID 是同一个主体"),
            (LogonProbe.UnlockMessageType, machine, localUser,
                "消息类型 7 + 本机 SAM 账户形式（若这一项被接受，说明问题只在名字，不在消息类型）"),
            (LogonProbe.InteractiveMessageType, machine, credential.UserName,
                "消息类型 2 + 同一个登录地址，但挂在本机自己的授权下（邮箱别名解析到本地账户）"),
        };

        var queue = new List<(int Message, string Domain, string User, string Note)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var form in forms)
        {
            if (seen.Add($"{form.Message}|{form.Domain}|{form.User}")) queue.Add(form);
        }

        Console.WriteLine("锁屏提交探针 —— 逐字复现「在磁贴上按回车」时 LogonUI 交给 LSA 的那一份提交，");
        Console.WriteLine("然后对每一种可能的登录名形式各重放一次，看 LSA 真正接受哪一个。");
        Console.WriteLine();
        Console.WriteLine($"保管的账户: {credential.UserName} @ {credential.Domain}   口令 {credential.Password.Length} 字符");
        Console.WriteLine($"本会话用户: {machine}\\{localUser}");
        Console.WriteLine($"账户 SID  : {(credential.Sid.Length == 0 ? "(空 —— 锁屏挂不上磁贴)" : credential.Sid)}");
        Console.WriteLine();
        Console.WriteLine($"提交参数与锁屏保持一致: 包=Negotiate  登录类型=Interactive(2)  打包=ByteOffsets");
        Console.WriteLine($"最多尝试 {queue.Count} 次，遇到成功立即停止。失败的那些会计入账户的失败口令计数。");
        Console.WriteLine();

        var attempts = 0;
        LogonProbeResult? calibration = null;
        (int Message, string Domain, string User, string Note)? winner = null;

        foreach (var form in queue)
        {
            attempts++;
            var sid = LogonProbe.ResolveSidText(form.Domain, form.User);

            Console.WriteLine($"[{attempts}] {form.User} @ {form.Domain}      (msg={form.Message})");
            Console.WriteLine($"     {form.Note}");
            Console.WriteLine($"     名字在本机解析出的主体: {(sid is null ? "解析不出（不存在这个名字）" : sid)}");

            var result = LogonProbe.Run(form.Domain, form.User, credential.Password,
                form.Message, LogonBlobPacking.ByteOffsets, "Negotiate", LogonProbe.LogonTypeInteractive);

            Console.WriteLine($"     NTSTATUS 0x{result.Status:X8}   substatus 0x{result.SubStatus:X8}");
            Console.WriteLine($"     {result.Explanation}");

            calibration ??= result;

            if (result.Status == LogonProbe.StatusSuccess)
            {
                winner = form;
                Console.WriteLine("     ✅ 被接受 —— 停止尝试");
                Console.WriteLine();
                break;
            }

            Console.WriteLine();
        }

        Console.WriteLine("=== 结论 ===");

        // Calibration first, because it decides how much the rest is worth.
        if (calibration is { } first
            && first.Status == LogonProbe.StatusLogonFailure
            && first.SubStatus == unchecked((int)LogonProbe.StatusInternalError))
        {
            Console.WriteLine("✅ 校准通过：重放 cred.bin 原样提交，得到的正是锁屏上那一对");
            Console.WriteLine("   (0xC000006D / 0xC00000E5)。说明探针和真机走的是同一条路，");
            Console.WriteLine("   下面任何一次成功都可信、任何一次失败也都能说明问题。");
        }
        else
        {
            Console.WriteLine("⚠ 校准未复现：原样提交的结果和锁屏上那次不一样，所以下面的对照可靠性要打折。");
            Console.WriteLine("   请把这一整段输出发出来，不要据此下结论。");
        }
        Console.WriteLine();

        if (winner is not { } found)
        {
            Console.WriteLine($"❌ {attempts} 种形式全部被拒。");
            Console.WriteLine();
            Console.WriteLine("这说明问题不在「名字怎么写」，而在更下面一层。接着要查的是：");
            Console.WriteLine("  · 口令本身对不对 —— 跑 btwu verify，它的 [1] 是让 Windows 自己回答；");
            Console.WriteLine("  · 认证包不同：凭据提供程序用 Negotiate，而 btwu verify 的 [2] 用的是");
            Console.WriteLine("    MICROSOFT_AUTHENTICATION_PACKAGE_V1_0。两者在 MSA 账户上不一定等价；");
            Console.WriteLine("  · 消息类型与该场景是否匹配（锁屏走 scenario 1，这里提交的是 msg=7）。");
            return 1;
        }

        var winnerSid = LogonProbe.ResolveSidText(found.Domain, found.User);
        Console.WriteLine("⇒ 结论：LSA 接受的是");
        Console.WriteLine($"     {found.User} @ {found.Domain}      (msg={found.Message})");
        Console.WriteLine($"     解析主体: {winnerSid ?? "(解析不出)"}");
        if (credential.Sid.Length > 0 && winnerSid is not null &&
            !winnerSid.Equals(credential.Sid, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("     ⚠ 注意：这个主体和 cred.bin 里的账户 SID **不是同一个**。锁屏靠 SID 挂磁贴，");
            Console.WriteLine("       两者必须是同一主体，否则会退回「磁贴挂不上」那一类问题。");
        }
        Console.WriteLine();

        if (found.Message != LogonProbe.InteractiveMessageType)
        {
            Console.WriteLine("⚠ 被接受的是消息类型 7（KerbWorkstationUnlockLogon），而凭据提供程序在锁屏场景");
            Console.WriteLine("   (scenario 1 = CPUS_LOGON) 提交的是消息类型 2。也就是说名字形式对了、消息类型");
            Console.WriteLine("   却不对 —— 请把这段输出发出来，BtwuCredProv.cpp 里的选择还得再改。");
            Console.WriteLine();
        }

        Console.WriteLine("修法：");
        Console.WriteLine($"  1. 直接采用（不用重输口令）： btwu unlock-probe --apply");
        Console.WriteLine($"  2. 重新录入（会再问一次口令）： btwu enroll --user \"{found.User}\" --domain \"{found.Domain}\"");
        Console.WriteLine();

        if (!apply) return 0;

        try
        {
            CredentialStore.Save(new StoredCredential(found.User, found.Domain, credential.Password, credential.Sid));
            var cfg = AppConfig.Load();
            cfg.UserName = found.User;
            cfg.Domain = found.Domain;
            cfg.Save();
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("写 cred.bin 失败——需要管理员权限。");
            return 2;
        }

        Console.WriteLine("✅ 已写入 cred.bin 与 config.json：");
        Console.WriteLine($"     {found.User} @ {found.Domain}");
        Console.WriteLine($"     账户 SID 保持不变: {credential.Sid}");
        Console.WriteLine();
        Console.WriteLine("   现在按 Win+L，然后按回车实测。");
        Console.WriteLine("   再失败的话，先看 %ProgramData%\\BtwuUnlock\\credprov.log 的尾部。");
        return 0;
    }

    // ------------------------------------------------------ credential provider

    static int InstallCredProv(string[] args)
    {
        if (!Registration.IsElevated)
            throw new UnauthorizedAccessException("安装凭据提供程序需要管理员权限");

        var source = Arg(args, "--dll");
        if (source is null)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(AppContext.BaseDirectory, Registration.DllFileName),
                         Path.Combine(AppContext.BaseDirectory, "..", "Btwu.CredProv", "build", "x64", Registration.DllFileName),
                         Path.Combine(AppContext.BaseDirectory, "..", "..", "Btwu.CredProv", "build", "x64", Registration.DllFileName),
                     })
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full)) { source = full; break; }
            }
        }

        if (source is null || !File.Exists(source))
        {
            Console.Error.WriteLine($"没有找到 {Registration.DllFileName}——先跑 scripts\\build.ps1，或用 --dll 指定路径。");
            return 1;
        }

        var target = Registration.SystemDllPath;
        if (!string.Equals(Path.GetFullPath(source), target, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(source, target, overwrite: true);
            Console.WriteLine($"已拷贝: {source}");
            Console.WriteLine($"     -> {target}");
        }

        Registration.Register(target);
        Console.WriteLine($"已注册 CLSID {Registration.ClsidText}");
        Console.WriteLine($"  InprocServer32      = {target}");
        Console.WriteLine($"  ThreadingModel      = Apartment");
        Console.WriteLine($"  Credential Providers  已加入（名称: {Registration.ProviderName}）");
        Console.WriteLine();
        Console.WriteLine("生效时机：下一次锁屏/登录界面加载时。若锁屏界面正在显示，先解锁再锁一次。");
        Console.WriteLine("卸载：btwu uninstall-cp（或 scripts\\uninstall.ps1）");
        return 0;
    }

    static int UninstallCredProv()
    {
        if (!Registration.IsElevated)
            throw new UnauthorizedAccessException("注销凭据提供程序需要管理员权限");

        Registration.Unregister();
        Console.WriteLine($"已注销 CLSID {Registration.ClsidText}");

        var dll = Registration.SystemDllPath;
        if (File.Exists(dll))
        {
            try
            {
                File.Delete(dll);
                Console.WriteLine($"已删除: {dll}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"删除 {dll} 失败（可能仍被 LogonUI 占用，注销登录后再删）: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"{dll} 不存在，跳过删除");
        }

        Console.WriteLine();
        Console.WriteLine("注意：正在显示的锁屏界面由已加载的 DLL 服务，注销登录一次即可完全恢复原状。");
        return 0;
    }

    // ----------------------------------------------------------- pause/resume

    static int Pause(string[] args)
    {
        var minutes = ArgInt(args, "--minutes") ?? 60;
        Paths.EnsureDataDir();
        var until = DateTime.UtcNow.AddMinutes(minutes).Ticks;
        File.WriteAllText(Paths.PauseFile, until.ToString());
        Console.WriteLine($"已暂时停用免密解锁，直到 {new DateTime(until, DateTimeKind.Utc).ToLocalTime():HH:mm:ss}"
            + "（锁屏不再出现免密磁贴，用密码/PIN 照常登录）");
        return 0;
    }

    static int Resume()
    {
        try { if (File.Exists(Paths.PauseFile)) File.WriteAllText(Paths.PauseFile, "0"); }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        Console.WriteLine("已恢复免密解锁");
        return 0;
    }

    /// <summary>
    /// The credential provider's kill switch. Unlike <see cref="Pause"/>, this is read by the DLL
    /// itself, so it works even when the agent is not running - which is the situation anyone is in
    /// when they reach for it.
    /// </summary>
    static int KillSwitch(bool disable)
    {
        try
        {
            if (disable)
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllText(Paths.DisableCpFile, "");
                Console.WriteLine($"已停用凭据提供程序：{Paths.DisableCpFile}");
                Console.WriteLine("锁屏从此与本工具无关（返回 0 个凭据），PIN / 口令 / 指纹照常。");
                Console.WriteLine("恢复： btwu enable-cp");
            }
            else if (File.Exists(Paths.DisableCpFile))
            {
                File.Delete(Paths.DisableCpFile);
                Console.WriteLine("已重新启用凭据提供程序");
            }
            else
            {
                Console.WriteLine("本来就是启用的");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Chooses between the two ways the credential provider can get the stored password into LSA.
    ///
    ///   Enter mode (default) - the credential is offered as the lock screen's *selected tile* only.
    ///                         Windows paints its normal sign-in UI (user tile, PIN, password box,
    ///                         fingerprint) and pressing Enter, or clicking 解锁, submits the stored
    ///                         password. Nothing is announced to LogonUI in advance, so there is no
    ///                         state it can hang in if the credential turns out to be unusable.
    ///
    ///   Auto mode           - pbAutoLogonWithDefault is set, so LogonUI submits the moment it
    ///                         enumerates: a manual Win+L bounces straight back to the desktop with no
    ///                         keystroke at all. Safe only because the provider now validates the
    ///                         identity and pre-packs the whole submission before it reports anything.
    ///
    /// Like the kill switch, this is a file the DLL reads itself, so it takes effect on the next
    /// lock/unlock even with the agent stopped.
    /// </summary>
    static int SetAutoSubmit(bool enabled)
    {
        try
        {
            if (enabled)
            {
                Directory.CreateDirectory(Paths.DataDir);
                File.WriteAllText(Paths.AutoSubmitFile, "");
                Console.WriteLine($"全自动模式：{Paths.AutoSubmitFile}");
                Console.WriteLine("锁屏时不再需要回车，凭据一被枚举就直接提交。");
            }
            else if (File.Exists(Paths.AutoSubmitFile))
            {
                File.Delete(Paths.AutoSubmitFile);
                Console.WriteLine("回车模式：凭据作为默认磁贴提供，按回车（或点「解锁」）提交。");
            }
            else
            {
                Console.WriteLine("本来就是回车模式（默认）");
            }
            Console.WriteLine("下一步：按一次 Win+L 实测；若锁屏出现任何异常，先跑 btwu disable-cp。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static int LockNow()
    {
        if (!LockWorkStation())
        {            Console.Error.WriteLine($"LockWorkStation 失败: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
            return 1;
        }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool LockWorkStation();

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Maps a user-supplied --require-hid value onto the canonical config spelling.
    ///
    /// Internal rather than private because the enrollment dialog runs the identical gate; a second
    /// spelling table in the GUI is a second thing to keep in sync with the C++ side.
    /// </summary>
    internal static string NormalizeHidRequirement(string text) => text.Trim().ToLowerInvariant() switch
    {
        "keyboard" or "kb" or "kbd" => "Keyboard",
        "hid" or "device" or "peripheral" or "input" => "Hid",
        "any" or "none" or "off" or "disable" => "Any",
        _ => "",
    };

    /// <summary>
    /// Turns a stored domain component into the one <c>LogonUser</c> wants. "." and "" mean "this
    /// machine", and LogonUser wants the actual machine name there. Every other value - including the
    /// literal "MicrosoftAccount" - is passed through untouched: it is a real logon-name prefix, not a
    /// domain in the DNS sense.
    /// </summary>

    /// <summary>
    /// The SID to store for <c>ICredentialProviderCredential2::GetUserSid</c>.
    ///
    /// THIS IS NOT THE SID OF THE NAME THAT GETS REPLAYED TO LSA, and conflating the two broke the
    /// lock screen on 2026-10-14. The two roles are genuinely different:
    ///
    ///   * <c>StoredCredential.UserName</c> + <c>Domain</c> are the *logon name* handed to LSA, which
    ///     for a Microsoft account must be <c>MicrosoftAccount\&lt;sign-in address&gt;</c>.
    ///   * <c>StoredCredential.Sid</c> is the *local account* the credential belongs to, which is what
    ///     LogonUI uses to place the credential on a user tile. That is the account the interactive
    ///     session is signed in as: <c>S-1-5-21-...</c>.
    ///
    /// Resolving the logon name instead yields a different identity for a Microsoft account
    /// (measured: <c>MicrosoftAccount\alice@example.com</c> -> <c>S-1-11-96-...</c>). LogonUI cannot
    /// attach a credential to that, and the lock screen came up with no UI at all - no user tile, no
    /// password box, no PIN; only Windows Hello still worked.
    ///
    /// So resolve the *local* account, and prefer the SAM name under the machine's own authority.
    /// </summary>
    internal static string ResolveInteractiveUserSid()
    {
        foreach (var candidate in new[]
                 {
                     new System.Security.Principal.NTAccount(Environment.MachineName, Environment.UserName),
                     new System.Security.Principal.NTAccount(Environment.UserName),
                 })
        {
            try
            {
                var sid = ((System.Security.Principal.SecurityIdentifier)candidate
                    .Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value;
                if (sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) ||
                    sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))
                    return sid;
            }
            catch
            {
                // Try the next form.
            }
        }

        // Fallback: whoever is running enroll. UAC elevation keeps the same SID, so in the normal
        // (elevated, same user) case this is the identical answer to the loop above.
        try
        {
            return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot resolve the interactive user SID: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// True when a SID names an account LogonUI can attach a credential to: a local/AD account
    /// (<c>S-1-5-21-…</c>) or an Entra ID account (<c>S-1-12-1-…</c>). Mirrors <c>LocalAccountSid</c>
    /// in BtwuCredProv.cpp, which refuses anything else rather than break the lock screen.
    /// </summary>
    internal static bool IsAccountSid(string sid) =>
        sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) ||
        sid.StartsWith("S-1-12-1-", StringComparison.Ordinal);

    static string ResolveSid(string user, string domain)
    {
        // Legacy path, kept only so an explicit --user/--domain enrolment can still name a different
        // account. The credential provider's GetUserSid refuses anything that is not an account SID,
        // so a wrong answer here degrades to "no user tile" rather than to a broken lock screen.
        try
        {
            var account = domain is "." or ""
                ? new System.Security.Principal.NTAccount(user)
                : new System.Security.Principal.NTAccount(domain, user);
            return ((System.Security.Principal.SecurityIdentifier)account
                .Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value;
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot resolve SID for {user}: {ex.Message}");
            return "";
        }
    }

    static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    static int? ArgInt(string[] args, string name) => int.TryParse(Arg(args, name), out var v) ? v : null;

    static List<string> ArgList(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) values.Add(args[i + 1]);
        return values;
    }

    static string ReadPassword(string prompt)
    {
        // Console.ReadKey needs a real console. Catch the redirect here rather than letting the raw
        // InvalidOperationException ("Cannot read keys when either application does not have a
        // console...") reach someone who piped stdin on purpose, or whose shell does it by accident.
        if (Console.IsInputRedirected)
            throw new InvalidOperationException(
                "录入口令需要真实控制台：口令是按字符读取且不回显的，无法从管道读。" +
                "请在交互式窗口里直接运行 btwu enroll。");

        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                continue;
            }
            if (!char.IsControl(key.KeyChar)) { sb.Append(key.KeyChar); Console.Write('*'); }
        }
        return sb.ToString();
    }

    static string? RunTool(string exe, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Duplicates everything written to it into a second writer, so a command's console output can also
/// land in a file without a shell being involved.
///
/// WHY THIS EXISTS. Capturing a native program's output in Windows PowerShell 5.1 cost this project
/// two rounds of debugging on the same command:
///
///   * <c>Tee-Object -Encoding</c> does not exist before PowerShell 6. Passing it is a parameter
///     binding failure, and that kills the WHOLE pipeline - so the tool never even ran and nothing
///     was written to the log at all.
///   * 5.1 decodes a native command's stdout using the console code page (936 on this machine), so a
///     tool printing UTF-8 came back as "杩欐潯鍛戒护" and that mojibake was then written to the log.
///
/// A C# writer holding the strings in memory has neither problem: the text is already correct, so
/// whatever the shell does to the console cannot reach the file.
/// </summary>
sealed class TeeTextWriter : TextWriter
{
    readonly TextWriter m_first;
    readonly TextWriter m_second;

    public TeeTextWriter(TextWriter first, TextWriter second)
    {
        m_first = first;
        m_second = second;
    }

    // WriteLine() reads NewLine off the writer, so both have to agree or the file gets \r\n while
    // the console gets whatever it was set to. The attribute is not decoration: the base setter is
    // declared [AllowNull], and overriding it without saying so raises CS8765.
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string NewLine
    {
        get => m_first.NewLine;
        set
        {
            m_first.NewLine = value;
            m_second.NewLine = value;
        }
    }

    public override Encoding Encoding => m_first.Encoding;

    public override void Write(char value)
    {
        m_first.Write(value);
        m_second.Write(value);
    }

    public override void Write(string? value)
    {
        m_first.Write(value);
        m_second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        m_first.WriteLine(value);
        m_second.WriteLine(value);
    }

    protected override void Dispose(bool disposing)
    {
        // Never dispose the console writer; only make sure the file writer is flushed.
        if (disposing) m_second.Flush();
        base.Dispose(disposing);
    }
}
