# StructuralOffice for Windows

This standalone project directory contains the native Windows client. It is deliberately
separate from the Home Assistant integration source. The first development milestone
connects to Home Assistant and checks whether the StructuralOffice integration is
installed and configured.

## Run during development

The application requires the .NET 8 SDK on Windows:

```powershell
dotnet run --project .\src\StructuralOffice.Desktop\StructuralOffice.Desktop.csproj
```

Enter the base URL of Home Assistant (for example `http://homeassistant.local:8123`)
and sign in through Home Assistant's OAuth page in the system browser. StructuralOffice
never receives or stores the user's password or two-factor code. When **Stay signed in**
is selected, the refresh token is stored in Windows Credential Manager and exchanged
for a short-lived access token at application startup.

## Build an installable package

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tools\Build-WindowsPackage.ps1
```

The command creates the self-contained installer `artifacts/StructuralOffice_Install.exe`.
Run this EXE to install StructuralOffice for the current user. The setup dialog allows
the installation directory and optional desktop shortcut to be selected. It creates
Start-menu shortcuts and registers the program under Installed Apps. Starting the
installer interactively when StructuralOffice is already registered opens the existing
program instead of reinstalling it. Verified automatic updates use the quiet installer
mode and keep the registered location and desktop-shortcut choice.

## Automatic updates

The application checks the dedicated GitHub repository
`jl0906/StructuralOffice-Windows` at startup, at most once every 12 hours:

```text
https://api.github.com/repos/jl0906/StructuralOffice-Windows/releases
```

Publishing contract:

1. Increment `<Version>` in `StructuralOffice.Desktop.csproj`.
2. Build `StructuralOffice_Install.exe`.
3. Create a GitHub release tagged `v<Version>` in the Windows repository.
4. Upload the installer with the exact asset name `StructuralOffice_Install.exe`.

The updater only accepts a newer semantic version, HTTPS downloads hosted by GitHub,
the exact asset name, and GitHub's matching SHA-256 asset digest. Alpha/beta clients may
receive prereleases; stable clients ignore prereleases. A verified update is installed
silently for the current user and the updated application starts again automatically.
Failures never prevent startup and are logged to
`%LOCALAPPDATA%\StructuralOffice\Logs\updater.log`.

## Architecture

`IStructuralOfficeBackend` is the application boundary. `HomeAssistantBackend` is the
first implementation. A future standalone release can implement the same interface
with a local service and database without coupling the user interface to Home
Assistant HTTP endpoints.
