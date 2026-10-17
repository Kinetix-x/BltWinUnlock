# CdfToggle.ps1 - diagnostic switch for the Windows Companion Device Framework (CDF)
# credential provider {48B4E58D-2791-456C-9091-D524C6C706F2}
#
# WHY: on this machine that key ships with "Disabled" = 1 (verified to be the OS image
#      default via registry last-write timestamps). It is NOT known whether that value
#      actually blocks CDF. This tool exists to answer exactly that question, reversibly.
#
# USAGE (run -Enable / -Restore from an ELEVATED PowerShell):
#   powershell -ExecutionPolicy Bypass -File CdfToggle.ps1 -Status
#   powershell -ExecutionPolicy Bypass -File CdfToggle.ps1 -Enable
#   <<< lock the screen, look at the sign-in screen / Sign-in options >>>
#   powershell -ExecutionPolicy Bypass -File CdfToggle.ps1 -Restore
#
# WHAT THIS DOES *NOT* DO: it will never make a Bluetooth keyboard unlock anything.
# The CDF path additionally requires a peer device that answers the Huami challenge
# response and an app holding the secondaryAuthenticationFactor capability.
#
# This script only uses ASCII so that Windows PowerShell 5.1 (ANSI/GBK script decoding)
# cannot mangle it.

[CmdletBinding()]
param(
    [switch]$Status,
    [switch]$Enable,
    [switch]$Restore,
    [switch]$AllowPolicy
)

$ErrorActionPreference = 'Stop'

$CpKey     = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{48B4E58D-2791-456C-9091-D524C6C706F2}'
$CpKeyReg  = 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{48B4E58D-2791-456C-9091-D524C6C706F2}'
$PolicyKey = 'HKLM:\SOFTWARE\Policies\Microsoft\SecondaryAuthenticationFactor'
$PolicyReg = 'HKLM\SOFTWARE\Policies\Microsoft\SecondaryAuthenticationFactor'
$StateKey  = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\SecondaryAuthFactor'
$PolicyVal = 'AllowSecondaryAuthenticationDevice'

$BackupDir = Join-Path $PSScriptRoot 'cdf-backup'
$BackupReg = Join-Path $BackupDir 'cdf-credprov.reg'
$PolicyRegFile = Join-Path $BackupDir 'cdf-policy.reg'

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p  = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-Status {
    Write-Host '--- elevation ---'
    Write-Host ("  elevated            : " + (Test-Elevated))

    Write-Host '--- CDF credential provider key ---'
    if (Test-Path $CpKey) {
        $p = Get-ItemProperty $CpKey
        Write-Host ("  key exists          : True")
        Write-Host ("  DisplayName         : " + $p.'(default)')
        $d = $p.Disabled
        if ($null -eq $d) { Write-Host "  Disabled            : (absent)" }
        else               { Write-Host ("  Disabled            : " + $d) }
    } else {
        Write-Host "  key exists          : False"
    }

    Write-Host '--- policy key the provider reads ---'
    Write-Host ("  " + $PolicyReg + " exists = " + (Test-Path $PolicyKey))
    if (Test-Path $PolicyKey) {
        Get-ItemProperty $PolicyKey | Out-String | Write-Host
    }

    Write-Host '--- enrolled companion devices ---'
    Write-Host ("  " + $StateKey.Replace('HKLM:\','') + " exists = " + (Test-Path $StateKey))
    if (Test-Path $StateKey) {
        $kids = @(Get-ChildItem $StateKey -ErrorAction SilentlyContinue)
        Write-Host ("  enrolled subkeys    : " + $kids.Count)
        foreach ($k in $kids) { Write-Host ("    " + $k.PSChildName) }
    }

    Write-Host '--- backup present? ---'
    Write-Host ("  " + $BackupReg + " = " + (Test-Path $BackupReg))
}

function Backup-All {
    if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null }
    if (Test-Path $BackupReg) { Remove-Item $BackupReg -Force }
    & reg.exe export $CpKeyReg $BackupReg /y | Out-Null
    if (-not (Test-Path $BackupReg)) { throw "reg export failed for $CpKeyReg" }
    Write-Host "  backup written      : $BackupReg"
}

function Enable-Cdf {
    if (-not (Test-Elevated)) { throw 'This action requires an elevated PowerShell. Run -Status instead.' }

    Write-Host '=== step 1: backup ==='
    Backup-All

    Write-Host '=== step 2: set Disabled = 0 ==='
    if (-not (Test-Path $CpKey)) { throw "credential provider key not found: $CpKey" }
    Set-ItemProperty -Path $CpKey -Name 'Disabled' -Value 0 -Type DWord
    Write-Host ("  Disabled            : " + (Get-ItemProperty $CpKey).Disabled)

    if ($AllowPolicy) {
        Write-Host '=== step 3 (optional): create the AllowSecondaryAuthenticationDevice policy ==='
        Write-Host '  WARNING: the semantics of this policy are NOT verified. Absence may already mean'
        Write-Host '           "allowed". Creating it is a guess; use -Restore to undo.'
        if (Test-Path $PolicyKey) {
            & reg.exe export $PolicyReg $PolicyRegFile /y | Out-Null
            Write-Host "  policy backup       : $PolicyRegFile"
        } else {
            Write-Host '  policy key did not exist before (nothing to back up)'
        }
        if (-not (Test-Path $PolicyKey)) { New-Item -Path $PolicyKey -Force | Out-Null }
        New-ItemProperty -Path $PolicyKey -Name $PolicyVal -Value 1 -PropertyType DWord -Force | Out-Null
        Write-Host ("  " + $PolicyVal + "  = 1")
    }

    Write-Host ''
    Write-Host 'NEXT:'
    Write-Host '  1) Press Win+L to lock the screen.'
    Write-Host '  2) Look at the sign-in screen AND at Settings > Accounts > Sign-in options.'
    Write-Host '     Question: does a wearable / companion device entry appear?'
    Write-Host '     - appears      -> the Disabled flag WAS the blocker; CDF is still alive on this build'
    Write-Host '     - absent       -> something deeper disables CDF; this route is closed'
    Write-Host '  3) Run with -Restore to put everything back.'
}

function Restore-All {
    if (-not (Test-Elevated)) { throw 'This action requires an elevated PowerShell.' }

    Write-Host '=== restore ==='
    if (Test-Path $BackupReg) {
        & reg.exe import $BackupReg | Out-Null
        Write-Host "  imported            : $BackupReg"
        Write-Host ("  Disabled now        : " + (Get-ItemProperty $CpKey).Disabled)
    } else {
        Write-Host "  no backup at $BackupReg - falling back to setting Disabled = 1"
        Set-ItemProperty -Path $CpKey -Name 'Disabled' -Value 1 -Type DWord
    }

    if (Test-Path $PolicyRegFile) {
        & reg.exe import $PolicyRegFile | Out-Null
        Write-Host "  policy restored     : $PolicyRegFile"
    } elseif (Test-Path $PolicyKey) {
        Write-Host '  removing the policy key created by -AllowPolicy'
        Remove-Item $PolicyKey -Recurse -Force
    } else {
        Write-Host '  policy key absent   : nothing to do'
    }
    Write-Host '  done'
}

if ($Enable)  { Enable-Cdf }
elseif ($Restore) { Restore-All }
else { Show-Status }
