// LSA 诊断工具 —— 独立的调试入口，不参与安装，也不被安装脚本分发。
//
// 它存在的原因是：`btwu verify` 的结论曾经把两件毫不相干的事混成一句。
// 想分清「口令不对」和「打包不对」，需要能分别去问 LSA 和大局。
//
//   dotnet run --project tools\lsadiag -- packages
//   dotnet run --project tools\lsadiag -- lu  DESKTOP-A1B2C3 alice
//   dotnet run --project tools\lsadiag -- probe DESKTOP-A1B2C3 alice <msg> <packing> <package>
//
// 输出跟项目其他命令一样是中文；如果把它接到管道里看，记得按 UTF-8 解码。

using System.Runtime.InteropServices;
using System.Text;
using Btwu.Core;

namespace Btwu.LsaDiag;

static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 2; }
        return args[0] switch
        {
            "packages" => Packages(),
            "probe" => Probe(args),
            "lu" => LogonUserCheck(args),
            _ => Unknown(args[0]),
        };
    }

    static int Unknown(string mode) { Console.Error.WriteLine($"unknown mode '{mode}'"); Usage(); return 2; }

    static void Usage()
    {
        Console.WriteLine("lsadiag —— Btwu 的 LSA 诊断工具");
        Console.WriteLine();
        Console.WriteLine("  lsadiag packages");
        Console.WriteLine("      列出哪些认证包名能被 LsaLookupAuthenticationPackage 解析。");
        Console.WriteLine("      零风险：不产生任何登录尝试，不需要管理员。查包名问题先用它。");
        Console.WriteLine();
        Console.WriteLine("  lsadiag lu <domain> <user>");
        Console.WriteLine("      用 LogonUser(LOGON32_LOGON_NETWORK) 检查账户口令，口令从 stdin 读一行。");
        Console.WriteLine("      从 stdin 读是为了不让口令出现在命令行和 shell 历史里。");
        Console.WriteLine();
        Console.WriteLine("  lsadiag probe <domain> <user> <msgType> <packing> <package>");
        Console.WriteLine("      用项目自己的 LogonProbe 打包方式调用 LsaLogonUser，口令同样从 stdin 读。");
        Console.WriteLine("      packing 取 bytes | ptr；msgType 通常 2。");
        Console.WriteLine();
        Console.WriteLine("注意：lu 和 probe 每次调用都会产生**一次真实的登录尝试**。");
        Console.WriteLine("口令错会计入账户的失败计数，不要放在循环里跑。");
    }

    // ------------------------------------------------------------------ packages

    [StructLayout(LayoutKind.Sequential)]
    struct LsaString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("secur32.dll")]
    static extern uint LsaConnectUntrusted(out IntPtr lsaHandle);

    [DllImport("secur32.dll", CharSet = CharSet.Ansi)]
    static extern uint LsaLookupAuthenticationPackage(IntPtr lsaHandle, ref LsaString packageName,
        out uint authenticationPackage);

    [DllImport("secur32.dll")]
    static extern uint LsaDeregisterLogonProcess(IntPtr lsaHandle);

    // 2026-10-13 在这台 Windows 11 25H2 上的实测结果，留作对照：
    //
    //   MSV1_0                               -> 0xC00000FE  STATUS_NO_SUCH_PACKAGE
    //   MICROSOFT_AUTHENTICATION_PACKAGE_V1_0 -> packageId 11
    //   Negotiate                             -> packageId 0
    //   Kerberos / NTLM / TSSSP / PKU2U / CloudAP / WDigest / Schannel -> 2 / 3 / 4 / 5 / 6 / 7 / 8
    //   LiveSSP                               -> 0xC00000FE（本机没有）
    //
    // 所以「查不到 MSV1_0」不是 bug，是这台机器的注册名不同；但把包名写死成
    // "MSV1_0" 就会让 verify 的兜底分支永远走不到。
    static readonly string[] CandidateNames =
    [
        "MSV1_0",
        "MICROSOFT_AUTHENTICATION_PACKAGE_V1_0",
        "Negotiate",
        "Kerberos",
        "NTLM",
        "TSSSP",
        "PKU2U",
        "CloudAP",
        "WDigest",
        "Schannel",
        "LiveSSP",
    ];

    static int Packages()
    {
        Console.WriteLine("== LsaLookupAuthenticationPackage，来自当前进程 ==");
        Console.WriteLine($"   is64bit={Environment.Is64BitProcess}  user={Environment.UserName}  machine={Environment.MachineName}");
        Console.WriteLine();

        var connect = LsaConnectUntrusted(out var lsa);
        if (connect != 0) { Console.Error.WriteLine($"   LsaConnectUntrusted 失败 0x{connect:X8}"); return 1; }

        try
        {
            foreach (var name in CandidateNames)
            {
                var bytes = Encoding.ASCII.GetBytes(name);
                var buffer = Marshal.AllocHGlobal(bytes.Length + 1);
                try
                {
                    Marshal.Copy(bytes, 0, buffer, bytes.Length);
                    Marshal.WriteByte(buffer, bytes.Length, 0);
                    var lsaString = new LsaString
                    {
                        Length = (ushort)bytes.Length,
                        MaximumLength = (ushort)(bytes.Length + 1),
                        Buffer = buffer,
                    };
                    var status = LsaLookupAuthenticationPackage(lsa, ref lsaString, out var packageId);
                    Console.WriteLine(status == 0
                        ? $"   OK    {name,-40} packageId={packageId}"
                        : $"   FAIL  {name,-40} 0x{status:X8}  {ExplainLookup(status)}");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { LsaDeregisterLogonProcess(lsa); }

        return 0;
    }

    static string ExplainLookup(uint status) => status switch
    {
        0xC00000FE => "STATUS_NO_SUCH_PACKAGE（这个名字在本机不存在）",
        0xC000000D => "STATUS_INVALID_PARAMETER",
        0xC0000022 => "STATUS_ACCESS_DENIED",
        _ => "",
    };

    // ---------------------------------------------------------------------- probe

    static bool TryParsePacking(string text, out LogonBlobPacking packing)
    {
        if (text is "bytes" or "byteoffsets" or "ByteOffsets")
        { packing = LogonBlobPacking.ByteOffsets; return true; }
        if (text is "ptr" or "absolutepointers" or "AbsolutePointers")
        { packing = LogonBlobPacking.AbsolutePointers; return true; }
        packing = LogonBlobPacking.ByteOffsets;
        return false;
    }

    static int Probe(string[] args)
    {
        if (args.Length < 6) { Usage(); return 2; }
        var domain = args[1];
        var user = args[2];
        if (!int.TryParse(args[3], out var messageType)) { Console.Error.WriteLine("msgType 必须是整数"); return 2; }
        if (!TryParsePacking(args[4], out var packing)) { Console.Error.WriteLine("packing 只能是 bytes 或 ptr"); return 2; }
        var package = args[5];

        var password = ReadPasswordLine();
        Console.WriteLine($"== probe  domain={domain} user={user} msg={messageType} packing={packing} package={package} ==");
        Console.WriteLine();
        Print(LogonProbe.Run(domain, user, password, messageType, packing, package));
        return 0;
    }

    static int LogonUserCheck(string[] args)
    {
        if (args.Length < 3) { Usage(); return 2; }
        var domain = args[1];
        var user = args[2];

        var password = ReadPasswordLine();
        Console.WriteLine($"== LogonUser  domain={domain} user={user} ==");
        Console.WriteLine();
        var result = LogonProbe.CheckPassword(domain, user, password);
        Console.WriteLine($"  {result.Label}");
        Console.WriteLine(result.Success
            ? "    SUCCESS —— 拿到了令牌，口令正确"
            : $"    FAIL  0x{result.Win32Error:X8}  {LogonProbe.ExplainWin32(result.Win32Error)}");
        return 0;
    }

    static void Print(LogonProbeResult result)
    {
        Console.WriteLine($"  {result.Label}");
        Console.WriteLine($"    NTSTATUS 0x{result.Status:X8}  substatus 0x{result.SubStatus:X8}");
        Console.WriteLine($"    {result.Explanation}");
    }

    // 口令从 stdin 读而不是当参数传：参数会出现在进程列表和 shell 历史里。
    static string ReadPasswordLine()
    {
        Console.Error.Write("password (input is echoed, use a throwaway account): ");
        return Console.ReadLine() ?? "";
    }
}
