#requires -version 5.1
<#
  Btwu —— 打出「绿色版」便携文件夹（以及一个 zip）。

  产物布局（<stage> = dist\Btwu-<版本>-<RID>）：

      <stage>\
        app\                          ← 会被整个复制到 %ProgramFiles%\Btwu 的内容
          btwu.exe                    ← 自包含：CLI + 托盘 + 图形面板，同一个程序
          BtwuCredProv.dll            ← 原生凭据提供程序（安装时进 System32）
          scripts\                    ← 安装/卸载/录入脚本，GUI 直接从这儿调用
        启动管理面板.cmd               ← 双击这个
        install-admin.cmd             ← 图形面板起不来时的应急入口
        uninstall-admin.cmd
        使用说明.txt

  为什么只发布一个 exe：图形面板是同一个 btwu.exe 的一个模式（btwu gui），不是第二个程序。
  分开的话，自包含包里要装两份 .NET 运行时（各约 150 MB），而且每条命令都会有两个实现。
  这么做的代价是 btwu.exe 必须保持控制台子系统（CLI 要能被管道读写、enroll 要能逐字符读口令），
  所以它启动时会自己把控制台窗口藏掉——托盘从第一天起就是这么做的。

  「绿色」能到哪一步，说清楚：
    · 不需要装 .NET（默认自包含）；
    · 解压即用，卸载删除文件夹即可（数据目录另说，见 uninstall.ps1）；
    · 但凭据提供程序必须进 System32 并在 HKLM 注册，登录自启的计划任务也必须管理员建——
      LogonUI 在用户配置文件存在之前就要加载那个 DLL，这三件事绕不过管理员。
      所以「免安装」= 只解压 + 首次点一次 UAC。

  开关：
    -Runtime RID    win-x64（默认）或 win-arm64
    -Version VER    版本号，用于目录名、zip 名与使用说明（默认 1.3.3）
                    ⚠️ 与仓库根 Directory.Build.props 里的 <Version> 是同一件事，改了要一起改
    -SkipBuild      不重新构建，直接用 dist\ 里现成的产物
    -FrameworkDependent
                    发布成「依赖运行时」的瘦包（几 MB），代价是目标机必须先装
                    .NET 10 桌面运行时。默认是自包含。
    -NoZip          只做文件夹，不压缩
    -NoShortcut     不写启动入口（例如在无桌面环境里打包）
    -SourceUrl URL  源码地址，写进包内「使用说明.txt」的许可证一节
                    本项目以 **GPL-3.0** 发布：分发二进制时要一并给出源码获取方式，
                    所以发版时**应当**带上这个参数（不给也能打包，但会打印一条警告）
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Version = '1.3.3',
    [switch]$SkipBuild,
    [switch]$FrameworkDependent,
    [switch]$NoZip,
    [switch]$NoShortcut,
    [string]$SourceUrl = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 与 install.ps1 / uninstall.ps1 同一套理由：被重定向时输出 UTF-8，否则调用方解码会乱码；
# 直接跑时保留控制台自己的代码页，中文在 936 窗口里才正常。
try {
    if ([Console]::IsOutputRedirected) {
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    }
} catch { }

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
# 两个版本的目录名必须分开，否则先打的那个会被后打的整个覆盖——而覆盖是静默的，
# 最后只剩一个包，还看不出少了谁。自包含不带后缀（默认，也是推荐发给别人的那个），
# 依赖运行时的加 -fd，两个 zip 的名字同样区分。
$stageName = "Btwu-$Version-$Runtime" + $(if ($FrameworkDependent) { '-fd' } else { '' })
$stage = Join-Path $dist $stageName
$appDir = Join-Path $stage 'app'

function Write-Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "    $text" -ForegroundColor Gray }
function Write-Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }

if ($Runtime -notin @('win-x64', 'win-arm64')) {
    throw "-Runtime 只能是 win-x64 或 win-arm64（收到 '$Runtime'）。"
}

# --------------------------------------------------------------------------- 前置

Write-Host "Btwu —— 打包绿色版 ($stageName)" -ForegroundColor White

$nativeDll = Join-Path $dist 'BtwuCredProv.dll'
if (-not $SkipBuild) {
    Write-Step '构建全部（原生 + .NET）'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw '构建失败，打包中止。' }
} else {
    Write-Step '构建（-SkipBuild：跳过）'
}

if (-not (Test-Path $nativeDll)) {
    throw "找不到 $nativeDll。原生凭据提供程序没构建成功——没有它，绿色版装上去也解锁不了。" +
          "请先跑 scripts\build.ps1（需要 Visual Studio 生成工具的 C++ 工作负载）。"
}
Write-Ok "原生组件: $nativeDll"

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if ($null -eq $dotnet) {
    $fallback = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $fallback) { $dotnet = Get-Item $fallback }
}
if ($null -eq $dotnet) { throw '没找到 dotnet。请安装 .NET 10 SDK。' }

# --------------------------------------------------------------------- 发布

Write-Step "发布 ($Runtime, $(if ($FrameworkDependent) { '依赖运行时' } else { '自包含' }))"

# 整个重新来过，而不是往旧目录里叠加：dotnet publish -o 不清理陈旧文件，
# 上一版留下的 DLL 会一直躺在包里，然后被 robocopy /MIR 原样带进 Program Files。
if (Test-Path $stage) {
    # -Version 是个自由参数，而下面要递归删掉 $stage。名字对不上就直接拒绝，
    # 免得 -Version 打错一个字符就删到别的目录去。
    if ((Split-Path $stage -Leaf) -notlike 'Btwu-*') {
        throw "拒绝删除不像本脚本暂存目录的路径：$stage（目录名必须以 Btwu- 开头）"
    }
    Remove-Item $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $appDir | Out-Null

$publishArgs = @(
    'publish', (Join-Path $root 'src\Btwu.Agent\Btwu.Agent.csproj'),
    '-c', 'Release',
    '-r', $Runtime,
    '-o', $appDir,
    '-v', 'minimal',
    '--nologo'
)
if ($FrameworkDependent) {
    $publishArgs += '--self-contained'; $publishArgs += 'false'
    Write-Warn '依赖运行时：目标机必须自己装 .NET 10 桌面运行时（windowsdesktop-runtime），否则双击没反应。'
} else {
    $publishArgs += '--self-contained'; $publishArgs += 'true'
}

# 不做单文件：WinForms + WinRT 投影在单文件下需要自解压到临时目录，启动更慢，
# 而且会让 System32 里的 DLL 与旁边的脚本之间多出一层不可控的路径。
$publishArgs += '-p:PublishSingleFile=false'

# 不发调试符号。它们占地方，而且 PDB 里带着构建机的源码路径。
# 用发布开关而不是发布完再删文件：少一步依赖删除权限的操作，也就少一种失败方式。
$publishArgs += '-p:DebugType=none'
$publishArgs += '-p:DebugSymbols=false'

& $dotnet.Source @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

$appExe = Join-Path $appDir 'btwu.exe'
if (-not (Test-Path $appExe)) { throw "发布完成但找不到 $appExe。" }
Write-Ok $appExe

# ------------------------------------------------------------------- 组装内容

Write-Step '组装便携目录'

Copy-Item $nativeDll (Join-Path $appDir 'BtwuCredProv.dll') -Force
Write-Ok 'BtwuCredProv.dll'

# 只复制在这次安装里真有角色的脚本。install.ps1 自己会识别「上一层有 btwu.exe」= 便携布局，
# 因此直接跑 app\scripts\install.ps1 就能装，不需要任何参数。
# build.ps1 与 package.ps1 是开发工具：前者在这里没有源码可编译，后者会把 dist\ 当成输出目录，
# 放进用户包里只会让人以为它们有用。
$runtimeScripts = @('install.ps1', 'uninstall.ps1', 'enroll.ps1')
$scriptDir = Join-Path $appDir 'scripts'
New-Item -ItemType Directory -Force -Path $scriptDir | Out-Null
foreach ($name in $runtimeScripts) {
    $source = Join-Path $PSScriptRoot $name
    if (-not (Test-Path $source)) { throw "缺少 scripts\$name——便携包不完整。" }
    Copy-Item $source $scriptDir -Force
}
Write-Ok "scripts\ ($($runtimeScripts.Count) 个脚本)"

# 应急入口：图形面板起不来时（例如组件被安全软件拦了、或者用户就想看控制台输出）走这些。
$launchers = @('install-admin.cmd', 'uninstall-admin.cmd')
$copiedLaunchers = 0
foreach ($launcher in $launchers) {
    $source = Join-Path $root $launcher
    if (Test-Path $source) {
        Copy-Item $source $appDir -Force
        $copiedLaunchers++
    } else {
        Write-Warn "缺少 $launcher（仓库里没有，跳过）"
    }
}
Write-Ok "$copiedLaunchers 个应急入口 .cmd"

# ------------------------------------------------------------------ 快捷方式

if (-not $NoShortcut) {
    Write-Step '写启动入口'

    # 复制 `启动管理面板.cmd`，而不是生成 .lnk。
    #
    # .lnk 只能存**绝对路径**（CreateShortcut 的设计如此），于是绿色包一改名、一移动、一换机器
    # 就失效——实测：双击包里的快捷方式得到「指定路径不存在」，而 btwu.exe 明明就在旁边。
    # 绿色版的全部意义是「拷到哪儿都能用」，所以入口必须自己按 %~dp0 解析（见那个 .cmd 里的注释：
    # 它还会先把工作目录切走，免得进程的 CWD 钉住包目录、之后删不掉也改不了名）。
    Copy-Item (Join-Path $root '启动管理面板.cmd') $stage -Force
    Write-Ok '启动管理面板.cmd → app\btwu.exe gui（路径相对本文件，可随意移动）'
}

# ------------------------------------------------------------------ 使用说明

Write-Step '写使用说明.txt'

$runtimeLine = if ($FrameworkDependent) {
    "$Runtime（依赖运行时的瘦包：目标机需要先装 .NET 10 桌面运行时）"
} else {
    "$Runtime（自包含：目标机不需要装 .NET）"
}

# GPL-3.0：分发二进制时要给出源码获取方式。没给就明说没有，而不是编一个地址出来。
if ([string]::IsNullOrWhiteSpace($SourceUrl)) {
    Write-Warn '没有传 -SourceUrl：使用说明里会写「见项目主页」。'
    Write-Warn '本项目以 GPL-3.0 发布，正式发版时请用 -SourceUrl https://github.com/<你>/<仓库>。'
    $sourceLine = '见项目主页（本包未内置地址）'
} else {
    $sourceLine = $SourceUrl
}

$readme = @"
Btwu —— 蓝牙在场免密解锁（Windows 10/11）
版本 $Version    $runtimeLine

这是什么
--------
带上蓝牙键盘走开，电脑锁屏；回到电脑旁边，键盘一重连，锁屏自动解开。
**它只负责解锁，不负责锁屏**——什么时候锁屏由 Windows 自己决定（合盖、超时、Win+L）。
面板上勾选的设备就是钥匙，其中任何一台连着，就认为你在场。

怎么开始
--------
1. 双击「启动管理面板.cmd」。
2. 最上面的「安装」里点「安装」。**第一次它会先让你勾选触发设备、再录入口令，最后才装 ——
   一次授权（UAC）把这三步做完。** 口令要的是 Windows 账户口令，不是 PIN。
3. 之后想在面板里改设备，就用「触发设备」区的列表 + 「保存」。
4. Win+L 锁屏再解锁一次：设备在场时不用输密码就回到桌面。

更新到新版本
------------
从**新版本**的文件夹里启动「Btwu 管理面板」，再点「更新」。
（从已安装的那份面板点同一个按钮只会重做注册与自启——因为那时的安装来源就是安装目标，
脚本会正确地跳过复制，于是文件不会更新。面板上会写明这一点。）

面板与托盘
----------
面板就一页：顶部一句话（已连接/未连接、免密可用/不可用）、触发设备、账户口令、
暂停与启用、安装（装好后按钮变成「更新」）、诊断，最下面是作者信息。**「保存」会顺带重启后台 agent**——它只在启动时读一次配置。

托盘图标右键只有四项：状态、打开管理面板、暂停/启用、退出（双击图标也是打开面板）。

关于设备选择
------------
勾上的设备就是钥匙，**不检查它是键盘还是别的东西**：勾了耳机或手机，它们连着也算你在场，
面板会在提示里点名。要收紧成只认 HID 键盘：

    app\btwu.exe enroll --require-hid Keyboard

界面之外的入口
--------------
  app\btwu.exe --help      命令行全貌
  app\btwu.exe list        已配对设备清单；monitor 统计断连情况
  app\btwu.exe autosubmit  回车模式（默认）/ 全自动
  app\btwu.exe unlock-probe  复现锁屏按回车那一次提交，排查「锁屏就是不解锁」（需管理员）
  app\install-admin.cmd    面板起不来时的安装入口；uninstall-admin.cmd removedata 是卸载入口
  config.json              进阶项：StartupGraceSeconds、UnlockWindowSeconds、
                           HeartbeatMilliseconds / PollSeconds，以及 LockOnAbsence
                           （默认 false = 只解锁不锁屏；改成 true 才会在设备离开后主动锁屏）

为什么需要一次管理员
--------------------
凭据提供程序必须放进 System32 并在 HKLM 注册、计划任务必须管理员建、cred.bin 要收紧 ACL。
装完之后日常操作不需要管理员，卸载也不需要。

数据在哪
--------
  C:\ProgramData\BtwuUnlock\   config.json、cred.bin（DPAPI 加密的口令）、presence.dat、两个日志
  agent 不需要任何窗口；想手动跑就在终端里执行 btwu tray，它会立刻脱离控制台。

安全，直说
----------
cred.bin 是本机管理员（或拿到磁盘和机器 DPAPI 密钥的人）可以解密的——要能在锁屏重放口令，
这一点绕不过去。不想用了就在面板里删掉口令，或用「暂停」让本工具彻底不介入锁屏（下一次锁屏生效）。
另外：勾了非输入设备（耳机、手机）时，它连着就等于你在场。

出问题了怎么办
--------------
  · 先跑「诊断 → 运行自检」，再用「复制诊断信息」把快照拿出来。自检里那句「状态很久没更新 +
    present=0」是正常的（设备不在场时心跳刻意不重复写盘），只有「很久没更新 + present=1」才是出事。
  · **录口令时说校验不通过**：对话框会列出「试过的形式」和「本机登录的账户」两行 ——
    **照「本机登录的账户」那一行填**。两个常见的填错法：
    ① 填了注册表里出现的那个微软邮箱（那只是某个应用的登录记录，不一定是 Windows 登录身份）；
    ② 填了 PIN（PIN 只在本机解锁用，不是账户口令）。
    也可以先跑「app\btwu.exe doctor」，它第二行就会写出本机账户是本地账户还是 Microsoft 账户。
  · 锁屏界面异常：安全模式不加载第三方凭据提供程序；笔记本自带键盘始终可用；
    也可以用「其他用户 / PIN」切回系统自带的。
  · 完全回滚：app\uninstall-admin.cmd removedata。

卸载
----
双击 app\uninstall-admin.cmd（会弹一次 UAC），它会问你数据目录要不要一起删。
想不询问、直接连数据目录一起删，就用命令加参数运行：uninstall-admin.cmd removedata

凭据提供程序、注册表、计划任务、Program Files 都会被回滚，系统自带的 PIN / 口令 / 指纹不受影响。
面板里没有卸载按钮，这是有意的：程序正在运行时锁着自己的目录，从面板里卸载必然删不干净（实测过）。

许可证与源码
------------
本程序以 **GPL-3.0**（GNU General Public License v3.0）发布，许可证全文见本文件夹里的 LICENSE。
你可以自由使用、修改、再分发；但如果你分发修改后的版本（包括二进制），GPL-3.0 要求你
**同时以同样的许可证提供对应的源码**。

源码地址：$sourceLine
（本包是这个源码的构建产物；按 GPL-3.0，分发这个包的人应当把源码获取方式一并提供。）

程序按「现状」提供，不附带任何担保。它保管着一份能替代你 Windows 口令的凭据（cred.bin），
本机管理员可以解密它——所以不要用在多人共用的、或你不信任管理员的机器上。
"@
$readmePath = Join-Path $stage '使用说明.txt'
# UTF-8 带 BOM：不带的话记事本在部分系统上仍按 ANSI 打开，中文会花掉。
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($readmePath, $readme, $utf8Bom)
Write-Ok $readmePath

# 许可证全文随包走。GPL-3.0 要求分发二进制时同时给出许可证副本与源码获取方式 ——
# 少了这个文件，包里那句「以 GPL-3.0 发布」就只是一句口号。
$licenseSource = Join-Path $root 'LICENSE'
if (-not (Test-Path $licenseSource)) { throw "找不到 $licenseSource —— 包内必须带许可证全文。" }
Copy-Item $licenseSource (Join-Path $stage 'LICENSE') -Force
Write-Ok 'LICENSE（GPL-3.0 全文）'

# ------------------------------------------------------------------------ 自检

Write-Step '自检便携目录'

$required = @(
    (Join-Path $appDir 'btwu.exe'),
    (Join-Path $appDir 'BtwuCredProv.dll'),
    (Join-Path $stage 'LICENSE'),
    (Join-Path $appDir 'scripts\install.ps1'),
    (Join-Path $appDir 'scripts\uninstall.ps1'),
    (Join-Path $stage '使用说明.txt'),
    (Join-Path $stage '启动管理面板.cmd')
)
$missing = @($required | Where-Object { -not (Test-Path $_) })
if ($missing.Count -gt 0) {
    foreach ($item in $missing) { Write-Warn "缺少 $item" }
    throw '便携目录不完整。'
}
foreach ($item in $required) { Write-Ok "有 $item" }

# 调试符号不该出现在用户包里。发布时已经用 DebugType=none 关掉了，这里只是确认它真的关掉了——
# 发布参数被谁改回默认值时，应该在这里响，而不是等文件被发出去。
$pdb = @(Get-ChildItem $appDir -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue)
if ($pdb.Count -gt 0) {
    Write-Warn "包里仍然有 $($pdb.Count) 个 .pdb（DebugType=none 没生效？）"
}

# 源码泄漏检查：自包含发布不会带源码，但如果发布配置被改过就会。留下就是留下。
foreach ($pattern in @('*.cs', '*.cpp', '*.h')) {
    $stray = @(Get-ChildItem $appDir -Recurse -Filter $pattern -ErrorAction SilentlyContinue)
    if ($stray.Count -gt 0) { Write-Warn "包里出现了 $pattern（$($stray.Count) 个）—— 确认一下是不是有意为之。" }
}

$totalBytes = (Get-ChildItem $stage -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Ok ("文件夹大小: {0:N1} MB" -f ($totalBytes / 1MB))

# -------------------------------------------------------------------------- zip

if (-not $NoZip) {
    Write-Step '压缩成 zip'
    $zipPath = Join-Path $dist "$stageName.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    # 三条结论都来自踩过的坑，都体现在下面这几行里：
    #   · Compress-Archive 走 System.IO.Compression，不依赖外部工具，也不需要加载任何程序集
    #     （System.IO.Compression.FileSystem 不会自动解析，加载它要 Add-Type）。
    #   · 它压缩的是文件夹里的内容，所以解压出来就是 stage 目录的内容本身。
    #   · 进度条必须静音，而且必须写 $global:：脚本作用域里的 $ProgressPreference 对模块里的 cmdlet
    #     无效（模块有自己的 SessionState，只认 global）。压 140 MB 的目录能刷出几十兆噪声。
    $previousProgress = $global:ProgressPreference
    $global:ProgressPreference = 'SilentlyContinue'
    try {
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal
    } finally {
        $global:ProgressPreference = $previousProgress
    }

    if (-not (Test-Path $zipPath)) { throw "压缩失败：$zipPath 没有生成。" }
    Write-Ok ("$zipPath  ({0:N1} MB)" -f ((Get-Item $zipPath).Length / 1MB))
}

# --------------------------------------------------------------------------- 收尾

Write-Host ''
Write-Host '打包完成。' -ForegroundColor Green
Write-Host ''
Write-Host "  便携文件夹: $stage"
if (-not $NoZip) { Write-Host "  zip:        $(Join-Path $dist "$stageName.zip")" }
Write-Host ''
Write-Host '  在别的机器上用：'
Write-Host '    1) 整个文件夹复制过去（不要只挑 exe —— 旁边的 BtwuCredProv.dll 是必需的）；'
Write-Host '    2) 双击「启动管理面板.cmd」；'
Write-Host '    3) 面板最上面「安装」→「安装」：第一次会让你勾设备 + 录口令，一次 UAC 做完；'
Write-Host '       以后改设备就在「触发设备」区勾选后点「保存」。'
Write-Host ''
if ($FrameworkDependent) {
    Write-Host '  注意：这是「依赖运行时」的瘦包，目标机必须先装 .NET 10 桌面运行时。' -ForegroundColor Yellow
    Write-Host ''
}
