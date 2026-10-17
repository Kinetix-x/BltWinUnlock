#requires -version 5.1
<#
  Btwu 录入口令脚本 —— 需要管理员权限。

  单独用它的场景：安装时跳过了录入（-SkipEnroll），或者以后想换设备/换口令。
  它会跑一遍 btwu enroll，然后把 cred.bin 的 ACL 收紧到 SYSTEM + Administrators，
  这是 btwu enroll 自己做不到的一步（它跑在普通权限下时没有权限改 ACL）。

  用法示例：
    powershell -ExecutionPolicy Bypass -File scripts\enroll.ps1
    powershell -ExecutionPolicy Bypass -File scripts\enroll.ps1 -Args '--device','POP Icon Keys'
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Args
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $env:ProgramFiles 'Btwu'
$dataDir = Join-Path $env:ProgramData 'BtwuUnlock'

function Write-Step([string]$text) { Write-Host "`n==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "    $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "    $text" -ForegroundColor Gray }
function Write-Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole(
    [System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw '需要管理员权限。请用管理员身份打开 PowerShell 后重新运行本脚本。'
}

function Resolve-Account([string]$sid) {
    return (New-Object System.Security.Principal.SecurityIdentifier($sid)).Translate(
        [System.Security.Principal.NTAccount]).Value
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

$agentExe = Join-Path $installDir 'btwu.exe'
if (-not (Test-Path $agentExe)) {
    $staged = Join-Path $root 'dist\app\btwu.exe'
    if (Test-Path $staged) {
        Write-Warn "还没安装到 $installDir，改用构建产物 $staged"
        $agentExe = $staged
    } else {
        throw "没有找到 btwu.exe。请先运行 scripts\build.ps1，或者 scripts\install.ps1。"
    }
}

# cr 录入必须能看到真实控制台，密码是不回显读取的。
Write-Step '录入触发设备与账户口令'
if ($Args -and $Args.Count -gt 0) {
    & $agentExe enroll @Args
} else {
    & $agentExe enroll
}
if ($LASTEXITCODE -ne 0) { throw '录入未完成。' }

Write-Step '锁定 cred.bin 的访问控制'
$credentialFile = Join-Path $dataDir 'cred.bin'
if (Set-CredentialAcl -file $credentialFile) {
    Write-Ok 'cred.bin 现在只有 SYSTEM 和 Administrators 能读'
} else {
    Write-Warn "没有找到 $credentialFile —— 录入似乎没有写入成功"
}

Write-Step '验证保管的口令'
Write-Note '这一步把口令交给 LSA 验证，能确认「打包格式 + 口令」两件事都对，且只做一次登录尝试。'
& $agentExe verify
if ($LASTEXITCODE -ne 0) {
    Write-Warn '验证未通过。上面会说明是口令不对还是 blob 畸形。'
}

Write-Host ''
Write-Host '完成。' -ForegroundColor Green
Write-Host '如果 agent 正在运行，它会在下一次心跳（约 2 秒）内读到新配置，不需要重启。'
Write-Host ''
