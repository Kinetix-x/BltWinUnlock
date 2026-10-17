using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Btwu.Core;

/// <summary>What Windows itself says about the current account's internet identity (NetUserGetInfo level 24).</summary>
/// <param name="IsInternetIdentity">
/// <c>true</c> = the account really is an internet identity (Microsoft account / Entra ID);
/// <c>false</c> = a plain local SAM account; <c>null</c> = the call failed and we do not know.
/// </param>
/// <param name="ProviderName"><c>MicrosoftAccount</c> for a Microsoft account; empty for a local one.</param>
/// <param name="PrincipalName">The identity's own name (the sign-in address for a Microsoft account).</param>
public sealed record InternetIdentityInfo(bool? IsInternetIdentity, string? ProviderName, string? PrincipalName);

/// <summary>
/// Works out which *form* of the signed-in account a stored credential should use.
///
/// This matters far more than it looks. A Windows account comes in two shapes, and they are logged on
/// through different names:
///
///   Local account       ->  <c>.\alice</c> or <c>DESKTOP-A1B2C3\alice</c>       (the SAM account)
///   Microsoft account   ->  <c>MicrosoftAccount\alice@example.com</c>       (the MSA identity)
///
/// For a Microsoft account the local SAM entry still exists and still has a name, but it is not the
/// principal that owns the password: Windows manages that account's password itself, which is why
/// <c>net user alice</c> reports <c>Password required: No</c>. Replaying the Microsoft account
/// password against the SAM name therefore fails with STATUS_LOGON_FAILURE, and - because MSV1_0
/// collapses "wrong password" and "no such account" into the same status - it fails *silently*, with
/// no way to tell the two apart from the outside. Measured on the development machine:
///
///   DESKTOP-A1B2C3\alice                     -> S-1-5-21-...-1001                  (local SAM)
///   MicrosoftAccount\alice@example.com       -> S-1-11-96-3623454863-...          (MSA identity)
///   alice@example.com                        -> S-1-5-21-...-1001                  (alias, same as SAM)
///
/// Two different SIDs for two different principals - so the form is not cosmetic.
///
/// <b>How the form is decided (2026-10-15, fixed after a user report).</b> The first version read the
/// sign-in address out of <c>HKCU\...\IdentityCRL\UserExtendedProperties</c> and, when that key held
/// exactly one subkey, used it unconditionally. That is wrong: the key records the Microsoft account
/// the *user* has signed in to Store / Office / Edge with, and it can perfectly well exist on a machine
/// whose Windows login is a plain local account. A user reported exactly that ("我输的就是账户密码，
/// 它偏说不对"): his Windows account was a **local** account, the registry held an unrelated consumer
/// mail address, so Btwu prefilled <c>MicrosoftAccount\&lt;that address&gt;</c>, replayed it at the lock
/// screen, and got the same <c>STATUS_LOGON_FAILURE</c> any wrong name or password produces.
///
/// So the registry is now only a *lead*, never the verdict. The verdict comes from Windows:
/// <c>NetUserGetInfo(level 24).usri24_internet_identity</c> - the same source <c>Get-LocalUser</c>
/// shows as <c>PrincipalSource</c>, so a user can check our answer with one command. When Windows says
/// the account is <b>not</b> an internet identity, the logon form is the local SAM form, full stop.
///
/// So the registry is now only a *lead*, never the verdict. The verdict comes from Windows:
/// <c>NetUserGetInfo(level 24).usri24_internet_identity</c> - the same source <c>Get-LocalUser</c>
/// shows as <c>PrincipalSource</c>, so a user can check our answer with one command. When Windows says
/// the account is <b>not</b> an internet identity, the logon form is the local SAM form, full stop.
/// </summary>
public static class AccountIdentity
{
    /// <summary>The domain component Windows uses for a Microsoft account logon name.</summary>
    public const string MicrosoftAccountDomain = "MicrosoftAccount";

    /// <summary>
    /// Location of the per-user Microsoft account identity cache. The subkey *name* is the account's
    /// sign-in address (the email, or the phone-number form). Public so <c>btwu enroll</c> can show the
    /// user where the address it chose came from.
    ///
    /// ⚠️ 这只是**线索**：它记的是「本机这个用户拿哪个微软账户登录过 Store / Office / Edge」，
    /// 与本机的 Windows 登录身份是两件事（见类注释里那位用户的例子）。
    /// </summary>
    public const string IdentityRegistryKey = @"HKCU\Software\Microsoft\IdentityCRL\UserExtendedProperties";

    const string IdentityCrlKey = @"Software\Microsoft\IdentityCRL\UserExtendedProperties";

    // ------------------------------------------------------------ Windows 自己的答案

    const int UserInfoLevelInternetIdentity = 24;

    [StructLayout(LayoutKind.Sequential)]
    struct UserInfo24
    {
        public int InternetIdentity;      // BOOL
        public uint Flags;
        public IntPtr InternetProviderName;
        public IntPtr InternetPrincipalName;
        public IntPtr UserSid;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    static extern int NetUserGetInfo(string? serverName, string userName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>
    /// Asks Windows whether <paramref name="userName"/> (the current session's user by default) is an
    /// internet identity, instead of guessing from the registry.
    ///
    /// Never throws and never guesses: a failure to answer comes back as
    /// <see cref="InternetIdentityInfo.IsInternetIdentity"/> = <c>null</c> ("unknown"), and callers
    /// keep the older registry behaviour in that case. "Unknown" must not be reported as "local" -
    /// that would silently downgrade a real Microsoft account.
    /// </summary>
    public static InternetIdentityInfo InternetIdentity(string? userName = null, string? serverName = null)
    {
        userName ??= SessionAccount().User;
        if (string.IsNullOrWhiteSpace(userName)) return new InternetIdentityInfo(null, null, null);

        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (NetUserGetInfo(serverName, userName, UserInfoLevelInternetIdentity, out buffer) != 0
                || buffer == IntPtr.Zero)
            {
                return new InternetIdentityInfo(null, null, null);
            }

            var info = Marshal.PtrToStructure<UserInfo24>(buffer);
            return new InternetIdentityInfo(
                info.InternetIdentity != 0,
                PtrString(info.InternetProviderName),
                PtrString(info.InternetPrincipalName));
        }
        catch
        {
            return new InternetIdentityInfo(null, null, null);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                try { NetApiBufferFree(buffer); } catch { /* nothing useful to do */ }
            }
        }
    }

    static string? PtrString(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr);

    // --------------------------------------------------- 交互会话的账户（不受提权影响）

    const int WtsUserName = 5;
    const int WtsDomainName = 7;
    static readonly IntPtr WtsCurrentSession = new(-1);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WTSQuerySessionInformationW(IntPtr server, IntPtr sessionId, int infoClass,
        out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>
    /// The account of the <b>interactive session</b>, not of this process.
    ///
    /// The distinction is the whole point: <c>enroll</c> runs elevated and in its own process (see
    /// EnrollForm), so <see cref="Environment.UserName"/> is the account that answered the UAC prompt -
    /// which is the same account in the ordinary case but not always, and "not always" is precisely
    /// when a wrong answer costs a silent lock-screen failure. The credential provider reads the
    /// session user the same way (<c>PresenceState.h::QuerySessionUser</c>), so both sides now agree.
    ///
    /// Same shape as the C++ helper on purpose: user name plus session domain, which for a local
    /// account is the machine name.
    /// </summary>
    public static (string User, string Domain) SessionAccount()
    {
        var user = QueryWtsSessionString(WtsUserName);
        var domain = QueryWtsSessionString(WtsDomainName);

        // Falling back to the process' own account is right here: it is what the old code used
        // everywhere, so a machine where WTS fails behaves exactly as before rather than worse.
        if (string.IsNullOrWhiteSpace(user)) user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(domain)) domain = Environment.MachineName;
        return (user!, domain!);
    }

    static string? QueryWtsSessionString(int infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, WtsCurrentSession, infoClass, out buffer, out _)
                || buffer == IntPtr.Zero)
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                try { WTSFreeMemory(buffer); } catch { /* nothing useful to do */ }
            }
        }
    }

    // ------------------------------------------------------------------- 形式的判定

    /// <summary>
    /// The Microsoft account sign-in address for the current user, or <c>null</c> when this is a
    /// plain local account. Never throws.
    ///
    /// 判据顺序（见类注释）：Windows 的 <c>NetUserGetInfo(24)</c> 说了算 ——
    /// 它说「不是 internet identity」就直接返回 <c>null</c>（本地账户形式），注册表里的邮箱一概不看；
    /// 它说「是」或「查不出来」才去读注册表，并优先取 Windows 自己记的那个 principal 名。
    /// </summary>
    public static string? MicrosoftAccountSignInAddress()
    {
        var identity = InternetIdentity();

        // ★ 就是这一行缺失导致那位用户录错了形式。IdentityCRL 里的邮箱可能是任何一次
        //   Store/Office/Edge 登录留下的，它证明不了本机的 Windows 登录身份。
        if (identity.IsInternetIdentity == false) return null;

        var ordered = new List<string>();
        if (!string.IsNullOrWhiteSpace(identity.PrincipalName)) ordered.Add(identity.PrincipalName!);
        foreach (var candidate in RegistryIdentityCandidates())
        {
            if (!ordered.Contains(candidate, StringComparer.OrdinalIgnoreCase)) ordered.Add(candidate);
        }

        if (ordered.Count <= 1) return ordered.Count == 0 ? null : ordered[0];

        // 多个候选时，挑「别名能解析回当前用户」的那一个 —— 那才是会话真正登录的账户。
        var currentSid = CurrentUserSid();
        if (currentSid is not null)
        {
            foreach (var candidate in ordered)
            {
                var aliasSid = TryResolveSid(null, candidate) ?? TryResolveSid(Environment.MachineName, candidate);
                if (aliasSid == currentSid) return candidate;
            }
        }

        return ordered[0];
    }

    /// <summary>
    /// The sign-in addresses recorded in <see cref="IdentityRegistryKey"/>, in registry order.
    /// Pure "what is in the registry", no judgement - callers decide what it means.
    /// </summary>
    public static List<string> RegistryIdentityCandidates()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(IdentityCrlKey);
            if (key is null) return new List<string>();
            return key.GetSubKeyNames().Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// The misleading-registry case in one sentence, or <c>null</c> when there is nothing to warn
    /// about: a Microsoft address is registered in this user's hive, but Windows says the account is a
    /// local one. Printed by <c>enroll</c>, the enroll dialog and <c>doctor</c>, because the natural
    /// (and wrong) conclusion is "then I must type that email".
    /// </summary>
    public static string? StaleIdentityWarning()
    {
        var identity = InternetIdentity();
        if (identity.IsInternetIdentity != false) return null;

        var candidates = RegistryIdentityCandidates();
        if (candidates.Count == 0) return null;

        return $"{string.Join("、", candidates)} 这个微软账户登记在本机（{IdentityRegistryKey}），"
             + "但 Windows 说当前账户不是 internet identity —— 那只是某个应用（Store / Office / Edge）的"
             + "登录记录，不是 Windows 登录身份。请照上面那个账户填。";
    }

    /// <summary>
    /// The <c>(user, domain)</c> pair to store for the current session's account: the Microsoft
    /// account identity when Windows says this account is one, otherwise the local SAM form.
    /// </summary>
    public static (string User, string Domain) PreferredLogonForm()
    {
        var signIn = MicrosoftAccountSignInAddress();
        if (signIn is not null) return (signIn, MicrosoftAccountDomain);

        var session = SessionAccount();
        return (session.User, ".");
    }

    /// <summary>Human-readable description of what a stored credential will be replayed as.</summary>
    public static string Describe(string user, string domain) =>
        domain.Equals(MicrosoftAccountDomain, StringComparison.OrdinalIgnoreCase)
            ? $"Microsoft 账户 {user}（域组件 '{MicrosoftAccountDomain}'）"
            : $"本地账户 {user}（域 '{domain}'，即 {Environment.MachineName}）";

    /// <summary>
    /// True when <paramref name="user"/>/<paramref name="domain"/> is the Microsoft-account form while
    /// the session's account is a local one - i.e. exactly the combination that can never authenticate.
    /// <c>doctor</c> and <c>status</c> use it to turn a silent lock-screen failure into a named problem.
    /// </summary>
    /// <param name="sessionUser">
    /// 默认取交互会话的账户。可显式指定，唯一的目的就是让这条判据**能被测试**：
    /// 本机是 Microsoft 账户，所以「本机账户其实是本地账户」这一支在本机只可能通过
    /// 传一个本地账户名（例如内置的 <c>Administrator</c>）才走到。
    /// </param>
    public static bool IsImpossibleMsaForm(string user, string domain, string? sessionUser = null)
    {
        if (!domain.Equals(MicrosoftAccountDomain, StringComparison.OrdinalIgnoreCase)) return false;
        return InternetIdentity(sessionUser).IsInternetIdentity == false;
    }

    /// <summary>
    /// True when this <c>(user, domain)</c> pair is the account the interactive session is actually
    /// signed in as - the local SAM name, or the Microsoft account sign-in address of that same user.
    ///
    /// 为什么要问这个：锁屏要解锁的是**会话的那个主体**，而 `LogonUser` 只要账户名 + 口令对就返回成功。
    /// 所以「校验通过」只证明了「这是一份有效凭据」，没证明「这是本机这个人的凭据」——
    /// 输成另一个账户（比如顺手输了自己在别的机器/别的账户上的口令）会安静地存下去，
    /// 而它在锁屏上永远失败、且不报错。2026-10-15 那份用户排查记录里的第 4 条建议指的就是这个洞。
    /// </summary>
    public static bool IsSessionAccount(string user, string domain)
    {
        if (string.IsNullOrWhiteSpace(user)) return false;
        var session = SessionAccount();

        if (domain.Equals(MicrosoftAccountDomain, StringComparison.OrdinalIgnoreCase))
        {
            var signIn = MicrosoftAccountSignInAddress();
            return signIn is not null && user.Equals(signIn, StringComparison.OrdinalIgnoreCase);
        }

        // 本机账户：域写 "."、机器名、或者干脆留空，都算同一个主体。
        var domainIsLocal = domain is "." or ""
            || domain.Equals(session.Domain, StringComparison.OrdinalIgnoreCase)
            || domain.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        return domainIsLocal && user.Equals(session.User, StringComparison.OrdinalIgnoreCase);
    }

    static string? CurrentUserSid()
    {
        try
        {
            return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        }
        catch
        {
            return null;
        }
    }

    static string? TryResolveSid(string? domain, string user)
    {
        try
        {
            var account = string.IsNullOrEmpty(domain)
                ? new System.Security.Principal.NTAccount(user)
                : new System.Security.Principal.NTAccount(domain, user);
            return ((System.Security.Principal.SecurityIdentifier)account
                .Translate(typeof(System.Security.Principal.SecurityIdentifier))).Value;
        }
        catch
        {
            return null;
        }
    }
}
