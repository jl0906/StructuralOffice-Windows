[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\StructuralOffice'
$startShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\StructuralOffice.lnk'
$uninstallShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\StructuralOffice deinstallieren.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'StructuralOffice.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StructuralOffice'

foreach ($shortcut in @($startShortcut, $uninstallShortcut, $desktopShortcut)) {
    if (Test-Path -LiteralPath $shortcut) {
        Remove-Item -LiteralPath $shortcut -Force
    }
}

if (Test-Path -LiteralPath $uninstallKey) {
    Remove-Item -LiteralPath $uninstallKey -Recurse -Force
}

if (Test-Path -LiteralPath $installRoot) {
    $temporaryUninstaller = Join-Path $env:TEMP 'StructuralOffice-Uninstall.ps1'
    Copy-Item -LiteralPath $PSCommandPath -Destination $temporaryUninstaller -Force
    Start-Process powershell.exe -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command',
        "Start-Sleep -Seconds 1; Remove-Item -LiteralPath '$installRoot' -Recurse -Force; Remove-Item -LiteralPath '$temporaryUninstaller' -Force"
    )
}

Write-Host 'StructuralOffice was removed. Local settings were retained.'
