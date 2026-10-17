namespace Btwu.Core;

/// <summary>
/// Everything the agent, the CLI and the credential provider agree on lives under
/// <c>%ProgramData%\BtwuUnlock</c>.
///
/// ACL layout (applied by scripts/install.ps1):
///   * the directory itself is writable by the interactive user, because the agent runs unprivileged
///     and has to publish presence.dat / config.json / agent.log;
///   * cred.bin carries an explicit, inheritance-blocked DACL holding SYSTEM + Administrators only,
///     so no other local account can read the stored password.
/// </summary>
public static class Paths
{
    /// <summary>
    /// <c>%ProgramData%\BtwuUnlock</c>, unless <c>BTWU_DATA_DIR</c> points somewhere else.
    ///
    /// The override exists so a test or a diagnostic can drive the real publish path against a scratch
    /// directory instead of the live state file. The credential provider deliberately does NOT honour it
    /// (see PresenceState.h): it always reads the fixed location, so a redirected agent can only ever
    /// fail closed - it cannot make LogonUI look somewhere an attacker chose.
    /// </summary>
    public static string DataDir { get; } = ResolveDataDir();

    static string ResolveDataDir()
    {
        var overridden = Environment.GetEnvironmentVariable("BTWU_DATA_DIR");
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BtwuUnlock")
            : Path.GetFullPath(overridden);
    }

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string CredentialFile => Path.Combine(DataDir, "cred.bin");
    public static string PresenceFile => Path.Combine(DataDir, "presence.dat");
    public static string PauseFile => Path.Combine(DataDir, "pause.until");

    /// <summary>
    /// Written by the credential provider the instant it completes an unlock: a .NET-tick deadline
    /// before which the agent must not lock on absence.
    ///
    /// A keyboard that just unlocked this machine is present by definition, and a BLE keyboard's idle
    /// disconnect is indistinguishable from walking away with it - so without this dwell time the
    /// agent locks the screen it was just asked to unlock. The presence decision is taken normally
    /// again once the deadline passes (see <c>Btwu::kUnlockCooldownSeconds</c>).
    /// </summary>
    public static string UnlockCooldownFile => Path.Combine(DataDir, "unlocked.until");
    public static string LogFile => Path.Combine(DataDir, "agent.log");

    /// <summary>
    /// The credential provider's kill switch. Its existence alone makes the provider report zero
    /// credentials in every scenario, without an uninstall. Read by the DLL directly (see
    /// PresenceState.h), so it does not depend on the agent still running - which matters, because
    /// stopping the agent is the first thing anyone does when the lock screen misbehaves.
    /// </summary>
    public static string DisableCpFile => Path.Combine(DataDir, "disable.cp");

    /// <summary>
    /// The "submit without a keystroke" marker. Present = the provider offers its credential as an
    /// auto-logon, so LogonUI submits it the moment it enumerates and a manual Win+L bounces straight
    /// back to the desktop. Absent (the default) = the credential is only the *selected tile*, the lock
    /// screen keeps its own UI, and the user presses Enter to submit.
    ///
    /// A marker file rather than a config.json key on purpose: config.json is rewritten wholesale by
    /// `btwu enroll`, so a safety-relevant switch living there would vanish on the next re-enrol. It is
    /// also read by the DLL directly (see PresenceState.h), so it works with the agent stopped.
    /// </summary>
    public static string AutoSubmitFile => Path.Combine(DataDir, "autosubmit");

    /// <summary>Written by the credential provider; the only diagnostic channel from inside LogonUI.</summary>
    public static string CredProvLogFile => Path.Combine(DataDir, "credprov.log");

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}
