# Changelog

## [Unreleased]

## [0.7.0-alpha] - 2026-08-17

### Added

- Persistent English and German application language selection, with English as the
  source and default UI language.
- Overdue open invoices are represented as grouped tasks whose titles show the first
  and last invoice number in the batch.

### Changed

- Contacts, documents, and dunning are now marked as coming soon while those modules
  are redesigned.

## [0.6.1-alpha] - 2026-08-17

### Fixed

- CSV invoice imports now show a compact summary instead of rendering the complete
  parsed file, report validation errors clearly, and display whether the import was
  applied, cancelled, or already present.
- Running the installer manually over an existing installation now offers an in-place
  update while preserving the installation directory and shortcut preference.

## [0.6.0-alpha] - 2026-08-17

### Added

- Native routine editor with recurrence, topics, reminders, and business-day rules.
- Native task editor with filters, manual tasks, status, priority, notes, and checklists.
- Reconnecting live-update subscription for record and task changes.
- Manual update check in Settings that bypasses the automatic-check interval.

## [0.5.0-alpha] - 2026-08-17

### Added

- Native contact and topic forms with client-side validation.
- Editable topic checklists with priority, duration, required, and enabled fields.
- Search and archived-record filtering for data modules.
- Human-readable revision-conflict and active-editor information.

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
