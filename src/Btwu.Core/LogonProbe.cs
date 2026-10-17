using System.Runtime.InteropServices;
using System.Text;

namespace Btwu.Core;

/// <summary>
/// How a credential provider lays out the <c>KERB_INTERACTIVE_UNLOCK_LOGON</c> it hands back from
/// <c>GetSerialization</c>.
///
/// This is the single most error-prone detail in the whole project. Winlogon and LSA consume a
/// "packed" structure in which each <c>UNICODE_STRING.Buffer</c> is a byte OFFSET from the start of
/// the blob, not an address - the blob is copied to LSA, so real pointers would dangle. Microsoft's
/// own credential provider sample says so in <c>KerbInteractiveUnlockLogonPack</c>:
///
///   "WinLogon and LSA consume 'packed' KERB_INTERACTIVE_UNLOCK_LOGONs. In these, the PWSTR members
///    of each UNICODE_STRING are not actually pointers but byte offsets into the overall buffer."
///
/// <see cref="ByteOffsets"/> is what production credential providers use and what
/// <see cref="LogonBlobPacking.AbsolutePointers"/> exists to be compared against - see
/// <c>btwu verify</c>, which feeds both forms to LSA and reports which one actually authenticates.
/// </summary>
public enum LogonBlobPacking
{
    /// <summary>Interprets <c>Buffer</c> as a real pointer. Kept only for the A/B probe.</summary>
    AbsolutePointers,

    /// <summary>Interprets <c>Buffer</c> as a byte offset from the start of the blob. The correct one.</summary>
    ByteOffsets,
}

public sealed record LogonProbeResult(string Label, uint Status, int SubStatus, string Explanation);

/// <summary>Outcome of the <c>LogonUser</c> ground-truth check: a Win32 error, not an NTSTATUS.</summary>
public sealed record LogonUserResult(string Label, bool Success, int Win32Error);

/// <summary>
/// 同一个口令按两种账户形式各问 Windows 一次的结果（见 <see cref="LogonProbe.ProbeBothForms"/>）。
///
/// <see cref="AlternateWins"/> 是唯一能把「口令错」和「账户形式错」分开的那个组合：
/// 主形式被拒、而另一种形式被接受 ⇒ **口令是对的，错的是形式**。
/// </summary>
public sealed record TwoFormProbe(
    LogonUserResult Primary,
    string? AlternateUser,
    string? AlternateDomain,
    LogonUserResult? Alternate)
{
    /// <summary>另一种形式被接受：口令没问题，采用的是 <see cref="AlternateUser"/> / <see cref="AlternateDomain"/>。</summary>
    public bool AlternateWins => !Primary.Success && Alternate is { Success: true };

    /// <summary>两种形式都被拒 —— 这一次不是形式的错。</summary>
    public bool BothRejected => !Primary.Success && Alternate is { Success: false };
}

/// <summary>
/// Drives <c>LsaLogonUser</c> with a blob built exactly like the credential provider builds it, so
/// the stored credential and the serialization convention can be verified without locking the
/// screen.
///
/// The probe deliberately uses <c>SECURITY_LOGON_TYPE.Network</c>: it validates the credentials
/// through the same MSV1_0/Negotiate path as an unlock while needing no SeTcbPrivilege, so it works
/// from a normal command prompt.
/// </summary>
public static class LogonProbe
{
    /// <summary>
    /// <c>KERB_LOGON_SUBMIT_TYPE</c> for <c>KerbInteractiveLogon</c> - what <c>CPUS_LOGON</c> (1)
    /// submits. Since Windows 10/11 report the LOCK SCREEN as <c>CPUS_LOGON</c>, this is the message
    /// type the lock screen expects. Do not "fix" it to <see cref="UnlockMessageType"/> because the
    /// operation is an unlock; see the message-type note in <c>BtwuCredProv.cpp</c>.
    /// </summary>
    public const int InteractiveMessageType = 2;

    /// <summary>
    /// <c>KERB_LOGON_SUBMIT_TYPE</c> for <c>KerbWorkstationUnlockLogon</c> - what
    /// <c>CPUS_UNLOCK_WORKSTATION</c> (2) submits, and only that scenario. It asks LSA to unlock the
    /// session named by <c>LogonId</c>, which is left zero for Winlogon to fill in, so submitting it
    /// with a zero LUID fails with <c>STATUS_INTERNAL_ERROR</c> before the password is ever compared.
    /// </summary>
    public const int UnlockMessageType = 7;

    // STATUS codes worth naming; anything else is reported numerically.
    public const uint StatusSuccess = 0x00000000;
    public const uint StatusNoSuchUser = 0xC0000064;
    public const uint StatusWrongPassword = 0xC000006A;
    public const uint StatusLogonFailure = 0xC000006D;
    public const uint StatusInternalError = 0xC00000E5;
    public const uint StatusNoSuchPackage = 0xC00000FE;
    public const uint StatusInvalidParameter = 0xC000000D;
    public const uint StatusNoLogonServers = 0xC000005E;
    public const uint StatusAccountRestriction = 0xC000006E;
    public const uint StatusInvalidLogonHours = 0xC000006F;
    public const uint StatusInvalidWorkstation = 0xC0000070;
    public const uint StatusPasswordExpired = 0xC0000071;
    public const uint StatusAccountDisabled = 0xC0000072;
    public const uint StatusAccountLockedOut = 0xC0000234;
    public const uint StatusPasswordMustChange = 0xC0000224;

    /// <summary>
    /// The LSA authentication package that implements MSV1_0.
    ///
    /// Do NOT look this up as <c>"MSV1_0"</c>. That short name does not resolve on Windows 11 25H2:
    /// it comes back as <c>STATUS_NO_SUCH_PACKAGE</c> (0xC00000FE) even though the package is
    /// obviously present. Measured on this machine with <c>tools\lsadiag</c>:
    ///
    ///   MSV1_0                               -> 0xC00000FE   (absent)
    ///   MICROSOFT_AUTHENTICATION_PACKAGE_V1_0 -> packageId 11 (present)
    ///
    /// This is also the name <c>LogonUser</c> itself resolves, which is why
    /// <see cref="CheckPassword"/> agrees with the probe instead of contradicting it.
    /// </summary>
    public const string Msv1_0PackageName = "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0";

    const int LogonTypeNetwork = 3;

    /// <summary>
    /// <c>SECURITY_LOGON_TYPE.Interactive</c>. This is the logon type the lock screen really uses,
    /// and it is NOT interchangeable with <see cref="LogonTypeNetwork"/>: the two take different
    /// paths through MSV1_0, and for an account whose password is owned by Windows (a Microsoft
    /// account) they can disagree outright. Driving it needs SeTcbPrivilege plus
    /// SeIncreaseQuotaPrivilege, i.e. an elevated process - see <see cref="EnableLogonPrivileges"/>.
    /// </summary>
    public const int LogonTypeInteractive = 2;

    // --- privilege plumbing, only needed for the interactive logon type -------------------------

    const uint TokenAdjustPrivileges = 0x0020;
    const uint TokenQuery = 0x0008;
    const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    struct LuidAndAttributes
    {
        public long Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    /// <summary>
    /// Turns on the two privileges <c>LsaLogonUser</c> demands for an interactive logon. An
    /// administrator holds both but they are disabled in the token until asked for, which is why a
    /// probe that skips this step reports ERROR_PRIVILEGE_NOT_HELD (1314) for a perfectly good
    /// credential. Never throws; the caller reports whatever failed.
    /// </summary>
    public static bool EnableLogonPrivileges(out string failure)
    {
        failure = "";
        var names = new[] { "SeTcbPrivilege", "SeIncreaseQuotaPrivilege" };
        var state = new TokenPrivileges { PrivilegeCount = 1 };

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            failure = "OpenProcessToken failed";
            return false;
        }

        try
        {
            foreach (var name in names)
            {
                if (!LookupPrivilegeValue(null, name, out var luid))
                {
                    failure = $"LookupPrivilegeValue({name}) failed";
                    return false;
                }

                state.Privileges.Luid = luid;
                state.Privileges.Attributes = SePrivilegeEnabled;
                if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    failure = $"AdjustTokenPrivileges({name}) failed";
                    return false;
                }

                // AdjustTokenPrivileges "succeeds" when the privilege is absent - the only signal is
                // the last error, which must be cleared first to stay meaningful.
                var error = Marshal.GetLastWin32Error();
                if (error == 1300 /* ERROR_NOT_ALL_ASSIGNED */)
                {
                    failure = $"{name} is not held by this token - run this elevated";
                    return false;
                }
            }
        }
        finally
        {
            CloseHandle(token);
        }

        return true;
    }

    /// <summary>
    /// Which principal a <c>domain\user</c> pair resolves to, as an <c>S-1-5-21-...</c> string, or
    /// <c>null</c> when it resolves to nothing. Shown next to every probe attempt on purpose: the
    /// whole point of the form sweep is that a Microsoft account and its same-named local SAM entry
    /// are DIFFERENT principals, and a submission has to match the user who owns the session.
    /// </summary>
    public static string? ResolveSidText(string domain, string user)
    {
        var candidates = new[]
        {
            new System.Security.Principal.NTAccount(domain, user),
            new System.Security.Principal.NTAccount(user),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                return ((System.Security.Principal.SecurityIdentifier)candidate
                    .Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value;
            }
            catch
            {
                // Try the next spelling.
            }
        }

        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LsaString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TokenSource
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] SourceName;

        public Luid SourceIdentifier;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct QuotaLimits
    {
        public UIntPtr PagedPoolLimit;
        public UIntPtr NonPagedPoolLimit;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public UIntPtr PagefileLimit;
        public long TimeLimit;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KerbInteractiveLogon
    {
        public int MessageType;
        public UnicodeString LogonDomainName;
        public UnicodeString UserName;
        public UnicodeString Password;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KerbInteractiveUnlockLogon
    {
        public KerbInteractiveLogon Logon;
        public Luid LogonId;
    }

    [DllImport("secur32.dll")]
    static extern uint LsaConnectUntrusted(out IntPtr lsaHandle);

    [DllImport("secur32.dll", CharSet = CharSet.Ansi)]
    static extern uint LsaLookupAuthenticationPackage(IntPtr lsaHandle, ref LsaString packageName, out uint authenticationPackage);

    [DllImport("secur32.dll")]
    static extern uint LsaDeregisterLogonProcess(IntPtr lsaHandle);

    [DllImport("secur32.dll")]
    static extern uint LsaFreeReturnBuffer(IntPtr buffer);

    [DllImport("secur32.dll")]
    static extern uint LsaLogonUser(IntPtr lsaHandle, ref LsaString originName, int logonType, uint authenticationPackage,
        IntPtr authenticationInformation, uint authenticationInformationLength, IntPtr localGroups,
        ref TokenSource sourceContext, out IntPtr profileBuffer, out uint profileBufferLength, out Luid logonId,
        out IntPtr token, out QuotaLimits quotas, out int subStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AllocateLocallyUniqueId(out Luid luid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LogonUserW")]
    static extern bool LogonUser(string userName, string? domain, string password, int logonType,
        int logonProvider, out IntPtr token);

    const int Logon32ProviderDefault = 0;

    // Win32 errors LogonUser reports; anything else is printed numerically.
    const int ErrorLogonFailure = 1326;
    const int ErrorAccountRestriction = 1327;
    const int ErrorInvalidLogonHours = 1328;
    const int ErrorInvalidWorkstation = 1329;
    const int ErrorPasswordExpired = 1330;
    const int ErrorAccountDisabled = 1331;
    const int ErrorLogonTypeNotGranted = 1385;
    const int ErrorPrivilegeNotHeld = 1314;
    const int ErrorAccountLockedOut = 1909;
    const int ErrorPasswordMustChange = 1907;

    /// <summary>
    /// Ground truth for "is the password we hold actually this account's password", using the
    /// documented Win32 API instead of our own hand-rolled <c>LsaLogonUser</c> call.
    ///
    /// This separation matters. The LSA probe below and this check fail for *different* reasons, and
    /// conflating them is how <c>btwu verify</c> used to mislead: it reported a rejected credential as
    /// "blob parsed successfully", even though a nonexistent user name produces the very same
    /// <c>STATUS_LOGON_FAILURE</c>. So:
    ///
    ///   LogonUser says no  -> the password is wrong. Re-enroll. The blob is not implicated.
    ///   LogonUser says yes while the LSA probe says no -> the password is fine and OUR packing is
    ///                                                     wrong. That is a bug in this file.
    ///
    /// <c>LOGON32_LOGON_NETWORK</c> is deliberate: it validates through the same MSV1_0 path as an
    /// unlock without needing SeTcbPrivilege, so it runs from an ordinary prompt. Do not switch this to
    /// <c>LOGON32_LOGON_INTERACTIVE</c> - that one requires SeTcbPrivilege and would fail with
    /// ERROR_PRIVILEGE_NOT_HELD (1314) for a perfectly good password.
    /// </summary>
    public static LogonUserResult CheckPassword(string domain, string user, string password)
    {
        var label = "LogonUser  LOGON32_LOGON_NETWORK";
        var ok = LogonUser(user, domain, password, LogonTypeNetwork, Logon32ProviderDefault, out var token);
        if (!ok)
            return new LogonUserResult(label, false, Marshal.GetLastWin32Error());

        if (token != IntPtr.Zero) CloseHandle(token);
        return new LogonUserResult(label, true, 0);
    }

    /// <summary>
    /// Asks Windows about the same password under <b>both</b> account name forms. 这是本项目里唯一应该存在的一份。
    ///
    /// 为什么必须两种都问：<c>ERROR_LOGON_FAILURE</c> 分不出「口令错」和「账户形式错」——
    /// Windows 把两种情况（以及「没有这个账户」）报成同一个错误码 0x52E。只有「A 被拒 / B 被接受」
    /// 这个**组合**才是结论：口令没问题，错的是形式。
    ///
    /// 2026-10-15 的教训：这条逻辑在 CLI 里写了两三份、**对话框那份漏了**。用户反馈「我输的就是
    /// 账户密码」，而他确实没输错——对话框只问了一种形式，于是只能说「口令被拒绝」，
    /// 既没告诉他形式可能不对，也没法自动改过来。所以现在集中在这里，三处调用同一份。
    /// </summary>
    /// <param name="user">当前使用的登录名。</param>
    /// <param name="domain">当前使用的域（<c>MicrosoftAccount</c> 表示 Microsoft 账户形式）。</param>
    /// <param name="password">已经拿到手里的口令 —— 只会用它问，不会存任何东西。</param>
    public static TwoFormProbe ProbeBothForms(string user, string domain, string password)
    {
        var primaryDomain = ToLogonDomain(domain);
        var primary = CheckPassword(primaryDomain, user, password);

        // 已经对了就不必再花一次登录尝试（每次失败都会计入账户的失败计数）。
        if (primary.Success) return new TwoFormProbe(primary, null, null, null);

        var isMsaForm = domain.Equals(AccountIdentity.MicrosoftAccountDomain, StringComparison.OrdinalIgnoreCase);

        // 备用的那一种形式，一律取自**交互会话的账户**（WTS），不是本进程的账户：
        // enroll 是提权子进程，Environment.UserName 是「回应 UAC 的那个账户」，而这里要的是
        // 「锁屏上要解锁的那个账户」。绝大多数时候两者相同，但不同的时候，错的答案就是一个
        // 静默失败的配置（2026-10-15 那位用户正是踩在这种「几乎总是对」的假设上）。
        var session = AccountIdentity.SessionAccount();
        var alternateUser = isMsaForm ? session.User : AccountIdentity.MicrosoftAccountSignInAddress();
        var alternateDomain = isMsaForm ? session.Domain : AccountIdentity.MicrosoftAccountDomain;

        if (string.IsNullOrEmpty(alternateUser) ||
            alternateUser.Equals(user, StringComparison.OrdinalIgnoreCase))
        {
            return new TwoFormProbe(primary, null, null, null);
        }

        return new TwoFormProbe(primary, alternateUser, alternateDomain,
            CheckPassword(alternateDomain, alternateUser, password));
    }

    /// <summary>
    /// 两种形式各试一遍后，把「试过什么、各自得到什么」拼成一行给用户看。
    ///
    /// 存在的理由是不许再出现「断言得比证据多」的句子：`0x52E` 分不出「口令错 / 账户名错 / 域错」，
    /// 所以界面只能说「这些都试过了，都被拒」，不能说「你的口令不对」。列出试过的形式，
    /// 用户一眼就能看出「哦，它没试我真正的那个账户」——那正是 2026-10-15 那次报障的关键信息。
    /// </summary>
    public static string DescribeAttempts(TwoFormProbe probe, string primaryUser, string primaryDomain)
    {
        var lines = new List<string>
        {
            $"· {primaryDomain}\\{primaryUser} → 0x{probe.Primary.Win32Error:X8}"
        };

        if (probe.Alternate is not null)
            lines.Add($"· {probe.AlternateDomain}\\{probe.AlternateUser} → 0x{probe.Alternate.Win32Error:X8}");
        else
            lines.Add("· （没有第二种可试的形式：本机只有这一个账户身份）");

        return string.Join("\n", lines);
    }

    /// <summary><c>.</c> or empty means the local machine. Kept here so every caller normalises the same way.</summary>
    public static string ToLogonDomain(string domain) =>
        domain is "." or "" ? Environment.MachineName : domain;

    /// <summary>Human-readable Win32 error from <see cref="CheckPassword"/>.</summary>
    public static string ExplainWin32(int error) => error switch
    {
        0 => "ERROR_SUCCESS",
        ErrorLogonFailure => "ERROR_LOGON_FAILURE - 账户名、域、口令三者至少有一个不对。"
            + "⚠️ Windows 把「口令错」「账户名不存在」「名字形式错」报成同一个码，"
            + "所以这一行证明不了是哪一种，别把它读成「口令错」。",
        ErrorAccountRestriction => "ERROR_ACCOUNT_RESTRICTION - 账户策略拒绝（例如空口令账户、登录时段限制）",
        ErrorInvalidLogonHours => "ERROR_INVALID_LOGON_HOURS - 不在允许的登录时段内",
        ErrorInvalidWorkstation => "ERROR_INVALID_WORKSTATION - 不允许从这台工作站登录",
        ErrorPasswordExpired => "ERROR_PASSWORD_EXPIRED - 口令已过期",
        ErrorAccountDisabled => "ERROR_ACCOUNT_DISABLED - 账户被禁用",
        ErrorAccountLockedOut => "ERROR_ACCOUNT_LOCKED_OUT - 账户已被锁定（失败次数过多）",
        ErrorPasswordMustChange => "ERROR_PASSWORD_MUST_CHANGE - 必须先改口令",
        ErrorLogonTypeNotGranted => "ERROR_LOGON_TYPE_NOT_GRANTED - 该账户不允许这种登录类型",
        ErrorPrivilegeNotHeld => "ERROR_PRIVILEGE_NOT_HELD - 缺少所需特权（换成 LOGON32_LOGON_NETWORK）",
        _ => "未分类的 Win32 错误",
    };

    /// <summary>
    /// Logs on with the given credential and reports the NTSTATUS. A successful call proves that the
    /// blob layout was parsed and that the password is accepted for this account.
    /// </summary>
    public static LogonProbeResult Run(string domain, string user, string password, int messageType,
        LogonBlobPacking packing, string packageName = "Negotiate", int logonType = LogonTypeNetwork)
    {
        var label = $"{packageName,-8} msg={messageType} type={logonType} packing={packing}";

        var connect = LsaConnectUntrusted(out var lsa);
        if (connect != 0)
            return new LogonProbeResult(label, connect, 0, "cannot reach the local security authority");

        IntPtr originPtr = IntPtr.Zero;
        IntPtr blob = IntPtr.Zero;
        IntPtr profile = IntPtr.Zero;
        var token = IntPtr.Zero;
        try
        {
            var packageBytes = Encoding.ASCII.GetBytes(packageName);
            var package = new LsaString
            {
                Length = (ushort)packageBytes.Length,
                MaximumLength = (ushort)(packageBytes.Length + 1),
                Buffer = Marshal.AllocHGlobal(packageBytes.Length + 1),
            };
            try
            {
                Marshal.Copy(packageBytes, 0, package.Buffer, packageBytes.Length);
                Marshal.WriteByte(package.Buffer, packageBytes.Length, 0);

                var lookup = LsaLookupAuthenticationPackage(lsa, ref package, out var packageId);
                if (lookup != 0)
                    return new LogonProbeResult(label, lookup, 0, $"authentication package '{packageName}' is unavailable");

                var originText = Encoding.ASCII.GetBytes("Btwu");
                originPtr = Marshal.AllocHGlobal(originText.Length + 1);
                Marshal.Copy(originText, 0, originPtr, originText.Length);
                Marshal.WriteByte(originPtr, originText.Length, 0);

                var origin = new LsaString
                {
                    Length = (ushort)originText.Length,
                    MaximumLength = (ushort)(originText.Length + 1),
                    Buffer = originPtr,
                };

                var source = new TokenSource { SourceName = new byte[8] };
                Encoding.ASCII.GetBytes("Btwu\0\0\0\0", 0, 4, source.SourceName, 0);
                AllocateLocallyUniqueId(out var sourceId);
                source.SourceIdentifier = sourceId;

                blob = BuildBlob(domain, user, password, messageType, packing, out var blobLength);

                var status = LsaLogonUser(lsa, ref origin, logonType, packageId, blob,
                    (uint)blobLength, IntPtr.Zero, ref source, out profile, out _, out _, out token, out _, out var subStatus);

                return new LogonProbeResult(label, status, subStatus, Explain(status));
            }
            finally
            {
                Marshal.FreeHGlobal(package.Buffer);
            }
        }
        finally
        {
            if (blob != IntPtr.Zero) Marshal.FreeHGlobal(blob);
            if (profile != IntPtr.Zero) LsaFreeReturnBuffer(profile);
            if (token != IntPtr.Zero) CloseHandle(token);
            if (originPtr != IntPtr.Zero) Marshal.FreeHGlobal(originPtr);
            LsaDeregisterLogonProcess(lsa);
        }
    }

    static IntPtr BuildBlob(string domain, string user, string password, int messageType,
        LogonBlobPacking packing, out int totalLength)
    {
        var domainBytes = Encoding.Unicode.GetBytes(domain);
        var userBytes = Encoding.Unicode.GetBytes(user);
        var passwordBytes = Encoding.Unicode.GetBytes(password);

        var header = Marshal.SizeOf<KerbInteractiveUnlockLogon>();
        totalLength = header + domainBytes.Length + userBytes.Length + passwordBytes.Length + 16;

        var buffer = Marshal.AllocHGlobal(totalLength);
        for (var i = 0; i < totalLength; i++) Marshal.WriteByte(buffer, i, 0);

        // Same order the Microsoft sample packs: domain, user, password.
        var domainOffset = header;
        var userOffset = domainOffset + domainBytes.Length;
        var passwordOffset = userOffset + userBytes.Length;

        var logon = new KerbInteractiveUnlockLogon
        {
            Logon = new KerbInteractiveLogon
            {
                MessageType = messageType,
                LogonDomainName = MakeString(buffer, domainOffset, domainBytes.Length, packing),
                UserName = MakeString(buffer, userOffset, userBytes.Length, packing),
                Password = MakeString(buffer, passwordOffset, passwordBytes.Length, packing),
            },
            // Left zero on purpose: Winlogon fills the unlock session id in.
            LogonId = new Luid(),
        };

        Marshal.StructureToPtr(logon, buffer, false);

        Marshal.Copy(domainBytes, 0, IntPtr.Add(buffer, domainOffset), domainBytes.Length);
        Marshal.Copy(userBytes, 0, IntPtr.Add(buffer, userOffset), userBytes.Length);
        Marshal.Copy(passwordBytes, 0, IntPtr.Add(buffer, passwordOffset), passwordBytes.Length);

        return buffer;
    }

    static UnicodeString MakeString(IntPtr blob, int offset, int byteLength, LogonBlobPacking packing) => new()
    {
        Length = (ushort)byteLength,
        // The packed form is not null-terminated, and MaximumLength equals Length there.
        MaximumLength = (ushort)(packing == LogonBlobPacking.ByteOffsets ? byteLength : byteLength + sizeof(char)),
        Buffer = packing == LogonBlobPacking.ByteOffsets
            ? new IntPtr(offset)
            : IntPtr.Add(blob, offset),
    };

    // Wording matters here. A rejected credential must NOT be reported as "the blob parsed
    // successfully": measured on Windows 11 25H2, a user name that does not exist at all returns the
    // very same STATUS_LOGON_FAILURE with substatus 0. MSV1_0 collapses "wrong password", "no such
    // account" and "logon right missing" into one code over network logon, so this function must not
    // claim more than the code actually proves. Whether the password is right is answered by
    // CheckPassword, and only there.
    public static string Explain(uint status) => status switch
    {
        StatusSuccess => "STATUS_SUCCESS - 凭据被接受",
        StatusNoSuchUser => "STATUS_NO_SUCH_USER - 用户名不存在（这个码才真的区分了账户）",
        StatusWrongPassword => "STATUS_WRONG_PASSWORD - 口令错误",
        StatusLogonFailure => "STATUS_LOGON_FAILURE - 被拒，但**不说明原因**：口令错、账户不存在、"
            + "无网络登录权限都会返回它，且 substatus 为 0。别据此判断打包或口令。",
        StatusInternalError => "STATUS_INTERNAL_ERROR - 凭据没有被当成「口令错误」处理：LSA/MSV1_0 "
            + "在处理这份提交时内部就失败了。典型成因是登录名形式解析不到真正要解锁的主体，"
            + "或消息类型与该场景不符。**它不是说口令错了。**",
        StatusNoSuchPackage => "STATUS_NO_SUCH_PACKAGE - 认证包名查不到（见 Msv1_0PackageName 的注释）",
        StatusInvalidParameter => "STATUS_INVALID_PARAMETER - blob 被判为畸形（打包格式很可能错）",
        StatusNoLogonServers => "STATUS_NO_LOGON_SERVERS - 缺少 Kerberos 上下文（unlock 消息类型在探针里正常会这样）",
        StatusAccountRestriction => "STATUS_ACCOUNT_RESTRICTION - 账户策略拒绝，例如空口令账户不允许网络登录",
        StatusInvalidLogonHours => "STATUS_INVALID_LOGON_HOURS - 不在允许的登录时段内",
        StatusInvalidWorkstation => "STATUS_INVALID_WORKSTATION - 不允许从这台工作站登录",
        StatusPasswordExpired => "STATUS_PASSWORD_EXPIRED - 口令已过期",
        StatusAccountDisabled => "STATUS_ACCOUNT_DISABLED - 账户被禁用",
        StatusAccountLockedOut => "STATUS_ACCOUNT_LOCKED_OUT - 账户已被锁定（失败次数过多）",
        StatusPasswordMustChange => "STATUS_PASSWORD_MUST_CHANGE - 必须先改口令",
        _ => "未分类",
    };
}
