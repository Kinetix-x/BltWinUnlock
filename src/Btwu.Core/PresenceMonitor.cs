using Windows.Devices.Bluetooth;

namespace Btwu.Core;

/// <summary>
/// Tracks whether any configured trigger device is connected, and keeps TWO answers that must never
/// be conflated:
///
///   <see cref="RawConnected"/> - connected *this instant*, with no debounce. This is what gets
///   published to presence.dat, and therefore the fact the credential provider gates an unlock on.
///
///   <see cref="Present"/> - the same question with a grace period applied, so a short BLE idle
///   disconnect does not count as "the user left". This drives <see cref="PresenceChanged"/> (the
///   lock decision) and never leaves the process.
///
/// Collapsing the two into one bit is a security bug, not a convenience: the published value would
/// then still say "present" for the whole grace period after the keyboard is gone, and the provider
/// would keep offering the stored password - the machine unlocking itself with no keyboard in the
/// room. See <see cref="UpdatePresence"/>.
///
/// ConnectionStatusChanged gives an immediate edge, and the poll loop is the backstop for events the
/// WinRT radio stack drops (radio restart, device re-pairing).
/// </summary>
public sealed class PresenceMonitor : IAsyncDisposable
{
    sealed class Entry
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string Kind { get; set; } = "";
        public BluetoothLEDevice? Ble { get; set; }
        public BluetoothDevice? Classic { get; set; }
        public bool Connected { get; set; }

        /// <summary>HID class verified for this device on the current connection; null = not verified yet.</summary>
        public HidKind? Verified { get; set; }
        public string Why { get; set; } = "";

        /// <summary>Attempts since the last successful open, and when the next one is allowed.</summary>
        public int OpenFailures { get; set; }
        public long NextOpenTick { get; set; }

        /// <summary>True while an outage has already been reported, so it is only logged once.</summary>
        public bool OpenFailed { get; set; }
    }

    /// <summary>
    /// Retry ladder for a device that cannot be opened at all - unpaired, out of range, or the radio
    /// is off. Capped at 30s: a longer cap would mean turning Bluetooth back on could leave the agent
    /// disarmed for minutes, which is worse than the cost of the retry.
    /// </summary>
    static readonly TimeSpan[] OpenBackoff =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
    ];

    readonly AppConfig _cfg;
    readonly List<Entry> _entries = new();
    readonly CancellationTokenSource _cts = new();
    readonly object _gate = new();

    bool _present;
    bool _everPresent;
    long _sinceTicks;
    long _lastRawConnectedTick;

    /// <summary>The undebounced answer, and when it last went true. See <see cref="RawConnected"/>.</summary>
    bool _rawConnected;
    long _rawSinceTicks;

    Task? _loop;

    public PresenceMonitor(AppConfig cfg)
    {
        _cfg = cfg;
        foreach (var trigger in cfg.Triggers)
            _entries.Add(new Entry { Id = trigger.Id, Name = trigger.Name, Kind = trigger.Kind });
    }

    public event Action<bool>? PresenceChanged;
    public event Action<string>? DeviceEvent;

    public IReadOnlyList<string> Names => _entries.Select(e => e.Name).ToList();
    public int ConnectedCount { get { lock (_gate) return _entries.Count(e => e.Connected); } }

    /// <summary>
    /// Debounced presence - "has the user left". Drives locking. Not what gets published.
    /// </summary>
    public bool Present { get { lock (_gate) return _present; } }

    /// <summary>
    /// Raw presence - "is a trigger device connected right now". Published to presence.dat verbatim,
    /// so this is the bit the credential provider turns into "may I offer the stored password".
    /// </summary>
    public bool RawConnected { get { lock (_gate) return _rawConnected; } }

    /// <summary>When <see cref="RawConnected"/> last went true (UTC ticks); 0 if it never has.</summary>
    public long RawSinceTicks { get { lock (_gate) return _rawSinceTicks; } }

    public bool EverPresent { get { lock (_gate) return _everPresent; } }
    public long SinceTicks { get { lock (_gate) return _sinceTicks; } }

    /// <summary>One line per configured device: HID class, whether it passes the gate, current link state.</summary>
    public IReadOnlyList<string> DeviceSummaries()
    {
        lock (_gate)
        {
            var lines = new List<string>(_entries.Count);
            foreach (var entry in _entries)
            {
                var hid = entry.Verified is { } kind ? kind.ToString() : "未验证（本次未连接过）";
                var verdict = entry.Verified is null ? "—" : Accepted(entry) ? "接受" : "拒绝";
                lines.Add($"{entry.Name}: {hid} / {verdict} / {(entry.Connected ? "已连接" : "未连接")}");
            }
            return lines;
        }
    }

    public async Task StartAsync()
    {
        var nowTick = Environment.TickCount64;
        foreach (var entry in _entries) await OpenAsync(entry, nowTick);
        await PollAsync();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info($"monitoring {_entries.Count} device(s): {string.Join(" | ", _entries.Select(e => e.Name))}");
        Log.Info($"grace {_cfg.GraceSeconds}s, poll {_cfg.PollSeconds}s, lockOnAbsence={_cfg.LockOnAbsence}");
        Log.Info($"trigger gate: {HidGate.RequirementText(_cfg.RequiredHidKind)}");
    }

    async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _cfg.PollSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                // One bad poll must not end presence monitoring for the rest of the session. With this
                // loop gone the agent stops noticing departures, so it never locks again - and nothing
                // in the UI would say so. Same failure shape as the heartbeat loop's 2026-10-14 death.
                try
                {
                    await PollAsync();
                }
                catch (Exception ex)
                {
                    Log.Warn($"poll iteration failed, continuing: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error($"poll loop died: {ex}"); }
    }

    async Task PollAsync()
    {
        var nowTick = Environment.TickCount64;
        var anyConnected = false;
        foreach (var entry in _entries)
        {
            // Reopening is what makes a device that is currently offline become usable again, but a
            // device that cannot be opened at all would otherwise be retried - and complained about -
            // on every single tick, forever.
            if (entry.Ble is null && entry.Classic is null && nowTick >= entry.NextOpenTick)
                await OpenAsync(entry, nowTick);

            var connected = false;
            try
            {
                connected = entry.Ble is not null
                    ? entry.Ble.ConnectionStatus == BluetoothConnectionStatus.Connected
                    : entry.Classic is not null && entry.Classic.ConnectionStatus == BluetoothConnectionStatus.Connected;
            }
            catch
            {
                // the handle went stale (radio restart / device re-pair) - reopen on the next poll
                entry.Ble?.Dispose();
                entry.Ble = null;
                entry.Classic?.Dispose();
                entry.Classic = null;
                entry.Kind = "";
            }

            if (connected != entry.Connected)
            {
                entry.Connected = connected;
                // The HID class is re-read on every fresh connection instead of being trusted from
                // config.json: a device can be re-paired, re-flashed or replaced by a same-id clone.
                if (connected) Verify(entry); else entry.Verified = null;
                RaiseDeviceEvent($"{entry.Name} -> {(connected ? "connected" : "disconnected")}");
                Log.Info($"{entry.Name} {(connected ? "connected" : "disconnected")}");
            }
            anyConnected |= connected && Accepted(entry);
        }

        UpdatePresence(anyConnected, nowTick);
    }

    /// <summary>
    /// Reads the device's HID class out of the PnP tree and decides whether its connection may count as
    /// presence. A device that fails the gate is still reported through <see cref="DeviceEvent"/> so
    /// <c>btwu monitor</c> stays honest about what the radio is doing - it just cannot move presence.
    /// </summary>
    void Verify(Entry entry)
    {
        var identity = HidGate.Probe(entry.Id);
        entry.Verified = identity.Kind;
        entry.Why = identity.Detail();

        if (HidGate.Satisfies(_cfg.RequiredHidKind, identity))
        {
            Log.Info($"{entry.Name}: {identity.Detail()} -> 接受为在场依据");
        }
        else
        {
            Log.Warn($"{entry.Name}: {identity.Detail()} -> 拒绝（要求：{HidGate.RequirementText(_cfg.RequiredHidKind)}）");
            RaiseDeviceEvent($"{entry.Name}: 类别不符，不作为在场依据（{identity.KindText}）");
        }
    }

    bool Accepted(Entry entry)
    {
        if (entry.Verified is not { } kind) return false;
        return HidGate.Satisfies(_cfg.RequiredHidKind, kind);
    }

    /// <summary>
    /// Refreshes both notions of presence from one poll result.
    ///
    /// The raw value is refreshed first and unconditionally: it is a fact about the radio, not a
    /// policy, and every later question ("should we lock?", "may we unlock?") reads it directly.
    /// The debounced value below is the only place the grace period is applied, and it is what
    /// <see cref="PresenceChanged"/> reports.
    /// </summary>
    void UpdatePresence(bool anyConnected, long nowTick)
    {
        bool raise = false, value = false;
        lock (_gate)
        {
            if (anyConnected)
            {
                _lastRawConnectedTick = nowTick;
                if (!_rawConnected)
                {
                    _rawConnected = true;
                    _rawSinceTicks = DateTime.UtcNow.Ticks;
                }

                _everPresent = true;
                if (!_present)
                {
                    _present = true;
                    _sinceTicks = DateTime.UtcNow.Ticks;
                    raise = true;
                    value = true;
                }
            }
            else
            {
                _rawConnected = false;

                if (_present && nowTick - _lastRawConnectedTick >= _cfg.GraceSeconds * 1000L)
                {
                    _present = false;
                    raise = true;
                    value = false;
                }
            }
        }

        if (raise)
        {
            Log.Info($"presence -> {(value ? "PRESENT" : "ABSENT")}");

            // Subscriber code (the tray, mainly) runs here, on the polling thread. A throwing handler
            // would otherwise propagate out of PollAsync - historically straight into the loop, which
            // then stopped for good.
            try { PresenceChanged?.Invoke(value); }
            catch (Exception ex) { Log.Warn($"presence handler failed: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Raises <see cref="DeviceEvent"/> without letting a throwing subscriber take the polling thread
    /// with it. Subscribers are UI code (the tray); this loop is the one thing that must not stop.
    /// </summary>
    void RaiseDeviceEvent(string message)
    {
        try { DeviceEvent?.Invoke(message); }
        catch (Exception ex) { Log.Warn($"device event handler failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    async Task OpenAsync(Entry entry, long nowTick)
    {
        string? failure = null;
        try
        {
            if (entry.Kind != "Classic")
            {
                var ble = await BluetoothLEDevice.FromIdAsync(entry.Id);
                if (ble is not null)
                {
                    ble.ConnectionStatusChanged += (_, _) => _ = PollAsync();
                    entry.Ble = ble;
                    entry.Kind = "BLE";
                    OpenSucceeded(entry);
                    return;
                }
                if (entry.Kind == "BLE") failure = "BLE 设备节点打不开";
            }

            if (failure is null)
            {
                var classic = await BluetoothDevice.FromIdAsync(entry.Id);
                if (classic is not null)
                {
                    classic.ConnectionStatusChanged += (_, _) => _ = PollAsync();
                    entry.Classic = classic;
                    entry.Kind = "Classic";
                    OpenSucceeded(entry);
                    return;
                }
                failure = "设备已取消配对，或蓝牙适配器已关闭";
            }
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }

        OpenFailed(entry, nowTick, failure ?? "未知原因");
    }

    static void OpenSucceeded(Entry entry)
    {
        entry.OpenFailures = 0;
        entry.NextOpenTick = 0;
        if (!entry.OpenFailed) return;

        entry.OpenFailed = false;
        Log.Info($"{entry.Name}: 设备恢复可访问");
    }

    /// <summary>
    /// Records one failed open attempt. The warning is emitted once per outage rather than once per
    /// attempt - the old behaviour wrote a warning (and, when classification failed, a full stack
    /// trace) every poll, which added up to megabytes of log per day and forced the log to rotate.
    /// </summary>
    static void OpenFailed(Entry entry, long nowTick, string reason)
    {
        var wait = OpenBackoff[Math.Min(entry.OpenFailures, OpenBackoff.Length - 1)];
        entry.OpenFailures++;
        entry.NextOpenTick = nowTick + (long)wait.TotalMilliseconds;

        if (entry.OpenFailed) return;
        entry.OpenFailed = true;

        Log.Warn($"{entry.Name}: 暂时打不开（{reason}）；退避重试，最长每 {OpenBackoff[^1].TotalSeconds:F0} 秒一次，" +
                 $"恢复后会重新布防");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        foreach (var entry in _entries)
        {
            entry.Ble?.Dispose();
            entry.Classic?.Dispose();
        }
        _cts.Dispose();
    }
}
