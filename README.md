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

After authentication, the native application shell shows the verified Home Assistant
and StructuralOffice status on its dashboard. Version `1.0.0-rc1` is designed for the
StructuralOffice Home Assistant integration `0.9.1-beta` and provides:

- a focused daily dashboard with today's estimated workload, longest due task, and a
  calendar-style list of today's and overdue tasks;
- complete paginated invoice loading with an open-balance-only working view;
- readable task lists with friendly dates, duration estimates, and localized titles;
- guided payment-reminder completion that schedules the matching dunning task;
- explicit confirmation before a fully paid invoice range closes its dunning task;
- native routine scheduling without required topic assignments, with recurrence rules,
  priority, estimated duration, reminders,
  catch-up behavior, and business-day handling;
- native task filters and editors for manual tasks, due dates, status, priority,
  completion notes, and checklist progress;
- reconnecting live updates for the currently open module;
- CSV and Excel invoice preview/import plus CSV, Excel, and template export;
- revision-safe background synchronization and reconnecting live updates.

The primary navigation deliberately concentrates on Today, Tasks, Routines, invoice
import, and Settings. Contacts, topics, document generation, and technical administration
are not part of the beta's main workflow.

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
`jl0906/StructuralOffice-Windows` at startup, at most once every 12 hours. A manual
check that bypasses this interval is available under **Einstellungen**:

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

`IStructuralOfficeBackend` and `IStructuralOfficeDataBackend` form the application
boundary. `HomeAssistantBackend` is the first implementation and combines the
authenticated REST and WebSocket contracts. A future standalone release can implement
the same domain operations with a local service and database.
