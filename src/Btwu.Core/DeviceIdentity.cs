using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Btwu.Core;

/// <summary>What a paired Bluetooth peripheral presents itself as, on this machine.</summary>
public enum HidKind
{
    /// <summary>No HID service at all - phones, headsets, speakers, wearables, other computers.</summary>
    None = 0,
    /// <summary>HID nodes exist but none of them declares a setup class.</summary>
    Unknown = 1,
    /// <summary>Generic HID collection: consumer control, vendor defined, game controller.</summary>
    HidDevice = 2,
    Mouse = 3,
    Keyboard = 4,
}

/// <summary>
/// The verdict for one paired Bluetooth device: is it a keyboard, a mouse, some other HID thing, or
/// not an input device at all - and on what evidence.
/// </summary>
public sealed record DeviceIdentity(
    string Address,
    bool HidOverGatt,
    bool HidClassic,
    HidKind Kind,
    ushort? VendorId,
    ushort? ProductId,
    string Evidence)
{
    public bool HidPresent => Kind != HidKind.None;

    public string KindText => Kind switch
    {
        HidKind.Keyboard => "Keyboard",
        HidKind.Mouse => "Mouse",
        HidKind.HidDevice => "HID",
        HidKind.Unknown => "HID?",
        _ => "-",
    };

    /// <summary>One-line explanation for btwu list / enroll / doctor.</summary>
    public string Detail()
    {
        if (Kind == HidKind.None) return Evidence;

        var ids = VendorId is { } v ? $" VID {v:X4}:PID {ProductId:X4}" : "";
        var transport = HidOverGatt ? "HID over GATT" : HidClassic ? "HID BR/EDR" : "";
        return string.IsNullOrEmpty(transport)
            ? $"{KindText}{ids}"
            : $"{KindText} · {transport}{ids}";
    }
}

/// <summary>
/// The gate that decides whether a Bluetooth connection may count as "the user is here".
///
/// The problem it solves: a phone, headset, band or speaker pairs with the machine exactly like a
/// keyboard does. A presence monitor that only asks "is it connected?" cannot tell them apart, and
/// neither can a device name nor an RSSI threshold - those are guesses about a string and about radio
/// conditions, not facts about the device.
///
/// Windows already knows the answer. When a keyboard connects, Windows enumerates a HID node for it
/// and files that node under the *same device container* as the Bluetooth node. Walking that link is
/// a statement of fact, keyed on the 12 hex digits of the Bluetooth address, readable without radio
/// traffic, and valid while the device is asleep.
///
///   BluetoothLE#BluetoothLE2c:ea:fc:24:e9:69-d9:df:9f:87:1e:00       (WinRT paired-device id)
///     -&gt; address d9df9f871e00
///
///   Enum\BTHLEDEVICE\Dev_d9df9f871e00           ContainerID = {aa92ac88-...}
///     -&gt; Enum\HID\...\8&amp;2908b6b7&amp;0&amp;0000          ContainerID = {aa92ac88-...}
///                                                 ClassGUID    = {4d36e96b-...}  == Keyboard
///
/// Verified on Windows 10 25H2 / build 26200 for HID-over-GATT keyboards, a BR/EDR keyboard, two game
/// controllers, a HID mouse, and a headset (which produces no HID node at all).
///
/// Two narrower fallbacks are kept for devices Windows did not assign a container to: the
/// ParentIdPrefix that a HID-over-GATT service node records, and the {HID profile}&amp;VID_x&amp;PID_y name
/// of a classic HID device node. A device that exposes HID but cannot be narrowed further is reported
/// as <see cref="HidKind.HidDevice"/>, which the default requirement still refuses.
/// </summary>
public static class HidGate
{
    // The HID-over-GATT service, and the classic Bluetooth HID profile.
    const string GattHidService = "{00001812-0000-1000-8000-00805f9b34fb}";
    const string BredrHidProfile = "{00001124-0000-1000-8000-00805f9b34fb}";

    // Device setup classes.
    const string ClassKeyboard = "{4d36e96b-e325-11ce-bfc1-08002be10318}";
    const string ClassMouse = "{4d36e96f-e325-11ce-bfc1-08002be10318}";

    // The container id Windows uses for "this device has no container".
    const string NoContainer = "{00000000-0000-0000-ffff";

    const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum";

    const int MaxDepth = 3;

    // The container index is static in practice (it only changes when a device is paired), so a short
    // cache is enough to turn a per-device walk into a per-listing walk without risking staleness.
    const long ContainerCacheMs = 10_000;

    static readonly object CacheGate = new();
    static Dictionary<string, HidKind>? _containerKinds;
    static long _containerKindsStamp;

    static readonly Regex PnpAddressPattern = new(
        @"(?:DEV_|BluetoothDevice_)([0-9A-Fa-f]{12})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex ColonMacPattern = new(
        @"(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}",
        RegexOptions.Compiled);

    static readonly Regex VidPidPattern = new(
        @"VID&([0-9A-Fa-f]+)_PID&([0-9A-Fa-f]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The 12 hex digits of the remote Bluetooth address.
    ///
    /// WinRT hands out paired devices as "BluetoothLE#BluetoothLE&lt;local adapter&gt;-&lt;remote&gt;", so the
    /// remote address is the last one on the line; PnP instance ids spell it as DEV_xxxxxxxxxxxx.
    /// </summary>
    public static string? AddressOf(string deviceId)
    {
        var pnp = PnpAddressPattern.Match(deviceId);
        if (pnp.Success) return pnp.Groups[1].Value.ToUpperInvariant();

        var macs = ColonMacPattern.Matches(deviceId);
        if (macs.Count > 0)
            return macs[^1].Value.Replace(":", "").ToUpperInvariant();

        return null;
    }

    /// <summary>
    /// The Vendor ID field is four bytes with a two-byte source prefix, and devices that drop leading
    /// zeros show up as "VID&amp;02046d" instead of "VID&amp;0002046d". Taking the trailing four hex digits
    /// yields the value that also appears in a HID instance id ("VID_046D"), for both spellings.
    /// </summary>
    static ushort? LastFour(string text)
    {
        var trimmed = text.TrimStart('0');
        if (trimmed.Length == 0) return 0;
        if (trimmed.Length > 4) return null;
        return ushort.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public static DeviceIdentity Probe(string deviceId)
    {
        var address = AddressOf(deviceId);
        if (address is null)
            return new DeviceIdentity("", false, false, HidKind.None, null, null,
                                      "无法从设备实例 ID 解析出蓝牙地址");

        try
        {
            var evidence = new List<string>();

            // Which enumerator the HID service hangs off, plus the VID/PID it advertises there.
            var gatt = false;
            var bredr = false;
            ushort? vendorId = null, productId = null;
            foreach (var (bus, uuid, label) in new[]
                     {
                         ("BTHLEDEVICE", GattHidService, "HID over GATT 服务"),
                         ("BTHENUM", BredrHidProfile, "HID 配置文件 (BR/EDR)"),
                     })
            {
                var node = FindServiceNode(bus, uuid, address);
                if (node is null) continue;

                if (bus == "BTHLEDEVICE") gatt = true; else bredr = true;
                evidence.Add(label);

                var vidpid = VidPidPattern.Match(node);
                if (vidpid.Success)
                {
                    vendorId = LastFour(vidpid.Groups[1].Value);
                    productId = LastFour(vidpid.Groups[2].Value);
                }
            }

            // ---- primary: the device container -------------------------------------------------
            var container = ContainerOf(address);
            if (container is not null)
            {
                var kind = KindInContainer(container);
                if (kind != HidKind.None)
                {
                    evidence.Add($"容器内的 HID 类别 = {kind}");
                    return new DeviceIdentity(address, gatt, bredr, kind, vendorId, productId,
                                              string.Join("；", evidence));
                }
                evidence.Add("容器内没有 HID 节点");
            }
            else
            {
                evidence.Add("设备没有被分配容器 ID");
            }

            // ---- fallback 1: HID over GATT records its HID child's instance prefix -----------------
            if (gatt)
            {
                var kind = ResolveByParentPrefix(HidInstanceIndex(), ParentPrefixes(address));
                if (kind != HidKind.Unknown)
                {
                    evidence.Add($"HID over GATT 子实例前缀 -> {kind}");
                    return new DeviceIdentity(address, gatt, bredr, kind, vendorId, productId,
                                              string.Join("；", evidence));
                }
            }

            // ---- fallback 2: classic HID device nodes are named by VID/PID -------------------------
            if (bredr && vendorId is { } vid && productId is { } pid)
            {
                var kind = ResolveByVendorProduct(vid, pid);
                if (kind != HidKind.Unknown)
                {
                    evidence.Add($"VID/PID 匹配 -> {kind}");
                    return new DeviceIdentity(address, gatt, bredr, kind, vendorId, productId,
                                              string.Join("；", evidence));
                }
            }

            // HID is exposed but the usage could not be read: an input device, not a proven keyboard.
            if (gatt || bredr)
                return new DeviceIdentity(address, gatt, bredr, HidKind.HidDevice, vendorId, productId,
                                          string.Join("；", evidence) + "；无法确定具体用途");

            return new DeviceIdentity(address, false, false, HidKind.None, null, null,
                                      "没有 HID 服务——不是输入外设");
        }
        catch (Exception ex)
        {
            // Never throw out of a classification. "Cannot prove it is a keyboard" is a refusal, and an
            // unreadable registry must not silently become a pass.
            //
            // Deliberately one line rather than ex.ToString(): a devnode whose ACL denies read is a
            // normal condition - the contract is "cannot prove it", not "error" - and the full stack
            // trace cost nine lines every time a device reconnected.
            Log.Warn($"{address}: 读不到设备注册表节点（{ex.GetType().Name}: {ex.Message}），" +
                     $"按「证明不了是键盘」处理");
            return new DeviceIdentity(address, false, false, HidKind.None, null, null,
                                      $"注册表读取失败（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    static string? FindServiceNode(string bus, string serviceUuid, string address)
    {
        using var busKey = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{bus}");
        foreach (var name in busKey?.GetSubKeyNames() ?? [])
        {
            if (!name.StartsWith(serviceUuid, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Contains(address, StringComparison.OrdinalIgnoreCase)) return name;
        }
        return null;
    }

    // ------------------------------------------------------------------------ container resolution

    /// <summary>
    /// The container the Bluetooth device node belongs to. Windows keeps a Bluetooth node and every
    /// node derived from it in one container, which is what links an address to a setup class.
    /// </summary>
    static string? ContainerOf(string address)
    {
        foreach (var bus in new[] { "BTHLE", "BTHENUM" })
        {
            using var busKey = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\{bus}");
            if (busKey is null) continue;

            foreach (var name in busKey.GetSubKeyNames())
            {
                if (!name.Contains(address, StringComparison.OrdinalIgnoreCase)) continue;

                using var device = busKey.OpenSubKey(name);
                if (device is null) continue;

                if (RealContainer(device.GetValue("ContainerID") as string) is { } own) return own;

                foreach (var childName in device.GetSubKeyNames())
                {
                    using var child = device.OpenSubKey(childName);
                    if (RealContainer(child?.GetValue("ContainerID") as string) is { } nested) return nested;
                }
            }
        }
        return null;
    }

    static string? RealContainer(string? containerId) =>
        containerId is { Length: > 0 } && !containerId.StartsWith(NoContainer, StringComparison.OrdinalIgnoreCase)
            ? containerId
            : null;

    /// <summary>
    /// container id -> the most specific HID setup class in it.
    ///
    /// Built by one tolerant walk of the HID tree and cached briefly: listing nine devices would
    /// otherwise walk the same few hundred keys nine times. Some devnodes under Enum\HID (sensor
    /// collections, device-restricted nodes) deny read access to a normal user - a node that cannot be
    /// opened is skipped rather than allowed to fail the whole classification.
    /// </summary>
    static HidKind KindInContainer(string container)
    {
        lock (CacheGate)
        {
            var now = Environment.TickCount64;
            if (_containerKinds is null || now - _containerKindsStamp > ContainerCacheMs)
            {
                _containerKinds = BuildContainerIndex();
                _containerKindsStamp = now;
            }
            return _containerKinds.TryGetValue(container, out var kind) ? kind : HidKind.None;
        }
    }

    static Dictionary<string, HidKind> BuildContainerIndex()
    {
        var map = new Dictionary<string, HidKind>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\HID");
            if (root is not null) WalkHid(root, map, 0);
        }
        catch (Exception ex)
        {
            Log.Warn($"cannot index the HID tree: {ex.Message}");
        }
        return map;
    }

    static void WalkHid(RegistryKey key, Dictionary<string, HidKind> into, int depth)
    {
        if (depth > MaxDepth) return;

        string[] names;
        try { names = key.GetSubKeyNames(); }
        catch (Exception) { return; }

        foreach (var name in names)
        {
            RegistryKey? child = null;
            try
            {
                child = key.OpenSubKey(name);
                if (child is null) continue;

                var container = RealContainer(child.GetValue("ContainerID") as string);
                if (container is not null && child.GetValue("ClassGUID") is string guid && guid.Length > 0)
                {
                    var kind = Classify(guid);
                    into[container] = into.TryGetValue(container, out var known) ? Rank(known, kind) : kind;
                }
                WalkHid(child, into, depth + 1);
            }
            catch (Exception)
            {
                // An unreadable devnode is skipped. It cannot make a keyboard look like a phone, and it
                // must not make a keyboard look unclassifiable either.
            }
            finally { child?.Dispose(); }
        }
    }

    // ------------------------------------------------------------------------------ fallback paths

    /// <summary>Every HID instance id in the PnP tree, with the setup class Windows assigned to it.</summary>
    static List<(string Instance, string ClassGuid)> HidInstanceIndex()
    {
        var list = new List<(string, string)>();
        using var root = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\HID");
        if (root is not null) Collect(root, list, 0);
        return list;
    }

    static void Collect(RegistryKey key, List<(string, string)> into, int depth)
    {
        if (depth > MaxDepth) return;

        string[] names;
        try { names = key.GetSubKeyNames(); }
        catch (Exception) { return; }

        foreach (var name in names)
        {
            RegistryKey? child = null;
            try
            {
                child = key.OpenSubKey(name);
                if (child is null) continue;
                if (child.GetValue("ClassGUID") is string guid && guid.Length > 0) into.Add((name, guid));
                Collect(child, into, depth + 1);
            }
            catch (Exception) { /* unreadable devnode: skip */ }
            finally { child?.Dispose(); }
        }
    }

    /// <summary>For HID over GATT the service node stores the instance prefix of its HID child.</summary>
    static List<string> ParentPrefixes(string address)
    {
        var prefixes = new List<string>();
        using var busKey = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\BTHLEDEVICE");
        foreach (var name in busKey?.GetSubKeyNames() ?? [])
        {
            if (!name.StartsWith(GattHidService, StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains(address, StringComparison.OrdinalIgnoreCase)) continue;

            using var node = busKey!.OpenSubKey(name);
            foreach (var childName in node?.GetSubKeyNames() ?? [])
            {
                using var child = node!.OpenSubKey(childName);
                if (child?.GetValue("ParentIdPrefix") is string prefix && prefix.Length > 0)
                    prefixes.Add(prefix);
            }
            break;
        }
        return prefixes;
    }

    static HidKind ResolveByParentPrefix(List<(string Instance, string ClassGuid)> index, List<string> prefixes)
    {
        if (prefixes.Count == 0) return HidKind.Unknown;

        var best = HidKind.Unknown;
        foreach (var (instance, guid) in index)
        {
            if (!prefixes.Any(p => instance.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            best = Rank(best, Classify(guid));
        }
        return best;
    }

    static HidKind ResolveByVendorProduct(ushort vendorId, ushort productId)
    {
        var wanted = $"VID_{vendorId:X4}&PID_{productId:X4}";

        using var root = Registry.LocalMachine.OpenSubKey($@"{EnumRoot}\HID");
        foreach (var name in root?.GetSubKeyNames() ?? [])
        {
            if (!name.Contains(BredrHidProfile, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) < 0) continue;

            using var node = root!.OpenSubKey(name);
            var best = HidKind.Unknown;
            foreach (var childName in node?.GetSubKeyNames() ?? [])
            {
                using var child = node!.OpenSubKey(childName);
                if (child?.GetValue("ClassGUID") is string guid) best = Rank(best, Classify(guid));
            }
            if (best != HidKind.Unknown) return best;
        }
        return HidKind.Unknown;
    }

    static HidKind Classify(string classGuid) => classGuid.ToLowerInvariant() switch
    {
        ClassKeyboard => HidKind.Keyboard,
        ClassMouse => HidKind.Mouse,
        _ => HidKind.HidDevice,
    };

    // A keyboard also exposes consumer-control and vendor-defined collections, so the most specific
    // class found anywhere on the device wins.
    static HidKind Rank(HidKind a, HidKind b) => (HidKind)Math.Max((int)a, (int)b);

    // ---------------------------------------------------------------------------------------- gate

    /// <summary>
    /// <paramref name="required"/> is <see cref="AppConfig.RequiredHidKind"/>: "Keyboard" (default),
    /// "Hid" (any input peripheral) or "Any" (check disabled).
    /// </summary>
    public static bool Satisfies(string required, HidKind kind) => required.ToLowerInvariant() switch
    {
        "any" => true,
        "hid" => kind >= HidKind.HidDevice,
        _ => kind == HidKind.Keyboard,
    };

    public static bool Satisfies(string required, DeviceIdentity identity) => Satisfies(required, identity.Kind);

    public static string RequirementText(string required) => required.ToLowerInvariant() switch
    {
        "any" => "不检查类别（任何已配对设备都可作为触发）",
        "hid" => "必须是输入外设（HID 键盘或鼠标）",
        _ => "必须是 HID 键盘（默认）",
    };
}
