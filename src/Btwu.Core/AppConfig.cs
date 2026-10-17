using System.Text.Json;

namespace Btwu.Core;

public sealed class TriggerDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"BLE", "Classic", or "" to let the agent try BLE first and fall back to classic.</summary>
    public string Kind { get; set; } = "";

    /// <summary>
    /// HID class observed when this trigger was enrolled ("Keyboard" / "Mouse" / "HID" / "HID?" / "-").
    /// Recorded so <c>btwu doctor</c> can tell "the keyboard was re-paired as something else" apart from
    /// "the device is simply not connected".
    /// </summary>
    public string HidClass { get; set; } = "";

    public override string ToString() => $"{Name} ({Id})";

    public static TriggerDevice From(DeviceItem device) => new()
    {
        Id = device.Id,
        Name = device.Name,
        Kind = device.Kind,
        HidClass = device.Identity.KindText,
    };
}

public sealed class AppConfig
{
    /// <summary>Devices whose connection state counts as "the user is here".</summary>
    public List<TriggerDevice> Triggers { get; set; } = new();

    /// <summary>
    /// Which HID class a trigger device must present on this machine before its connection counts as
    /// presence. "Keyboard" (default) accepts only HID keyboards; "Hid" accepts any input peripheral
    /// (keyboard or mouse); "Any" disables the check.
    ///
    /// This is what keeps a paired phone, headset, band or speaker from unlocking the machine: those
    /// devices expose no HID service, so Windows never records a keyboard or mouse node for them. The
    /// verdict is read from the PnP tree and keyed on the Bluetooth address, so it cannot be satisfied
    /// by a different device that merely happens to be nearby.
    /// </summary>
    public string RequiredHidKind { get; set; } = "Keyboard";

    /// <summary>
    /// Lock the workstation once every trigger device has been gone for longer than <see cref="GraceSeconds"/>.
    ///
    /// <b>Off by default since 2026-10-15</b> (it was on until then): the tool's job is to unlock, and
    /// locking on absence turned out to be an unrequested extra - it also forced a grace period on
    /// everybody, because a BLE keyboard's idle disconnect is indistinguishable from walking away, so
    /// the value had to be tuned per keyboard or the screen locked mid-sentence. The agent still
    /// implements it, and <c>config.json</c> still turns it back on, for anyone who wants the old
    /// behaviour; the panel does not offer it and writes <c>false</c>.
    /// </summary>
    public bool LockOnAbsence { get; set; } = false;

    /// <summary>Allow the credential provider to submit the stored credential with no user interaction.</summary>
    public bool AutoUnlock { get; set; } = true;

    /// <summary>
    /// Seconds a device may be disconnected before it counts as absent. BLE peripherals idle-disconnect
    /// to save power, so this must absorb that gap or every typing pause would lock the screen.
    /// Measure yours with <c>btwu monitor</c>.
    /// </summary>
    public int GraceSeconds { get; set; } = 45;

    /// <summary>
    /// Only auto-unlock when presence was (re)established within this many seconds - i.e. the user
    /// actually walked back to the machine. 0 disables the check and unlocks whenever the device is present.
    /// </summary>
    public int UnlockWindowSeconds { get; set; } = 0;

    /// <summary>Do not lock before the trigger device has been seen connected at least once in this run.</summary>
    public bool RequireSeenPresentBeforeLocking { get; set; } = true;

    /// <summary>Seconds to ignore absence right after the agent starts (avoids locking during logon).</summary>
    public int StartupGraceSeconds { get; set; } = 90;

    /// <summary>
    /// How often the published state is re-stamped so the credential provider keeps believing the
    /// agent is alive. This is the agent's main steady-state disk write, so it is deliberately a
    /// multiple of the 15s freshness window rather than a large fraction of it.
    /// </summary>
    public int HeartbeatMilliseconds { get; set; } = 5000;

    /// <summary>
    /// Backstop poll interval. ConnectionStatusChanged supplies the immediate edge, so this only has
    /// to catch events the radio stack drops - it does not need to be fast, and every tick is a wakeup.
    /// </summary>
    public int PollSeconds { get; set; } = 10;

    /// <summary>Account the credential provider will unlock with. Last enrollment wins.</summary>
    public string UserName { get; set; } = "";
    public string Domain { get; set; } = "";

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Paths.ConfigFile))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.ConfigFile));
                if (cfg is not null) return cfg;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"config.json unreadable, using defaults: {ex.Message}");
        }
        return new AppConfig();
    }

    public void Save()
    {
        Paths.EnsureDataDir();
        File.WriteAllText(Paths.ConfigFile, JsonSerializer.Serialize(this, Options));
    }
}
