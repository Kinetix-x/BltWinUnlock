using System.Globalization;
using System.Runtime.InteropServices;
using Btwu.Core;
using Microsoft.Win32;

namespace Btwu.Agent;

/// <summary>
/// The resident half of the product: watches the trigger devices, publishes presence.dat for the
/// credential provider, and locks the workstation when the device walks away.
///
/// Shared by <c>btwu run</c> (headless, what the scheduled task normally starts) and <c>btwu tray</c>
/// (same engine with a notification-area UI) so both modes behave identically.
/// </summary>
sealed class PresenceAgent : IAsyncDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool LockWorkStation();

    readonly AppConfig _cfg;
    readonly CancellationTokenSource _cts = new();
    long _startedTick;
    bool _lockArmed;
    Task? _heartbeat;

    /// <summary>Everything the published state carries except the heartbeat timestamp itself.</summary>
    (bool Present, long Since, bool AutoUnlock, int Window, long SuppressUntil)? _lastSignature;

    public PresenceAgent(AppConfig cfg)
    {
        _cfg = cfg;
        Monitor = new PresenceMonitor(cfg);
        _lockArmed = !cfg.RequireSeenPresentBeforeLocking;
        Monitor.PresenceChanged += OnPresenceChanged;
    }

    public PresenceMonitor Monitor { get; }

    /// <summary>Raised whenever presence or the published state changes; used by the tray to repaint.</summary>
    public event Action? Changed;

    /// <summary>
    /// For the tray: the debounced "is the user here" notion, which is what the arm state is about,
    /// and stable enough not to flicker every time the keyboard naps.
    ///
    /// Deliberately NOT the published value - see <see cref="HeartbeatAsync"/>, which publishes
    /// <see cref="PresenceMonitor.RawConnected"/> because the credential provider must see a
    /// disconnect immediately.
    /// </summary>
    public bool Present => Monitor.Present;

    public async Task StartAsync()
    {
        _startedTick = Environment.TickCount64;

        // Sweep temp files a previous, hard-killed process left behind. Config-time only: the publish
        // path is not the place to spend IO on housekeeping.
        PresenceChannel.CleanupStaleTempFiles();

        await Monitor.StartAsync();
        _heartbeat = Task.Run(() => HeartbeatAsync(_cts.Token));

        // Unlocks that go through PIN or Windows Hello never reach the credential provider, so the only
        // way to learn about them is this event. Without a message pump it may never fire (SystemEvents
        // creates its own window thread, but this is not contractual), which is why the provider also
        // writes the same file - between the two, every unlock is covered in tray mode, and the
        // provider-driven one is covered in headless mode too.
        try
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            Log.Info("session events: listening for unlocks (post-unlock dwell)");
        }
        catch (Exception ex)
        {
            Log.Warn($"session events unavailable ({ex.Message}) - post-unlock dwell covers " +
                     "provider-driven unlocks only");
        }

        Log.Info($"agent running (pid {Environment.ProcessId}), autoUnlock={_cfg.AutoUnlock}, " +
                 $"unlockWindow={_cfg.UnlockWindowSeconds}s");
    }

    void OnPresenceChanged(bool present)
    {
        if (present) _lockArmed = true;

        if (present || !_lockArmed || !_cfg.LockOnAbsence)
        {
            Changed?.Invoke();
            return;
        }

        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64 - _startedTick);
        if (uptime < TimeSpan.FromSeconds(_cfg.StartupGraceSeconds))
        {
            Log.Info($"absence ignored - still inside startup grace ({uptime.TotalSeconds:F0}s)");
            Changed?.Invoke();
            return;
        }

        // An unlock happened moments ago, which means the keyboard was right here. A BLE keyboard that
        // just went quiet is asleep, not gone - and locking now would lock the screen the user was just
        // let into. Both sources of "an unlock just happened" land in the same file: the provider writes
        // it when it completes an unlock itself, and this agent writes it when Windows reports one that
        // went through a built-in provider (PIN, Windows Hello).
        var remaining = PresenceChannel.ReadUnlockCooldownUntil() - DateTime.UtcNow.Ticks;
        if (remaining > 0)
        {
            Log.Info($"absence ignored - inside the post-unlock dwell " +
                     $"({remaining / TimeSpan.TicksPerSecond}s left)");
            Changed?.Invoke();
            return;
        }

        Log.Info("trigger device gone -> locking workstation");
        if (!LockWorkStation()) Log.Warn($"LockWorkStation failed: {Marshal.GetLastWin32Error()}");
        Changed?.Invoke();
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock) return;

        // A manual unlock leaves nothing behind in the data directory, so record the dwell here too,
        // with the same meaning and the same file the provider uses.
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Paths.UnlockCooldownFile,
                (DateTime.UtcNow.Ticks + PresenceChannel.PostUnlockDwell.Ticks)
                    .ToString(CultureInfo.InvariantCulture));
            Log.Info($"session unlocked -> post-unlock dwell " +
                     $"({PresenceChannel.PostUnlockDwell.TotalSeconds:F0}s)");
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot publish the post-unlock dwell: {ex.Message}");
        }
    }

    async Task HeartbeatAsync(CancellationToken ct)
    {
        // How often the published state has to be re-stamped so the credential provider keeps
        // believing the agent is alive. Must stay comfortably under PresenceChannel.MaxAge.
        var keepalive = TimeSpan.FromMilliseconds(Math.Max(1000, _cfg.HeartbeatMilliseconds));

        // While the device is away nothing the provider reads can be acted on - ShouldUnlock already
        // returns false for Present=0 - so a stale heartbeat is harmless and there is nothing to
        // re-stamp. The loop slows right down and only watches for changes.
        var idleTick = TimeSpan.FromSeconds(10);

        // Deadlines are held as absolute tick counts rather than "time since the last write". Comparing
        // elapsed time against an interval that is also the sleep duration makes the check land exactly
        // on the boundary, and timer slack then makes every other poll miss it - which silently doubled
        // the real heartbeat interval.
        var nextWriteTick = Environment.TickCount64;

        // Consecutive publish attempts that did not reach disk. Drives the rate-limited warning and the
        // retry backoff.
        var failures = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var tick = idleTick;
                try
                {
                    // The RAW connection state, not the debounced one. This bit travels to the credential
                    // provider and decides whether a stored password may be handed to LSA, so it has to
                    // stop saying "present" the moment the keyboard is gone - a grace period here would
                    // mean the lock screen keeps unlocking itself with no keyboard in the room.
                    // Monitoring grace still exists; it just stays in OnPresenceChanged, where it delays
                    // *locking*, which is the question it was designed for.
                    var present = Monitor.RawConnected;
                    var now = DateTime.UtcNow;

                    // The pause file is only worth reading while it could change the outcome.
                    var suppressUntil = present ? PresenceChannel.ReadSuppressUntil() : 0L;

                    var signature = (present, Monitor.RawSinceTicks, _cfg.AutoUnlock,
                                     _cfg.UnlockWindowSeconds, suppressUntil);

                    var changed = _lastSignature != signature;
                    var keepaliveDue = present && Environment.TickCount64 >= nextWriteTick;

                    if (changed || keepaliveDue)
                    {
                        // The security-relevant transition deserves its own line: this is the exact moment
                        // the stored password becomes available to (or unavailable to) the lock screen.
                        if (_lastSignature is { } previous && previous.Present != present)
                            Log.Info($"published presence -> {(present ? "CONNECTED" : "DISCONNECTED")} " +
                                     "(this is what the lock screen gates on)");

                        if (PresenceChannel.Write(new PresenceSnapshot
                            {
                                Present = present,
                                SinceTicks = Monitor.RawSinceTicks,
                                HeartbeatTicks = now.Ticks,
                                SuppressUntilTicks = suppressUntil,
                                AutoUnlock = _cfg.AutoUnlock,
                                UnlockWindowSeconds = _cfg.UnlockWindowSeconds,
                            }))
                        {
                            _lastSignature = signature;
                            nextWriteTick = Environment.TickCount64 + (long)keepalive.TotalMilliseconds;
                            failures = 0;
                        }
                        else
                        {
                            // Deliberately leave _lastSignature alone so this same state is retried on the
                            // next iteration, and say so loudly: from the lock screen's side a state that
                            // cannot be republished is indistinguishable from "the user walked away".
                            failures++;
                            if (failures == 1 || failures % 20 == 0)
                                Log.Error($"cannot publish presence ({failures} time(s), still retrying - " +
                                          "the lock screen treats a stale state as absent)");
                        }

                        // Repainting once per heartbeat is what made the tray rebuild its icon, its tooltip
                        // and the shell's copy of both every couple of seconds.
                        if (changed) Changed?.Invoke();
                    }

                    tick = present ? keepalive : idleTick;
                    if (failures > 0)
                        tick = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Max(1, failures)));
                }
                catch (Exception ex)
                {
                    // The loop MUST survive this. Dying here is exactly how the feature went silently
                    // dead on 2026-10-14 21:26: one UnauthorizedAccessException, the process still
                    // running, presence.dat frozen, and the first visible symptom two hours later was
                    // the unlock tile quietly vanishing.
                    failures++;
                    if (failures == 1 || failures % 20 == 0)
                        Log.Error($"heartbeat iteration failed ({failures} time(s), still retrying): " +
                                  $"{ex.GetType().Name}: {ex.Message}");
                    tick = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Max(1, failures)));
                }

                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(tick, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Unreachable in practice - the per-iteration catch above keeps the loop alive. Kept as a
            // last resort so that even a bug in the error path cannot stop the publishing.
            Log.Error($"heartbeat loop stopped unexpectedly: {ex}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_heartbeat is not null)
        {
            try { await _heartbeat.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        await Monitor.DisposeAsync();

        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }

        // Publish "absent" so a credential provider that outlives us stops offering the unlock.
        PresenceChannel.Write(PresenceSnapshot.Absent(DateTime.UtcNow.Ticks));
        _cts.Dispose();
        Log.Info("agent stopped");
    }
}
