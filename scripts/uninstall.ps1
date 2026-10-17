#requires -version 5.1
<#
  Btwu 卸载脚本 —— 必须以管理员身份运行。

  按顺序回滚安装脚本做的每一件事：
    1. 结束正在运行的 agent 并注销计划任务；
    2. 注销凭据提供程序（两个注册表位置）并删除 System32 里的 DLL；
    3. 删除 %ProgramFiles%\Btwu；
    4. 询问是否删除 %ProgramData%\BtwuUnlock —— 那里存着你的口令，卸载时一并删掉更干净。

  开关：
    -RemoveData   直接删掉数据目录，不询问（图形面板走这条，它自己会先确认）
    -KeepData     直接保留数据目录，不询问
    -KeepPid LIST 逗号分隔的进程号，这些 btwu 进程不会被停掉。
                  图形面板调用时必须传：调用它的窗口本身就是 btwu.exe。

  卸载是幂等的：没装过也能跑，不会报错。
#>
[CmdletBinding()]
param(
    [switch]$RemoveData,
    [switch]$KeepData,
    [string]$KeepPid = ''
)

$ErrorActionPreference = 'Stop'

# 与 install.ps1 同一套理由：被重定向时改用 UTF-8，否则调用方解码会乱码；
# 直接跑的时候保留控制台自己的代码页，中文在 936 窗口里才正常。
try {
    if ([Console]::IsOutputRedirected) {
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    }
} catch { }

$dataDir = Join-Path $env:ProgramData 'BtwuUnlock'
$installDir = Join-Path $env:ProgramFiles 'Btwu'
$taskName = 'BtwuPresenceUnlock'

function Write-Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "    $text" -ForegroundColor Gray }
function Write-Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }

Write-Host 'Btwu —— 卸载' -ForegroundColor White

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole(
    [System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw '需要管理员权限。请用管理员身份打开 PowerShell 后重新运行： powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1'
}

# ------------------------------------------------------------------------- 停掉 agent 与任务

Write-Step '结束 agent 并注销计划任务'
$keep = @()
foreach ($part in ($KeepPid -split '[,\s]+')) {
    $value = 0
    if ([int]::TryParse($part, [ref]$value)) { $keep += $value }
}

Get-Process -Name 'btwu' -ErrorAction SilentlyContinue | ForEach-Object {
    if ($keep -contains $_.Id) {
        Write-Note "保留 pid $($_.Id)（调用方指定的卸载进程/管理面板）"
        return
    }
    try { $_.Kill(); Write-Note "已结束 pid $($_.Id)" } catch { Write-Warn "无法结束 pid $($_.Id): $($_.Exception.Message)" }
}
Start-Sleep -Milliseconds 500

$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($null -ne $task) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Ok "已注销计划任务 $taskName"
} else {
    Write-Note "计划任务 $taskName 不存在，跳过"
}

# ------------------------------------------------------------------- 注销凭据提供程序

Write-Step '注销凭据提供程序'
$installedAgent = Join-Path $installDir 'btwu.exe'
if (Test-Path $installedAgent) {
    & $installedAgent uninstall-cp
    if ($LASTEXITCODE -ne 0) { Write-Warn 'uninstall-cp 返回非零，请检查上面的输出' }
} else {
    Write-Note "找不到 $installedAgent，改用直接删注册表的方式"
    $clsid = '{B7C4E1A2-3D58-4F61-8A9E-2C7B5D40E913}'
    foreach ($key in @(
            "HKLM:\SOFTWARE\Classes\CLSID\$clsid",
            "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\$clsid")) {
        if (Test-Path $key) {
            Remove-Item -Path $key -Recurse -Force
            Write-Ok "已删除 $key"
        }
    }
    $dll = Join-Path $env:SystemRoot 'System32\BtwuCredProv.dll'
    if (Test-Path $dll) {
        try { Remove-Item $dll -Force; Write-Ok "已删除 $dll" }
        catch { Write-Warn "无法删除 $dll（可能仍被 LogonUI 占用）: $($_.Exception.Message)" }
    }
}

$remainingDll = Join-Path $env:SystemRoot 'System32\BtwuCredProv.dll'
if (Test-Path $remainingDll) {
    try { Remove-Item $remainingDll -Force; Write-Ok "已删除 $remainingDll" }
    catch { Write-Warn "无法删除 $remainingDll：注销并重新登录后再删一次" }
}

# ------------------------------------------------------------------- 删除程序目录

Write-Step "删除 $installDir"
if (Test-Path $installDir) {
    try {
        Remove-Item $installDir -Recurse -Force
        Write-Ok '已删除'
    } catch {
        Write-Warn "删除失败: $($_.Exception.Message)"
        Write-Warn "如果这个卸载是从 $installDir 里启动的（图形面板就在那儿），
    那么正在运行的 btwu.exe 会锁住它——脚本改不了自己正在跑的那个 exe。"
        Write-Warn '从绿色版文件夹（解压出来的那个目录）里再跑一次本脚本，就能删干净。'
    }
} else {
    Write-Note '不存在，跳过'
}

# ------------------------------------------------------------------- 数据目录

Write-Step "数据目录 $dataDir"

# ⚠ 这个局部变量**绝不能**叫 $removeData。
#
# PowerShell 的变量名大小写不敏感，所以 `$removeData` 就是参数 `$RemoveData` 本身：
# 一句 `$removeData = $false` 会把调用方传来的开关直接抹掉（实测发生过）——
# 于是 -RemoveData 永远不生效，数据目录连同 cred.bin（一份可以替代口令的凭据）被留下，
# 脚本还打印「要删除请显式传 -RemoveData」，而调用方明明传了，退出码还是 0。
# 静默、且有安全含义，所以这里用一个不可能与参数重名的名字。
$deleteData = $false
if ($RemoveData) {
    $deleteData = $true
} elseif (-not $KeepData -and (Test-Path $dataDir)) {
    if ([Console]::IsInputRedirected) {
        # 没有可以提问的控制台（图形面板的隐藏子进程）。默认保留，并且明确说出来：
        # 静默删掉一份能替代口令的凭据，比留着它更危险；反过来同理，所以这个决定必须有人做。
        Write-Note '输入被重定向，无法询问 —— 按默认「保留」处理。要删除请显式传 -RemoveData。'
    } else {
        Write-Note '这里存着 DPAPI 加密的账户口令（cred.bin）以及日志。'
        $answer = Read-Host '    是否一并删除？输入 y 删除，其他任意键保留 [默认保留]'
        if ($answer -eq 'y' -or $answer -eq 'Y') { $deleteData = $true }
    }
}

if ($deleteData -and (Test-Path $dataDir)) {
    try { Remove-Item $dataDir -Recurse -Force; Write-Ok '已删除' }
    catch { Write-Warn "删除失败: $($_.Exception.Message)" }
} elseif (Test-Path $dataDir) {
    Write-Note '已保留。注意 cred.bin 里是一份可以替代你口令的凭据，建议尽快删除它。'
}

Write-Host ''
Write-Host '卸载完成。' -ForegroundColor Green
Write-Host ''
Write-Host '  如果当前正显示锁屏界面，它仍由已加载的 DLL 服务；注销并重新登录一次即完全恢复原状。'
Write-Host '  计划任务与注册表已回滚，系统自带凭据提供程序（PIN/口令/指纹）未受影响。'
Write-Host ''
