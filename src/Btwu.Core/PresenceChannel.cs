using System.Globalization;
using System.Text;

namespace Btwu.Core;

/// <summary>
/// The one-line state file the agent writes and the credential provider (running as SYSTEM inside
/// LogonUI) reads. Format, space separated:
/// <c>BTWU1 &lt;present&gt; &lt;sinceTicks&gt; &lt;heartbeatTicks&gt; &lt;suppressUntilTicks&gt; &lt;autoUnlock&gt; &lt;unlockWindowSeconds&gt;</c>
/// Ticks are UTC DateTime ticks. A missing or unparsable file means "absent" - fail closed.
///
/// <c>present</c> answers "is a trigger device connected RIGHT NOW", with NO grace period. It is the
/// bit the provider turns into "may I offer the stored password", so a disconnect has to show up in
/// it immediately - debouncing it here would mean the lock screen keeps unlocking itself, with no
/// keyboard in the room, for as long as the debounce lasts. (2026-10-14: it was debounced by
/// GraceSeconds and did exactly that.) The grace period still exists, but only inside
/// <see cref="PresenceMonitor"/> where it delays the *lock* decision, which is the question it was
/// written for.
///
/// The C++ side mirrors this in PresenceState.h; both parsers must stay in sync.
/// </summary>
public sealed class PresenceSnapshot
{
    public const string Header = "BTWU1";

    /// <summary>A trigger device is connected right now. Undebounced - see the type summary.</summary>
    public bool Present { get; init; }
    public long SinceTicks { get; init; }
    public long HeartbeatTicks { get; init; }
    public long SuppressUntilTicks { get; init; }
    public bool AutoUnlock { get; init; } = true;
    public int UnlockWindowSeconds { get; init; }

    public static PresenceSnapshot Absent(long nowTicks) => new()
    {
        Present = false,
        SinceTicks = 0,
        HeartbeatTicks = nowTicks,
        SuppressUntilTicks = 0,
        AutoUnlock = false,
    };
}

public static class PresenceChannel
{
    /// <summary>State older than this is ignored by the credential provider.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Publishes the state the credential provider reads. <b>Never throws.</b> A publish failure must
    /// not be able to stop the caller from trying again, because "not publishing" is indistinguishable
    /// from "the user is away": the heartbeat ages out, the provider returns zero credentials, and the
    /// unlock tile silently disappears while the process still looks alive. (2026-10-14: one
    /// <c>UnauthorizedAccessException</c> here killed the heartbeat loop for the rest of the session
    /// and the feature stayed dead for two hours without anything looking wrong.)
    ///
    /// Written via a temp file plus a replace, so a reader never sees a half-written line. Both halves
    /// of that are load-bearing:
    ///
    ///   * <b>the temp name carries the pid</b>, because two agents can run at once (the installer
    ///     starts the scheduled task while a tray instance is already up). With one shared temp name
    ///     the two writers failed each other's <c>File.Move</c> with ACCESS_DENIED.
    ///   * <b>the replace is retried, then falls back to an in-place overwrite.</b> <c>MoveFileEx</c>
    ///     with REPLACE_EXISTING needs delete access to both files, so any transient holder - the other
    ///     agent, a reader that only asked for FILE_SHARE_READ, or an indexer/AV scan of the file we
    ///     just created - turns it into ERROR_ACCESS_DENIED. An in-place write needs write access only,
    ///     so the state still gets out. A reader can then briefly observe a torn line; the parser treats
    ///     an unparsable file as "absent" and the next heartbeat repairs it within seconds, which is the
    ///     right trade against losing the feature outright.
    /// </summary>
    /// <returns><c>true</c> when the state reached disk.</returns>
    public static bool Write(PresenceSnapshot snapshot)
    {
        try { Paths.EnsureDataDir(); } catch { /* the write below reports the real failure */ }

        var text = string.Create(CultureInfo.InvariantCulture,
            $"{PresenceSnapshot.Header} {(snapshot.Present ? 1 : 0)} {snapshot.SinceTicks} {snapshot.HeartbeatTicks} " +
            $"{snapshot.SuppressUntilTicks} {(snapshot.AutoUnlock ? 1 : 0)} {snapshot.UnlockWindowSeconds}\n");

        // Unique per process AND per thread. A shared temp name is not merely inefficient: two writers
        // racing on it can lose each other's source file mid-replace, which is what made the destination
        // disappear during contention (tools/statetest, phase 1). One writer, one temp file.
        var tmp = string.Create(CultureInfo.InvariantCulture,
            $"{Paths.PresenceFile}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp");

        for (var attempt = 1; attempt <= 3; ++attempt)
        {
            try
            {
                File.WriteAllText(tmp, text);
                File.Move(tmp, Paths.PresenceFile, overwrite: true);

                // Best effort: sweep the shared name the pre-2026-10-14 build used, so an orphan from
                // an older agent does not linger in the data directory forever.
                try { File.Delete(Paths.PresenceFile + ".tmp"); } catch { }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 3) break;
                Thread.Sleep(15 * attempt);
            }
        }

        // Fallback: overwrite in place. Needs no delete access and no replace, so it survives every
        // holder that can block the atomic path.
        try
        {
            using var stream = new FileStream(Paths.PresenceFile, FileMode.Create, FileAccess.Write,
                                              FileShare.ReadWrite | FileShare.Delete);
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>
    /// Best-effort sweep of temp files left behind by a process killed mid-publish. Called once at
    /// startup rather than on the hot path: an orphan only appears when something dies between creating
    /// its temp file and consuming it, and sweeping on every write would cost IO for nothing.
    ///
    /// Only deletes files older than a few minutes, so a *second* agent starting up cannot pull the
    /// temp file out from under a first one that is mid-publish right now.
    /// </summary>
    public static void CleanupStaleTempFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
            foreach (var pattern in new[] { "presence.dat.tmp", "presence.dat.*.tmp" })
            {
                foreach (var stale in Directory.GetFiles(Paths.DataDir, pattern))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(stale) < cutoff) File.Delete(stale);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public static bool TryRead(out PresenceSnapshot snapshot)
    {
        snapshot = PresenceSnapshot.Absent(DateTime.UtcNow.Ticks);
        try
        {
            if (!File.Exists(Paths.PresenceFile)) return false;

            // ReadWrite | Delete on purpose. File.ReadAllText would take the file with FILE_SHARE_READ
            // only, which denies delete - and the agent publishes by replacing the file, so our own
            // diagnostic commands (`btwu status`, `btwu monitor`, `harness --live`) could block a
            // heartbeat and turn it into an ACCESS_DENIED. The C++ reader in PresenceState.h already
            // requests all three shares; this keeps the two sides symmetric.
            string raw;
            using (var stream = new FileStream(Paths.PresenceFile, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
                raw = reader.ReadToEnd();

            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || parts[0] != PresenceSnapshot.Header) return false;
            snapshot = new PresenceSnapshot
            {
                Present = parts[1] == "1",
                SinceTicks = long.Parse(parts[2], CultureInfo.InvariantCulture),
                HeartbeatTicks = long.Parse(parts[3], CultureInfo.InvariantCulture),
                SuppressUntilTicks = long.Parse(parts[4], CultureInfo.InvariantCulture),
                AutoUnlock = parts.Length > 5 && parts[5] == "1",
                UnlockWindowSeconds = parts.Length > 6 ? int.Parse(parts[6], CultureInfo.InvariantCulture) : 0,
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Mirrors the credential provider's decision, so `btwu status` (and the management panel) can
    /// explain itself.
    ///
    /// The wording is Chinese because it is shown to whoever is looking at the tool - `btwu gui`'s
    /// status page and `btwu status` both print it verbatim, and a release build should not have one
    /// English sentence in the middle of a Chinese window. The C++ side keeps its own short tags in
    /// `credprov.log` (see PresenceState.h) and the two are deliberately not kept in step: these are
    /// sentences for a person, those are labels for a log. Nothing asserts either.
    /// </summary>
    public static (bool unlock, string reason) Evaluate(PresenceSnapshot s, DateTime nowUtc)
    {
        if (!s.Present) return (false, "触发设备此刻没连着");
        if (!s.AutoUnlock) return (false, "配置里关掉了自动解锁");
        if (nowUtc.Ticks - s.HeartbeatTicks > MaxAge.Ticks) return (false, "状态文件过期（agent 没在运行？）");
        if (nowUtc.Ticks < s.SuppressUntilTicks)
            return (false, $"抑制中，到 {new DateTime(s.SuppressUntilTicks, DateTimeKind.Utc).ToLocalTime():HH:mm:ss}");
        if (s.UnlockWindowSeconds > 0 && nowUtc.Ticks - s.SinceTicks > TimeSpan.FromSeconds(s.UnlockWindowSeconds).Ticks)
            return (false, $"在场时间已超过 {s.UnlockWindowSeconds} 秒（解锁窗口策略）");
        return (true, "触发设备在场，且状态是新鲜的");
    }

    public static long ReadSuppressUntil()
    {
        try
        {
            if (!File.Exists(Paths.PauseFile)) return 0;
            return long.TryParse(File.ReadAllText(Paths.PauseFile).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Deadline (same .NET-tick unit as <see cref="ReadSuppressUntil"/>) before which the agent must
    /// not lock on absence because an unlock just happened; 0 when there is no live dwell time.
    ///
    /// The provider writes it when *it* completes an unlock. The agent writes it when Windows reports
    /// an unlock that went through a built-in provider - one source of truth for the lock side, two
    /// writers, because only one of them can observe each kind of unlock.
    /// </summary>
    public static long ReadUnlockCooldownUntil()
    {
        try
        {
            if (!File.Exists(Paths.UnlockCooldownFile)) return 0;
            return long.TryParse(File.ReadAllText(Paths.UnlockCooldownFile).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// How long the agent refuses to lock after an unlock it observed itself (a PIN or Windows Hello
    /// unlock produces no provider-side file).
    ///
    /// MUST MATCH <c>Btwu::kUnlockCooldownSeconds</c> in PresenceState.h. Two languages, one number:
    /// this side only ever writes the deadline for unlocks the provider cannot see, and a mismatch
    /// changes how long the armistice lasts, never the format of the file. Keep both at 90 s.
    /// </summary>
    public static readonly TimeSpan PostUnlockDwell = TimeSpan.FromSeconds(90);
}
