[CmdletBinding()]
param([switch]$Quiet)

$ErrorActionPreference = 'Stop'
$applicationVersion = '0.7.0-alpha'
$nestedPayload = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'app'))
$packageRoot = if (Test-Path -LiteralPath (Join-Path $nestedPayload 'StructuralOffice.exe')) {
    $nestedPayload
} else {
    [System.IO.Path]::GetFullPath($PSScriptRoot)
}
$payload = Join-Path $packageRoot 'StructuralOffice.exe'
$defaultInstallRoot = Join-Path $env:LOCALAPPDATA 'Programs\StructuralOffice'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$startShortcut = Join-Path $startMenu 'StructuralOffice.lnk'
$uninstallShortcut = Join-Path $startMenu 'Uninstall StructuralOffice.lnk'
$desktopShortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'StructuralOffice.lnk'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\StructuralOffice'

if (-not (Test-Path -LiteralPath $payload)) {
    throw 'The application payload is missing.'
}

function Get-ExistingInstallation {
    $registered = Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue
    $registeredRoot = [string]$registered.InstallLocation
    if ($registeredRoot -and (Test-Path -LiteralPath (Join-Path $registeredRoot 'StructuralOffice.exe'))) {
        return [System.IO.Path]::GetFullPath($registeredRoot)
    }
    if (Test-Path -LiteralPath (Join-Path $defaultInstallRoot 'StructuralOffice.exe')) {
        return [System.IO.Path]::GetFullPath($defaultInstallRoot)
    }
    return $null
}

function Get-ExistingInstallationChoice([string]$installRoot) {
    Add-Type -AssemblyName System.Windows.Forms | Out-Null
    $choice = [System.Windows.Forms.MessageBox]::Show(
        "StructuralOffice is already installed.`r`n`r`n$installRoot`r`n`r`n" +
        "Yes: Update the existing installation`r`n" +
        "No: Open the installed application`r`n" +
        "Cancel: Exit setup`r`n`r`n" +
        "StructuralOffice will close automatically during the update.",
        'StructuralOffice Setup',
        [System.Windows.Forms.MessageBoxButtons]::YesNoCancel,
        [System.Windows.Forms.MessageBoxIcon]::Information
    )
    if ($choice -eq [System.Windows.Forms.DialogResult]::Yes) {
        return 'update'
    }
    if ($choice -eq [System.Windows.Forms.DialogResult]::No) {
        return 'open'
    }
    return 'cancel'
}

function Stop-InstalledApplication([string]$installRoot) {
    $target = [System.IO.Path]::GetFullPath((Join-Path $installRoot 'StructuralOffice.exe'))
    $running = @(Get-Process -Name 'StructuralOffice' -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                $_.Path -and [System.IO.Path]::GetFullPath($_.Path) -eq $target
            } catch {
                $false
            }
        })
    foreach ($process in $running) {
        if ($process.CloseMainWindow()) {
            try {
                Wait-Process -Id $process.Id -Timeout 5 -ErrorAction Stop
                continue
            } catch {
                # Fall back to stopping the process if it did not close in time.
            }
        }
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
    }
}

function Show-InstallDialog([string]$defaultPath) {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'StructuralOffice Setup'
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ClientSize = New-Object System.Drawing.Size(620, 250)
    $form.Font = New-Object System.Drawing.Font('Segoe UI', 10)
    $form.Icon = [System.Drawing.Icon]::ExtractAssociatedIcon($payload)

    $title = New-Object System.Windows.Forms.Label
    $title.Text = 'Install StructuralOffice 0.7.0-alpha'
    $title.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 15)
    $title.AutoSize = $true
    $title.Location = New-Object System.Drawing.Point(24, 20)
    $form.Controls.Add($title)

    $pathLabel = New-Object System.Windows.Forms.Label
    $pathLabel.Text = 'Installation folder'
    $pathLabel.AutoSize = $true
    $pathLabel.Location = New-Object System.Drawing.Point(26, 72)
    $form.Controls.Add($pathLabel)

    $pathBox = New-Object System.Windows.Forms.TextBox
    $pathBox.Text = $defaultPath
    $pathBox.Location = New-Object System.Drawing.Point(29, 98)
    $pathBox.Size = New-Object System.Drawing.Size(465, 28)
    $form.Controls.Add($pathBox)

    $browseButton = New-Object System.Windows.Forms.Button
    $browseButton.Text = 'Browse...'
    $browseButton.Location = New-Object System.Drawing.Point(504, 96)
    $browseButton.Size = New-Object System.Drawing.Size(95, 31)
    $browseButton.Add_Click({
        $browser = New-Object System.Windows.Forms.FolderBrowserDialog
        $browser.Description = 'Select the StructuralOffice installation folder'
        $browser.SelectedPath = $pathBox.Text
        if ($browser.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
            $pathBox.Text = $browser.SelectedPath
        }
    })
    $form.Controls.Add($browseButton)

    $desktopBox = New-Object System.Windows.Forms.CheckBox
    $desktopBox.Text = 'Create desktop shortcut'
    $desktopBox.Checked = $true
    $desktopBox.AutoSize = $true
    $desktopBox.Location = New-Object System.Drawing.Point(29, 145)
    $form.Controls.Add($desktopBox)

    $installButton = New-Object System.Windows.Forms.Button
    $installButton.Text = 'Install'
    $installButton.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $installButton.Location = New-Object System.Drawing.Point(375, 195)
    $installButton.Size = New-Object System.Drawing.Size(105, 34)
    $form.AcceptButton = $installButton
    $form.Controls.Add($installButton)

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = 'Cancel'
    $cancelButton.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $cancelButton.Location = New-Object System.Drawing.Point(494, 195)
    $cancelButton.Size = New-Object System.Drawing.Size(105, 34)
    $form.CancelButton = $cancelButton
    $form.Controls.Add($cancelButton)

    if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        return $null
    }
    return [pscustomobject]@{
        InstallRoot = $pathBox.Text.Trim()
        DesktopShortcut = $desktopBox.Checked
    }
}

function Resolve-SafeInstallRoot([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw 'Select an installation folder.'
    }
    $resolved = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($value))
    $volumeRoot = [System.IO.Path]::GetPathRoot($resolved)
    if ($resolved.TrimEnd('\') -eq $volumeRoot.TrimEnd('\')) {
        throw 'A drive root cannot be used as the installation folder.'
    }
    return $resolved
}

function Copy-ApplicationWithRetry([string]$source, [string]$destination) {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try {
            Copy-Item -LiteralPath $source -Destination $destination -Force
            return
        } catch [System.IO.IOException] {
            if ($attempt -eq 39) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
}

$existingInstallRoot = Get-ExistingInstallation
if ($existingInstallRoot -and -not $Quiet) {
    $existingChoice = Get-ExistingInstallationChoice $existingInstallRoot
    if ($existingChoice -eq 'open') {
        Start-Process -FilePath (Join-Path $existingInstallRoot 'StructuralOffice.exe')
        exit 0
    }
    if ($existingChoice -ne 'update') {
        exit 0
    }
}

if ($Quiet) {
    if (-not $existingInstallRoot) {
        throw 'A quiet update requires an existing StructuralOffice installation.'
    }
    $installRoot = $existingInstallRoot
    $createDesktopShortcut = Test-Path -LiteralPath $desktopShortcutPath
} elseif ($existingInstallRoot) {
    $installRoot = $existingInstallRoot
    $createDesktopShortcut = Test-Path -LiteralPath $desktopShortcutPath
} else {
    $selection = Show-InstallDialog $defaultInstallRoot
    if ($null -eq $selection) { exit 0 }
    $installRoot = Resolve-SafeInstallRoot $selection.InstallRoot
    $createDesktopShortcut = $selection.DesktopShortcut
}

if ($existingInstallRoot) {
    Stop-InstalledApplication $existingInstallRoot
}
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
Copy-ApplicationWithRetry $payload (Join-Path $installRoot 'StructuralOffice.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $installRoot -Force

$shell = New-Object -ComObject WScript.Shell
$appShortcut = $shell.CreateShortcut($startShortcut)
$appShortcut.TargetPath = Join-Path $installRoot 'StructuralOffice.exe'
$appShortcut.WorkingDirectory = $installRoot
$appShortcut.Description = 'StructuralOffice for Windows'
$appShortcut.Save()

if ($createDesktopShortcut) {
    $desktopLink = $shell.CreateShortcut($desktopShortcutPath)
    $desktopLink.TargetPath = Join-Path $installRoot 'StructuralOffice.exe'
    $desktopLink.WorkingDirectory = $installRoot
    $desktopLink.Description = 'StructuralOffice for Windows'
    $desktopLink.Save()
} elseif (Test-Path -LiteralPath $desktopShortcutPath) {
    Remove-Item -LiteralPath $desktopShortcutPath -Force
}

$uninstallLink = $shell.CreateShortcut($uninstallShortcut)
$uninstallLink.TargetPath = 'powershell.exe'
$uninstallLink.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installRoot 'Uninstall.ps1')`""
$uninstallLink.WorkingDirectory = $installRoot
$uninstallLink.Description = 'Uninstall StructuralOffice'
$uninstallLink.Save()

New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'StructuralOffice' -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $applicationVersion -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'StructuralOffice' -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installRoot -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name UninstallString `
    -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $installRoot 'Uninstall.ps1')`"" `
    -Force | Out-Null

Start-Process -FilePath (Join-Path $installRoot 'StructuralOffice.exe')
