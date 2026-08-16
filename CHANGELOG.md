# Changelog

## [Unreleased]

## [0.4.0-alpha] - 2026-08-16

### Added

- New StructuralOffice application icon for the executable, window, installer, and shortcuts.
- Revision-safe CRUD and edit-presence workflows for contacts, topics, routines,
  occurrences, and invoices.
- Invoice CSV/Excel import, CSV/Excel export, templates, and batch document generation.
- Accounting task batches, invoice membership, and editable escalation rules.
- Administration for roles, backups, restore/download/delete, notifications, audit,
  and persisted change events.

## [0.3.0-alpha] - 2026-08-16

### Added

- Native WPF desktop foundation.
- Home Assistant connectivity, token, and StructuralOffice integration checks.
- Backend abstraction with a placeholder for a future standalone service.
- Self-contained per-user installer named `StructuralOffice_Install.exe`.
- Automatic GitHub Releases updater with semantic-version channel selection, HTTPS host
  restrictions, SHA-256 verification, silent installation, and restart.
- Home Assistant OAuth login in the system browser with optional automatic login via a
  refresh token protected by Windows Credential Manager.
- Installer selection for the destination directory and desktop shortcut.
- Existing-installation detection that prevents an interactive reinstall while allowing
  verified quiet updates to retain the original installation location.
- Authenticated desktop shell with dashboard, live integration status, and navigation
  foundations for contacts, topics, routines, tasks, invoices, documents, and settings.
