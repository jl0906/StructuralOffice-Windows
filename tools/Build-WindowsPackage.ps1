[CmdletBinding()]
param([string]$Runtime = 'win-x64')

$ErrorActionPreference = 'Stop'
$windowsRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $windowsRoot 'artifacts'
$buildRoot = Join-Path $artifactRoot '.build'
$publishRoot = Join-Path $buildRoot 'publish'
$payloadRoot = Join-Path $buildRoot 'installer-payload'
$project = Join-Path $windowsRoot 'src\StructuralOffice.Desktop\StructuralOffice.Desktop.csproj'
$localDotnet = Join-Path $windowsRoot '.dotnet-sdk\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null

& $dotnet publish $project -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Get-ChildItem -LiteralPath $payloadRoot -File | Remove-Item -Force
Copy-Item -LiteralPath (Join-Path $publishRoot 'StructuralOffice.exe') -Destination $payloadRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $payloadRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $payloadRoot
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.cmd') -Destination $payloadRoot

$installer = Join-Path $artifactRoot 'StructuralOffice_Install.exe'
$checksumFile = Join-Path $artifactRoot 'StructuralOffice_Install.exe.sha256'
$legacyArchive = Join-Path $artifactRoot "StructuralOffice-Windows-$Runtime.zip"
$sedFile = Join-Path $buildRoot 'StructuralOffice-Installer.sed'
foreach ($oldArtifact in @($installer, $checksumFile, $legacyArchive, $sedFile)) {
    if (Test-Path -LiteralPath $oldArtifact) {
        Remove-Item -LiteralPath $oldArtifact -Force
    }
}
Get-ChildItem -LiteralPath $artifactRoot -Filter '~StructuralOffice_Install*' -File |
    Remove-Item -Force

$iexpress = Join-Path $env:SystemRoot 'System32\iexpress.exe'
if (-not (Test-Path -LiteralPath $iexpress)) {
    throw 'IExpress is not available on this Windows system.'
}

$sedContent = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=<None>
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
FinishMessage=StructuralOffice wurde installiert und ist im Startmenue verfuegbar.
TargetName=$installer
FriendlyName=StructuralOffice Setup
AppLaunched=cmd.exe /d /c Install.cmd
AdminQuietInstCmd=cmd.exe /d /c Install.cmd -Quiet
UserQuietInstCmd=cmd.exe /d /c Install.cmd -Quiet
FILE0="StructuralOffice.exe"
FILE1="Install.cmd"
FILE2="Install.ps1"
FILE3="Uninstall.ps1"
[SourceFiles]
SourceFiles0=$payloadRoot\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
"@
Set-Content -LiteralPath $sedFile -Value $sedContent -Encoding ASCII
$iexpressStartedAt = [DateTime]::Now.AddSeconds(-2)
& $iexpress /N /Q $sedFile
$deadline = [DateTime]::UtcNow.AddMinutes(5)
$installerReady = $false
while (-not $installerReady -and [DateTime]::UtcNow -lt $deadline) {
    $activePackagers = Get-Process iexpress, makecab -ErrorAction SilentlyContinue |
        Where-Object { $_.StartTime -ge $iexpressStartedAt }
    $temporaryCab = Get-ChildItem -LiteralPath $artifactRoot `
        -Filter '~StructuralOffice_Install.CAB' -File -ErrorAction SilentlyContinue
    $installerFile = Get-Item -LiteralPath $installer -ErrorAction SilentlyContinue
    $installerReady = $null -ne $installerFile -and `
        $installerFile.Length -gt 1MB -and `
        $null -eq $temporaryCab -and `
        $null -eq $activePackagers
    Start-Sleep -Milliseconds 250
}
if (-not $installerReady) {
    throw 'IExpress failed to create StructuralOffice_Install.exe.'
}
Remove-Item -LiteralPath $sedFile -Force
$checksum = $null
$hashDeadline = [DateTime]::UtcNow.AddMinutes(1)
while ($null -eq $checksum -and [DateTime]::UtcNow -lt $hashDeadline) {
    try {
        $checksum = (Get-FileHash -LiteralPath $installer -Algorithm SHA256 `
            -ErrorAction Stop).Hash.ToLowerInvariant()
    } catch [System.IO.IOException] {
        Start-Sleep -Milliseconds 250
    }
}
if ($null -eq $checksum) {
    throw 'The installer remained locked while calculating its SHA-256 checksum.'
}
Set-Content -LiteralPath $checksumFile `
    -Value "$checksum *StructuralOffice_Install.exe" -Encoding ASCII

Write-Host "Windows installer created: $installer"
