#requires -version 5.1
<#
  构建 Btwu 全工程：原生凭据提供程序（x64）+ .NET agent。

  这里刻意不走 vcvars64.bat，而是自己定位 MSVC 与 Windows SDK 并组装 INCLUDE/LIB。
  原因有两个：
    1. vcvars 会调用 reg.exe，在一些被收紧的执行环境里会被直接拦掉，构建会神秘失败；
    2. 少一层批处理环境，出问题时更容易看出是哪条路径不对。
  只要装了 Visual Studio 的“使用 C++ 的桌面开发”工作负载（或 VS 生成工具），这个脚本就能用。
#>
[CmdletBinding()]
param(
    [switch]$SkipNative,
    [switch]$SkipManaged,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$nativeSource = Join-Path $root 'src\Btwu.CredProv'
$nativeOut = Join-Path $nativeSource 'build\x64'
$harnessSource = Join-Path $nativeSource 'test'
$harnessOut = Join-Path $harnessSource 'build'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "    $text" -ForegroundColor Green }
function Write-Warn([string]$text) { Write-Host "    $text" -ForegroundColor Yellow }

# ---------------------------------------------------------------------------- 工具链定位

function Get-MsvcToolset {
    $parents = @()
    if ($env:ProgramFiles) { $parents += Join-Path $env:ProgramFiles 'Microsoft Visual Studio' }
    if (${env:ProgramFiles(x86)}) { $parents += Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio' }

    $best = $null
    foreach ($parent in $parents) {
        if (-not (Test-Path $parent)) { continue }
        foreach ($edition in Get-ChildItem $parent -Directory -ErrorAction SilentlyContinue) {
            foreach ($year in Get-ChildItem $edition.FullName -Directory -ErrorAction SilentlyContinue) {
                $toolsRoot = Join-Path $year.FullName 'VC\Tools\MSVC'
                if (-not (Test-Path $toolsRoot)) { continue }
                foreach ($versionDir in Get-ChildItem $toolsRoot -Directory -ErrorAction SilentlyContinue) {
                    $version = $null
                    if (-not [version]::TryParse($versionDir.Name, [ref]$version)) { continue }
                    $cl = Join-Path $versionDir.FullName 'bin\Hostx64\x64\cl.exe'
                    if (-not (Test-Path $cl)) { continue }
                    if ($null -eq $best -or $version -gt $best.Version) {
                        $best = [pscustomobject]@{ Version = $version; Dir = $versionDir.FullName; Cl = $cl }
                    }
                }
            }
        }
    }
    return $best
}

function Get-WindowsSdk {
    $candidates = @()
    if (${env:ProgramFiles(x86)}) { $candidates += Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10' }
    if ($env:ProgramFiles) { $candidates += Join-Path $env:ProgramFiles 'Windows Kits\10' }

    $best = $null
    foreach ($sdkRoot in $candidates) {
        $includeRoot = Join-Path $sdkRoot 'Include'
        $libRoot = Join-Path $sdkRoot 'Lib'
        if (-not (Test-Path $includeRoot)) { continue }
        foreach ($versionDir in Get-ChildItem $includeRoot -Directory -ErrorAction SilentlyContinue) {
            $version = $null
            if (-not [version]::TryParse($versionDir.Name, [ref]$version)) { continue }
            if (-not (Test-Path (Join-Path $versionDir.FullName 'um\windows.h'))) { continue }
            if (-not (Test-Path (Join-Path $libRoot "$($versionDir.Name)\um\x64"))) { continue }
            if ($null -eq $best -or $version -gt $best.Version) {
                $best = [pscustomobject]@{
                    Version = $version
                    Include = $versionDir.FullName
                    Lib     = Join-Path $libRoot $versionDir.Name
                }
            }
        }
    }
    return $best
}

function Invoke-NativeBuild([string]$source, [string]$outputName, [string]$outDir, [string[]]$extraArguments) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null

    $arguments = @(
        '/nologo', '/W4', '/EHsc', '/O2', '/MT', '/GS', '/utf-8',
        '/DUNICODE', '/D_UNICODE', '/D_WIN32_WINNT=0x0A00'
    ) + $extraArguments + @(
        $source,
        "/Fe:$outputName",
        '/link'
    )

    if ($outputName -like '*.dll') {
        $arguments += "/DEF:$(Join-Path $source '..\BtwuCredProv.def')"
    }

    # wtsapi32: Btwu::QuerySessionUser in PresenceState.h uses WTSQuerySessionInformationW, and both
    # the DLL and harness.exe include that header.
    $arguments += @('ole32.lib', 'oleaut32.lib', 'advapi32.lib', 'crypt32.lib', 'secur32.lib', 'shlwapi.lib', 'user32.lib', 'wtsapi32.lib')

    # Object files land next to the binary; run from the output directory so no /Fo quoting is needed.
    Push-Location $outDir
    try {
        & $script:clExe @arguments
        if ($LASTEXITCODE -ne 0) { throw "编译 $outputName 失败（退出码 $LASTEXITCODE）" }
    }
    finally {
        Pop-Location
    }
}

# --------------------------------------------------------------------------------- 构建

if (-not $SkipNative) {
    Write-Step '定位 MSVC 与 Windows SDK'
    $msvc = Get-MsvcToolset
    if ($null -eq $msvc) {
        Write-Warn '没找到 MSVC。跳过原生组件；.NET 部分仍会构建。'
        Write-Warn '要装凭据提供程序，请安装 Visual Studio 生成工具 + “使用 C++ 的桌面开发”。'
        $SkipNative = $true
    }
    else {
        $sdk = Get-WindowsSdk
        if ($null -eq $sdk) {
            Write-Warn '没找到 Windows 10/11 SDK（缺 um\windows.h）。跳过原生组件。'
            $SkipNative = $true
        }
        else {
            Write-Ok "MSVC      $($msvc.Version)  $($msvc.Dir)"
            Write-Ok "Windows SDK $($sdk.Version)"

            $env:INCLUDE = @(
                (Join-Path $msvc.Dir 'include')
                (Join-Path $sdk.Include 'ucrt')
                (Join-Path $sdk.Include 'shared')
                (Join-Path $sdk.Include 'um')
                (Join-Path $sdk.Include 'winrt')
            ) -join ';'

            $env:LIB = @(
                (Join-Path $msvc.Dir 'lib\x64')
                (Join-Path $sdk.Lib 'ucrt\x64')
                (Join-Path $sdk.Lib 'um\x64')
            ) -join ';'

            $env:PATH = (Split-Path $msvc.Cl -Parent) + ';' + $env:PATH
            $script:clExe = $msvc.Cl
        }
    }
}

if (-not $SkipNative) {
    Write-Step '编译凭据提供程序 BtwuCredProv.dll'
    Invoke-NativeBuild -source (Join-Path $nativeSource 'BtwuCredProv.cpp') `
                       -outputName 'BtwuCredProv.dll' -outDir $nativeOut -extraArguments @('/LD')
    Write-Ok (Join-Path $nativeOut 'BtwuCredProv.dll')

    Write-Step '编译测试脚手架 harness.exe'
    Invoke-NativeBuild -source (Join-Path $harnessSource 'harness.cpp') `
                       -outputName 'harness.exe' -outDir $harnessOut -extraArguments @()
    Write-Ok (Join-Path $harnessOut 'harness.exe')
}

if (-not $SkipManaged) {
    Write-Step "构建 .NET 工程（$Configuration）"
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
    if ($null -eq $dotnet) {
        $fallback = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
        if (Test-Path $fallback) { $dotnet = Get-Item $fallback }
    }
    if ($null -eq $dotnet) { throw '没找到 dotnet。请安装 .NET 10 SDK。' }

    & $dotnet.Source build (Join-Path $root 'Btwu.sln') -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败（退出码 $LASTEXITCODE）" }

    Write-Step '发布 agent 到 dist\app（安装脚本会把它拷进 Program Files）'
    $appOut = Join-Path $root 'dist\app'
    & $dotnet.Source publish (Join-Path $root 'src\Btwu.Agent\Btwu.Agent.csproj') `
        -c $Configuration -o $appOut -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }
    Write-Ok (Join-Path $appOut 'btwu.exe')
}

if (Test-Path (Join-Path $nativeOut 'BtwuCredProv.dll')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'dist') | Out-Null
    Copy-Item (Join-Path $nativeOut 'BtwuCredProv.dll') (Join-Path $root 'dist\BtwuCredProv.dll') -Force
    Write-Ok (Join-Path $root 'dist\BtwuCredProv.dll')
}

Write-Host ''
Write-Host '构建完成。下一步：以管理员身份运行 scripts\install.ps1' -ForegroundColor Cyan
