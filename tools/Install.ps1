[CmdletBinding()]
param([switch]$DesktopShortcut)

$ErrorActionPreference = 'Stop'
$nestedPayload = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'app'))
$packageRoot = if (Test-Path -LiteralPath (Join-Path $nestedPayload 'StructuralOffice.exe')) {
    $nestedPayload
} else {
    [System.IO.Path]::GetFullPath($PSScriptRoot)
}
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\StructuralOffice'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startShortcut = Join-Path $startMenu 'StructuralOffice.lnk'
$uninstallShortcut = Join-Path $startMenu 'StructuralOffice deinstallieren.lnk'
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'StructuralOffice.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StructuralOffice'

if (-not (Test-Path -LiteralPath (Join-Path $packageRoot 'StructuralOffice.exe'))) {
    throw 'The application payload is missing. Extract the complete package first.'
}

New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $packageRoot 'StructuralOffice.exe') -Destination $installRoot -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $installRoot -Force

$shell = New-Object -ComObject WScript.Shell
foreach ($shortcutPath in @($startShortcut) + $(if ($DesktopShortcut) { $desktopShortcutPath })) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = Join-Path $installRoot 'StructuralOffice.exe'
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.Description = 'StructuralOffice for Windows'
    $shortcut.Save()
}

$uninstallLink = $shell.CreateShortcut($uninstallShortcut)
$uninstallLink.TargetPath = 'powershell.exe'
$uninstallLink.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installRoot 'Uninstall.ps1')`""
$uninstallLink.WorkingDirectory = $installRoot
$uninstallLink.Description = 'StructuralOffice deinstallieren'
$uninstallLink.Save()

New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'StructuralOffice' -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '0.1.0-alpha' -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'StructuralOffice' -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installRoot -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name UninstallString `
    -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installRoot 'Uninstall.ps1')`"" `
    -Force | Out-Null

Write-Host "StructuralOffice installed in $installRoot"
Write-Host 'Open it from the Start menu.'
Start-Process -FilePath (Join-Path $installRoot 'StructuralOffice.exe')
