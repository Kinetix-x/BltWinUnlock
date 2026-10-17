using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

// 目的：判定「本机发布的 BLE 广播，能否被本机另一个进程的 watcher 收到」。
// 这是「PC 自己冒充手环给本机的小米感应钥匙看」是否成立的唯一决定性前提。
//
// 用法：
//   BleLoopback.exe --watch 16           只监听 16 秒
//   BleLoopback.exe --publish 10         只广播 10 秒
//   BleLoopback.exe --both 4000 12000    同进程先监听 4 秒基线，再发布 12 秒
//
// 跨进程测试（排除“同进程被过滤”这一干扰）：
//   进程 A: --watch 16      进程 B 在 3 秒后用 --publish 10

internal static class Program
{
    private const ushort HuamiCompanyId = 0x0157;
    private static readonly byte[] Marker = { 0x42, 0x54, 0x57, 0x55, 0x4C, 0x4F, 0x4F, 0x50 }; // "BTWULOOP"

    private static async Task<int> Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "--both";

        if (mode == "--watch")
        {
            int seconds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 16;
            return await WatchOnly(seconds);
        }
        if (mode == "--publish")
        {
            int seconds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 10;
            return await PublishOnly(seconds);
        }
        return await Both();
    }

    private static async Task<int> WatchOnly(int seconds)
    {
        var seen = new List<string>();
        var gate = new object();

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var a = e.Advertisement;
            var parts = new List<string> { $"addr=0x{e.BluetoothAddress:X12}", $"rssi={e.RawSignalStrengthInDBm}" };
            if (!string.IsNullOrEmpty(a.LocalName)) parts.Add($"name='{a.LocalName}'");
            foreach (var m in a.ManufacturerData) parts.Add($"mfg=0x{m.CompanyId:X4}:{ToHex(m.Data)}");
            foreach (var u in a.ServiceUuids) parts.Add($"svc={u}");
            lock (gate) seen.Add(string.Join(" ", parts));
        };

        Console.WriteLine($"[watch] starting; {seconds}s window");
        Console.WriteLine($"[watch] local adapter address check happens via whether we see our own marker");
        watcher.Start();
        await Task.Delay(seconds * 1000);
        watcher.Stop();

        List<string> all;
        lock (gate) all = seen.ToList();
        var hits = all.Where(s => s.Contains("mfg=0x0157", StringComparison.OrdinalIgnoreCase)).ToList();

        Console.WriteLine($"[watch] total={all.Count} unique={all.Distinct().Count()} huamiIdHits={hits.Count}");
        foreach (var h in hits.Distinct()) Console.WriteLine("[watch] HIT " + h);
        Console.WriteLine(hits.Count > 0
            ? "[watch] RESULT=LOOPBACK_WORKS"
            : "[watch] RESULT=NO_LOOPBACK");
        Console.Out.Flush();

        var p = Environment.GetEnvironmentVariable("BTWU_LOOPBACK_LOG");
        if (!string.IsNullOrEmpty(p))
        {
            var lines = new List<string> { $"total={all.Count}", $"huamiIdHits={hits.Count}" };
            lines.AddRange(hits.Distinct().Select(h => "HIT " + h));
            lines.AddRange(all.Distinct().Take(200).Select(s => "ADV " + s));
            await File.WriteAllLinesAsync(p, lines);
        }
        return hits.Count > 0 ? 0 : 1;
    }

    private static async Task<int> PublishOnly(int seconds)
    {
        BluetoothLEAdvertisementPublisher publisher;
        try
        {
            publisher = new BluetoothLEAdvertisementPublisher();
            var writer = new DataWriter();
            writer.WriteBytes(Marker);
            publisher.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(HuamiCompanyId, writer.DetachBuffer()));
            publisher.StatusChanged += (_, e) => Console.WriteLine($"[publish] status={e.Status} error={e.Error}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[publish] CONSTRUCT_FAILED " + ex.Message);
            return 2;
        }

        try
        {
            publisher.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine("[publish] START_FAILED " + ex.Message);
            return 3;
        }

        Console.WriteLine($"[publish] started as mfg=0x{HuamiCompanyId:X4}:{Convert.ToHexString(Marker)}; holding {seconds}s");
        await Task.Delay(seconds * 1000);
        publisher.Stop();
        Console.WriteLine("[publish] stopped");
        return 0;
    }

    private static async Task<int> Both()
    {
        var seen = new List<string>();
        var gate = new object();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        watcher.Received += (_, e) =>
        {
            var a = e.Advertisement;
            var parts = new List<string> { $"addr=0x{e.BluetoothAddress:X12}", $"rssi={e.RawSignalStrengthInDBm}" };
            if (!string.IsNullOrEmpty(a.LocalName)) parts.Add($"name='{a.LocalName}'");
            foreach (var m in a.ManufacturerData) parts.Add($"mfg=0x{m.CompanyId:X4}:{ToHex(m.Data)}");
            lock (gate) seen.Add(string.Join(" ", parts));
        };

        watcher.Start();
        await Task.Delay(4000);
        int baseline;
        lock (gate) baseline = seen.Count;
        Console.WriteLine($"[both] baseline (4s, no publish): {baseline} advertisement(s)");

        var publisher = new BluetoothLEAdvertisementPublisher();
        var writer = new DataWriter();
        writer.WriteBytes(Marker);
        publisher.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(HuamiCompanyId, writer.DetachBuffer()));
        publisher.StatusChanged += (_, e) => Console.WriteLine($"[both] publisher status={e.Status} error={e.Error}");

        lock (gate) seen.Clear();
        publisher.Start();
        await Task.Delay(12000);
        publisher.Stop();
        watcher.Stop();

        List<string> during;
        lock (gate) during = seen.ToList();
        var hits = during.Where(s => s.Contains("mfg=0x0157", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"[both] during publish (12s): {during.Count} advertisement(s), huamiIdHits={hits.Count}");
        foreach (var h in hits.Distinct()) Console.WriteLine("[both] HIT " + h);
        Console.WriteLine(hits.Count > 0 ? "[both] RESULT=LOOPBACK_WORKS" : "[both] RESULT=NO_LOOPBACK");
        return hits.Count > 0 ? 0 : 1;
    }

    private static string ToHex(IBuffer b)
    {
        var bytes = new byte[b.Length];
        using var r = DataReader.FromBuffer(b);
        r.ReadBytes(bytes);
        return Convert.ToHexString(bytes);
    }
}
