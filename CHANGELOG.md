# Changelog

## [Unreleased]

## [1.0.0-rc1] - 2026-08-17

### Added

- A full-calendar-year task view with monthly sections, workload summary, and status
  and source filters.
- A shared modern popup for both creating and editing tasks.
- Multi-selection with atomic bulk removal of active tasks.

### Changed

- Removed the permanent right-hand task editor in favor of a wide, dashboard-style
  task list matching the Today page.
- Automatic accounting tasks are presented as coherent stage-and-currency work packages
  with invoice counts instead of invoice-number ranges.
- Manual task editing now includes title, description, category, schedule, status, and
  checklist structure.

## [0.9.3-beta] - 2026-08-17

### Changed

- Replaced the technical routine form with a guided task, timing, and recurrence flow.
- Show only recurrence controls relevant to the selected frequency.
- Move optional end date, weekend handling, and reminders into a collapsed advanced section.
- Save routines as direct task generators so their selected priority is authoritative.

### Fixed

- Persist and visibly confirm the selected routine priority in generated tasks and list details.
- Default empty weekly, monthly, and yearly selections from the chosen start date.

## [0.9.2-beta] - 2026-08-17

### Added

- A clearly labelled, confirmed delete action that removes tasks from the active list
  while retaining their revision-safe history.
- Separate date and time controls for task due dates.

### Changed

- Reworked the task module into a calmer master-detail layout with larger rows and columns.
- Moved task status, save, and delete actions next to the selected task.
- Replaced technical legacy routine identifiers with a friendly task label.

### Fixed

- Force button captions to use the button foreground colour, restoring sidebar readability.
- Prevent task detail labels from overlapping in narrow windows.

## [0.9.1-beta] - 2026-08-17

### Fixed

- Load every invoice page instead of silently stopping after the first 500 records.
- Show only unpaid and partially paid invoices in the invoice working view.
- Correct foreground propagation in button templates so sidebar and action text remains readable.

### Changed

- Replaced dashboard connection details and quick links with a calendar-style list of
  today's and overdue tasks.
- Localize canonical payment-reminder and dunning titles on the dashboard.

## [0.9.0-beta] - 2026-08-17

### Added

- Today's estimated office workload and a preview of the longest due task.
- Estimated-minute fields for manual tasks and directly materialized routines.
- Guided payment-reminder completion with a payment-deadline date picker.
- Explicit settlement confirmation for dunning tasks after an invoice CSV import.

### Changed

- Replaced the dense dark interface with a high-contrast, card-based light workspace.
- Reduced the primary navigation to Today, Tasks, Routines, invoice import, and Settings.
- Localized accounting task titles, dates, durations, filters, and common actions.
- Removed task edit-presence noise from the normal workflow.

## [0.8.0-alpha] - 2026-08-17

### Added

- Persistent developer mode for raw record data, revisions, audit entries, and events.
- Automatic edit-presence protection with passive conflict information while records
  are being changed.

### Changed

- Reworked record details to show task-oriented, human-readable information by default.
- Removed the manual start editing, end editing, and editors controls.

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
