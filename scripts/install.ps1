#requires -version 5.1
<#
  Btwu 安装脚本 —— 必须以管理员身份运行。

  它按顺序做这几件事：
    1. 构建（可用 -SkipBuild 跳过），把 agent 装进 %ProgramFiles%\Btwu；
    2. 把凭据提供程序放进 System32 并注册两个注册表位置；
    3. 建 %ProgramData%\BtwuUnlock 并收紧 ACL（目录给当前用户可写，cred.bin 只给 SYSTEM 和管理员）；
    4. 注册登录时自启的计划任务（默认 `btwu tray`，加 -Headless 改用无界面的 `btwu run`）；
    5. 引导录入账户口令，然后给 cred.bin 上锁；
    6. 跑一次全链路自检。

  开关：
    -SkipBuild     用已有产物，不重新构建（便携目录里没有 build.ps1，会自动跳过）
    -SkipEnroll    先不录入口令（以后跑 scripts\enroll.ps1；图形面板自己收口令时用这个）
    -NoAutostart   不注册计划任务
    -Headless      自启用无界面模式，不加载托盘/UI 栈，占用更低，但没有状态图标
    -NoDoctor      跳过最后的全链路自检
    -User NAME     指定交互账户（默认当前登录用户）
    -StagedApp DIR 指定要安装的产物目录（默认自动判断：便携目录 or dist\app）
    -StagedDll F   指定 BtwuCredProv.dll（默认取产物目录里的那一份）
    -KeepPid LIST  逗号分隔的进程号，这些 btwu 进程不会被停掉。
                   图形面板调用时必须传：调用它的窗口本身就是 btwu.exe，
                   否则「先停掉所有 agent」这一步会把安装程序自己和窗口一起杀掉。

  任何一步失败都会明确指出是哪一步；脚本本身不会绕过任何确认。
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipEnroll,
    [switch]$NoAutostart,
    [switch]$Headless,
    [switch]$NoDoctor,
    [string]$User = '',
    [string]$StagedApp = '',
    [string]$StagedDll = '',
    [string]$KeepPid = ''
)

$ErrorActionPreference = 'Stop'

# 输出被重定向时（图形面板的子进程、管道）改用 UTF-8，否则调用方按 UTF-8 解码会全是乱码。
# 不重定向时保持控制台自己的代码页——在 936 的窗口里把编码改成 UTF-8 同样会乱码。
# 两个场景都要对，所以按「有没有被重定向」切换，而不是无条件设死。
try {
    if ([Console]::IsOutputRedirected) {
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    }
} catch { }

$root = Split-Path -Parent $PSScriptRoot
$dataDir = Join-Path $env:ProgramData 'BtwuUnlock'
$installDir = Join-Path $env:ProgramFiles 'Btwu'
$taskName = 'BtwuPresenceUnlock'

function Write-Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "    $text" -ForegroundColor Gray }
function Write-Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }

# 用 SID 解析账户名，避免中文/英文系统上组名不一致。
function Resolve-Account([string]$sid) {
    return (New-Object System.Security.Principal.SecurityIdentifier($sid)).Translate(
        [System.Security.Principal.NTAccount]).Value
}

function Set-DataDirectoryAcl([string]$path, [string]$account) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)

    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagate = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow

    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        (Resolve-Account 'S-1-5-18'), 'FullControl', $inherit, $propagate, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        (Resolve-Account 'S-1-5-32-544'), 'FullControl', $inherit, $propagate, $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $account, 'Modify', $inherit, $propagate, $allow)))

    Set-Acl -Path $path -AclObject $acl
}

function Set-CredentialAcl([string]$file) {
    if (-not (Test-Path $file)) { return $false }

    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $allow = [System.Security.AccessControl.AccessControlType]::Allow

    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        (Resolve-Account 'S-1-5-18'), 'FullControl', $allow)))
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        (Resolve-Account 'S-1-5-32-544'), 'FullControl', $allow)))

    Set-Acl -Path $file -AclObject $acl
    return $true
}

# 调用方（图形面板）要保留的进程。见 -KeepPid 的说明：这个脚本按名字杀 btwu 进程，
# 而从面板里运行时，面板自己和这次安装进程都叫 btwu。
$script:keepPids = @()
foreach ($part in ($KeepPid -split '[,\s]+')) {
    $value = 0
    if ([int]::TryParse($part, [ref]$value)) { $script:keepPids += $value }
}

function Stop-RunningAgent {
    Get-Process -Name 'btwu' -ErrorAction SilentlyContinue | ForEach-Object {
        if ($script:keepPids -contains $_.Id) {
            Write-Note "保留 pid $($_.Id)（调用方指定的安装进程/管理面板）"
            return
        }
        try { $_.Kill(); Write-Note "已结束正在运行的 agent (pid $($_.Id))" } catch { }
    }
    Start-Sleep -Milliseconds 500
}

# --------------------------------------------------------------------------------- 前置检查

Write-Host 'Btwu 蓝牙在场免密解锁 —— 安装' -ForegroundColor White

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole(
    [System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw '需要管理员权限。请用管理员身份打开 PowerShell 后重新运行： powershell -ExecutionPolicy Bypass -File scripts\install.ps1'
}

if ([string]::IsNullOrWhiteSpace($User)) {
    $userId = $identity.Name
} else {
    $userId = "$env:COMPUTERNAME\$User"
}
$accountName = $userId.Split('\')[-1]
Write-Ok "交互账户（写数据目录、跑托盘 agent）: $userId"

# -------------------------------------------------------- 产物位置：便携目录 or 仓库

# 两种布局都要能跑，而它们长得不一样：
#   绿色版   <portable>\app\scripts\install.ps1   产物在脚本的上一层（app\ 就是 %ProgramFiles%\Btwu 的内容）
#   仓库     <repo>\scripts\install.ps1           产物在 <repo>\dist\app
# 判断依据是「上一层有没有 btwu.exe」，而不是猜调用方是谁。
if ([string]::IsNullOrWhiteSpace($StagedApp)) {
    $beside = Split-Path -Parent $PSScriptRoot
    if (Test-Path (Join-Path $beside 'btwu.exe')) {
        $StagedApp = $beside
    } elseif (Test-Path (Join-Path $root 'dist\app\btwu.exe')) {
        $StagedApp = Join-Path $root 'dist\app'
    } else {
        throw "找不到要安装的产物。既没有 $beside\btwu.exe，也没有 $root\dist\app\btwu.exe。`n" +
              "绿色版：请确认整个文件夹完整解压（btwu.exe 与 BtwuCredProv.dll 都在 app\ 里）。`n" +
              "源码：请先完整跑一次 scripts\build.ps1（不要加 -SkipManaged/-SkipNative）。"
    }
}

if ([string]::IsNullOrWhiteSpace($StagedDll)) {
    $besideDll = Join-Path $StagedApp 'BtwuCredProv.dll'
    if (Test-Path $besideDll) {
        $StagedDll = $besideDll
    } elseif (Test-Path (Join-Path $root 'dist\BtwuCredProv.dll')) {
        $StagedDll = Join-Path $root 'dist\BtwuCredProv.dll'
    } else {
        throw "找不到 BtwuCredProv.dll。原生组件没构建成功——请检查 Visual Studio 生成工具是否装了 C++ 工作负载。"
    }
}

Write-Ok "安装来源: $StagedApp"
Write-Ok "凭据提供程序: $StagedDll"

# ------------------------------------------------------------------------------------- 构建

if (-not $SkipBuild) {
    # 便携目录里没有源码也没有 MSVC，构建无从谈起；产物本来就已经在那儿了。
    # 判断依据是「有没有源码树」，不是「有没有 build.ps1」——脚本目录整个会被复制进绿色版，
    # 所以单看脚本在不在会把便携目录误判成仓库，然后试图用不存在的源码去编译。
    $buildScript = Join-Path $PSScriptRoot 'build.ps1'
    $sourceTree = Join-Path $root 'src\Btwu.Agent\Btwu.Agent.csproj'
    if (-not (Test-Path $buildScript) -or -not (Test-Path $sourceTree)) {
        Write-Step '构建（便携目录，没有源码：跳过，用现成产物）'
    } else {
        Write-Step '构建'
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $buildScript
        if ($LASTEXITCODE -ne 0) { throw '构建失败，安装中止。' }
    }
} else {
    Write-Step '构建（-SkipBuild：跳过）'
}

$agentExe = Join-Path $installDir 'btwu.exe'

# --------------------------------------------------------------- 安装 agent 到 Program Files

Write-Step "安装 agent 到 $installDir"
Stop-RunningAgent

# robocopy /MIR 在「来源 == 目标」时会直接报错，而这正是「在已安装的副本上点更新」的情形。
# 那种情况下要做的不是复制，而是把注册、ACL、自启这些步骤重做一遍（也就是修复安装）。
$stagedFull = [System.IO.Path]::GetFullPath($StagedApp).TrimEnd('\')
$installFull = [System.IO.Path]::GetFullPath($installDir).TrimEnd('\')
if ($stagedFull -eq $installFull) {
    Write-Note '安装来源就是安装目标（正在已安装的副本上运行）—— 跳过复制，只重做注册与自启。'
} else {
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    & robocopy $StagedApp $installDir /MIR /NJH /NJS /NP /NFL /NDL /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy 失败（退出码 $LASTEXITCODE）" }
    Write-Ok 'btwu.exe 等文件已就位'
}

# ------------------------------------------------------------ 注册凭据提供程序（System32）

Write-Step '注册凭据提供程序'
& $agentExe install-cp --dll $StagedDll
if ($LASTEXITCODE -ne 0) { throw '注册凭据提供程序失败。' }

# --------------------------------------------------------------- 数据目录与访问控制

Write-Step "准备数据目录 $dataDir"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Set-DataDirectoryAcl -path $dataDir -account $userId
Write-Ok 'SYSTEM / Administrators = 完全控制，交互用户 = 修改（agent 需要写 presence.dat）'
Write-Note 'cred.bin 会在录入口令之后单独上锁（只有 SYSTEM 和 Administrators 能读）'

# ------------------------------------------------------------------------ 登录自启的计划任务

if (-not $NoAutostart) {
    # `tray` puts a status icon in the notification area and needs the WinForms stack loaded; `run`
    # is the same engine with no UI at all. tools\footprint\measure.ps1 measures the difference.
    $mode = if ($Headless) { 'run' } else { 'tray' }
    Write-Step "注册登录自启计划任务 $taskName（模式：$mode）"
    $action = New-ScheduledTaskAction -Execute $agentExe -Argument $mode -WorkingDirectory $installDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
    $principal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -MultipleInstances IgnoreNew -StartWhenAvailable -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
        -ExecutionTimeLimit ([TimeSpan]::Zero)

    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Force `
        -Description 'Btwu 蓝牙在场免密解锁：登录后常驻，设备离开即锁屏，设备回来自动解锁' | Out-Null
    Write-Ok "已注册（登录时以 $userId 身份、普通权限启动）"

    # 立刻拉起来一次。触发器是「登录时」，而安装时用户早就登录过了，所以注册完不主动启动的话，
    # 在下次登录之前 agent 根本不在跑：presence.dat 不存在，锁屏上什么都不会发生，
    # doctor 的 [4] 会一直报「agent 从未启动」，而且从现象上完全看不出这只是「还没登录过」。
    # （安装过程本身还会先杀掉正在运行的 btwu 进程，所以这一步也是必要的补偿。）
    try {
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        Start-Sleep -Milliseconds 900
        Write-Ok "已立即启动（State=$((Get-ScheduledTask -TaskName $taskName).State)）"
    } catch {
        Write-Warn "自动启动没成功：$($_.Exception.Message)"
        Write-Note "手动启动： Start-ScheduledTask -TaskName $taskName"
    }

    if ($Headless) {
        Write-Note '无托盘图标：看不到当前布防状态，用 btwu status 查询'
    } else {
        Write-Note '托盘图标可查看状态；想要最低占用请改用 -Headless 重装'
    }
} else {
    Write-Warn '-NoAutostart：未注册自启任务，需要自己启动 btwu run 或 btwu tray'
}

# ------------------------------------------------------------------------------ 录入口令

if (-not $SkipEnroll) {
    Write-Step '录入账户口令（DPAPI 加密后写入 cred.bin）'
    Write-Note '口令用星号回显；这是唯一一次需要输入它。'
    & $agentExe enroll
    if ($LASTEXITCODE -ne 0) {
        Write-Warn '录入没有完成 —— 安装会继续，但自动解锁用不了。'
        Write-Warn 'btwu enroll 现在会在存盘前用 LogonUser 验一次口令；被拒时它不保存、'
        Write-Warn '不改动 cred.bin，并在上面说明原因（录成了 PIN / 账户是 Microsoft 账户等）。'
        Write-Warn '补齐： scripts\enroll.ps1'
    }
} else {
    Write-Note '-SkipEnroll：跳过'
}

# --------------------------------------------------------------- 给 cred.bin 上锁

Write-Step '锁定 cred.bin 的访问控制'
$credentialFile = Join-Path $dataDir 'cred.bin'
if (Set-CredentialAcl -file $credentialFile) {
    Write-Ok 'cred.bin 现在只有 SYSTEM 和 Administrators 能读'
} else {
    Write-Warn '还没有 cred.bin；录入口令后请再跑一次 scripts\enroll.ps1（它会补上这一步）'
}

# ----------------------------------------------------------------------------------- 自检

if (-not $NoDoctor) {
    Write-Step '全链路自检'
    & $agentExe doctor
}

# ----------------------------------------------------------------------------------- 总结

Write-Host ''
Write-Host '安装完成。' -ForegroundColor Green
Write-Host ''
Write-Host '  怎么验证：'
Write-Host '    1) 确认触发设备（键盘）已连接，然后按 Win+L 锁屏 —— 应当自动回到桌面；'
Write-Host '    2) 关掉键盘电源再锁屏 —— 应当停在锁屏界面，用键盘重新连上后自动解开；'
Write-Host '    3) 看日志： %ProgramData%\BtwuUnlock\agent.log 和 credprov.log'
Write-Host ''
Write-Host '  日常命令（任意权限的 PowerShell，用完整路径或把安装目录加进 PATH）：'
Write-Host "    & '$agentExe' gui         # 打开图形管理面板（状态/设备/口令/设置/工具/安装）"
Write-Host "    & '$agentExe' status      # 当前在场状态与解锁判定"
Write-Host "    & '$agentExe' pause --minutes 60   # 临时忽略在场判断"
Write-Host "    & '$agentExe' resume"
Write-Host ''
Write-Host '  如果锁屏界面出问题（凭据提供程序写错会这样）：' -ForegroundColor Yellow
Write-Host '    - 锁屏界面仍可用「其他用户/PIN」切回系统自带凭据提供程序，笔记本自带键盘始终可用；'
Write-Host '    - 安全模式下 Windows 不加载第三方凭据提供程序，可直接进系统执行卸载；'
Write-Host '    - 应急恢复：删除注册表项'
Write-Host "        HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{B7C4E1A2-3D58-4F61-8A9E-2C7B5D40E913}"
Write-Host '      以及 HKLM\SOFTWARE\Classes\CLSID\{B7C4E1A2-3D58-4F61-8A9E-2C7B5D40E913}，再删掉 System32\BtwuCredProv.dll；'
Write-Host "    - 或者双击 $installDir\uninstall-admin.cmd（会完整回滚）。"
Write-Host ''
Write-Host "  卸载： 双击 $installDir\uninstall-admin.cmd（会弹一次 UAC），它会问数据目录要不要一起删。"
Write-Host ''
