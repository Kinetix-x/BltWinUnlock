# Measures the resident cost of the agent in each mode, as deltas over a settle window so that
# one-off startup I/O is excluded.
#
# Working set alone is misleading for .NET: the runtime maps assemblies lazily and shares a lot of
# pages between processes. The numbers that actually answer "what does this cost me while it idles":
#   privateMB   - committed memory, i.e. what this process really takes
#   cpuPercent   - share of one core, averaged over the window
#   writeOps     - disk write operations; this is what the heartbeat interval controls
#   gdi/user     - GDI and USER handles; the tray rebuilds its icon from a bitmap on every refresh
#
# Usage: powershell -ExecutionPolicy Bypass -File tools\footprint\measure.ps1 [-SettleSeconds 12]

param(
    [int] $SettleSeconds = 12,
    [int] $WarmupSeconds = 4,
    [string] $Exe = ''
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Exe)) {
    $root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $Exe = Join-Path $root 'dist\app\btwu.exe'
}
if (-not (Test-Path -LiteralPath $Exe)) { throw "not built yet: $Exe  (run build.cmd first)" }

if (-not ('Win32.Native' -as [type])) {
    # GetProcessIoCounters is used instead of the WMI Win32_Process counters: WMI intermittently
    # returns nothing for a just-started process, which silently turns the row into all zeros.
    Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct IO_COUNTERS {
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern int GetGuiResources(System.IntPtr hProcess, int uiFlags);
[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern bool GetProcessIoCounters(System.IntPtr hProcess, out IO_COUNTERS counters);
'@ -ErrorAction SilentlyContinue
}

# Assemblies whose presence means a UI stack got loaded. Extension stripped before comparing.
$heavyNames = @('System.Windows.Forms', 'System.Drawing', 'System.Drawing.Common', 'WindowsBase',
                'PresentationCore', 'PresentationFramework', 'Microsoft.Windows.SDK.NET',
                'WinRT.Runtime', 'System.Net.Http', 'System.Management')

function Get-Gui($h, $flag) {
    try { if ($h -ne [IntPtr]::Zero) { return [Win32.Native]::GetGuiResources($h, $flag) } } catch { }
    return -1
}

function Get-Io($h) {
    $c = New-Object Win32.Native+IO_COUNTERS
    try {
        if ($h -ne [IntPtr]::Zero -and [Win32.Native]::GetProcessIoCounters($h, [ref]$c)) {
            return [pscustomobject]@{
                Read  = [int64]$c.ReadOperationCount
                Write = [int64]$c.WriteOperationCount
                Other = [int64]$c.OtherOperationCount
            }
        }
    } catch { }
    return [pscustomobject]@{ Read = [int64]-1; Write = [int64]-1; Other = [int64]-1 }
}

function Measure-Mode {
    param([string] $Mode)

    $proc = Start-Process -FilePath $Exe -ArgumentList $Mode -PassThru -WindowStyle Hidden
    $result = [ordered]@{
        Mode = $Mode; Note = ''; WorkingSetMB = 0; PrivateMB = 0
        Threads = 0; Handles = 0; Gdi = 0; User = 0
        CpuPercent = 0; WriteOps = 0; ReadOps = 0; OtherOps = 0
        LoadedHeavy = '(none)'
    }
    try {
        Start-Sleep -Seconds $WarmupSeconds
        $proc.Refresh()
        if ($proc.HasExited) { $result.Note = "exited early (code $($proc.ExitCode))"; return [pscustomobject]$result }

        $io0 = Get-Io $proc.Handle
        $cpu0 = $proc.TotalProcessorTime
        $t0 = [datetime]::UtcNow

        Start-Sleep -Seconds $SettleSeconds

        $proc.Refresh()
        if ($proc.HasExited) { $result.Note = "exited during window (code $($proc.ExitCode))"; return [pscustomobject]$result }

        $io1 = Get-Io $proc.Handle
        $cpu1 = $proc.TotalProcessorTime
        $elapsed = ([datetime]::UtcNow - $t0).TotalSeconds

        if ($io0.Write -lt 0 -or $io1.Write -lt 0) {
            $result.Note = 'process IO counters unavailable'
        }

        try {
            $loaded = $proc.Modules |
                ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.ModuleName) } |
                Where-Object { $heavyNames -contains $_ } |
                Sort-Object -Unique
            if ($loaded) { $result.LoadedHeavy = ($loaded -join ', ') }
        } catch { $result.LoadedHeavy = '(unavailable)' }

        $result.WorkingSetMB = [math]::Round($proc.WorkingSet64 / 1MB, 1)
        $result.PrivateMB    = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        $result.Threads      = $proc.Threads.Count
        $result.Handles      = $proc.HandleCount
        $result.Gdi          = Get-Gui $proc.Handle 0
        $result.User         = Get-Gui $proc.Handle 1
        $result.CpuPercent   = [math]::Round(($cpu1 - $cpu0).TotalMilliseconds / ($elapsed * 10), 3)
        $result.WriteOps     = $io1.Write - $io0.Write
        $result.ReadOps      = $io1.Read - $io0.Read
        $result.OtherOps     = $io1.Other - $io0.Other
        return [pscustomobject]$result
    }
    finally {
        if (-not $proc.HasExited) { $proc.Kill(); $null = $proc.WaitForExit(5000) }
    }
}

$rows = @()
foreach ($m in @('run', 'tray')) { $rows += Measure-Mode -Mode $m }

$lines = @()
$lines += "btwu footprint report  ($(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))"
$lines += "exe: $Exe"
$lines += "warmup ${WarmupSeconds}s excluded, then a ${SettleSeconds}s measurement window per mode"
$lines += ''
$lines += ('{0,-5} {1,9} {2,9} {3,8} {4,8} {5,6} {6,6} {7,9} {8,8} {9,8}' -f `
    'mode', 'workSetMB', 'privateMB', 'threads', 'handles', 'gdi', 'user', 'cpuPercent', 'writeOps', 'readOps')
$lines += ('-' * 100)
foreach ($r in $rows) {
    $lines += ('{0,-5} {1,9} {2,9} {3,8} {4,8} {5,6} {6,6} {7,9} {8,8} {9,8}' -f `
        $r.Mode, $r.WorkingSetMB, $r.PrivateMB, $r.Threads, $r.Handles, $r.Gdi, $r.User,
        $r.CpuPercent, $r.WriteOps, $r.ReadOps)
    $lines += "      heavy assemblies: $($r.LoadedHeavy)"
    if ($r.Note) { $lines += "      note: $($r.Note)" }
}
$lines += ''
$lines += 'reading: privateMB = real committed cost. cpuPercent is share of ONE core.'
$lines += '         writeOps over the window is what the heartbeat interval and the poll loop drive.'

$report = $lines -join [Environment]::NewLine
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'footprint.txt') -Value $report -Encoding UTF8
Write-Output $report
