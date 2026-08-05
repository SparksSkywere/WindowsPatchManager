# Changelog

All notable changes to **Windows Patch Manager** are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/).

## [2.1.10] — 2026-08-05

### Fixed
- Busy overlay shows the spinning swirl again, with **progress % underneath in smaller text** (instead of replacing the spinner).
- Options **Configuration file** path box no longer stuck in light mode (`#FFF8F8F8`); uses theme brushes for dark/era themes.

---

## [2.1.9] — 2026-08-05

### Added

#### Themes
- Full theme catalog, with **System** as the default (follows Windows light/dark app mode → Windows 11 / Windows 11 Dark).
- Selectable era themes in **Options → Appearance**:
  - Windows 95, 98, 2000, XP, Vista, 7, 8, 10, 11, 11 Dark
- Live theme preview in Options; Cancel restores the saved theme.
- Dark title bar / caption colors via DWM when the active theme is a dark surface.
- Fully themed menus, context menus, combo boxes, list headers, status bar, and **scrollbars** (including arrow buttons).

#### UI & workflow
- Column sorting by clicking list headers (with sort indicators).
- Right-click **Install specific version…** (winget version list; supports upgrade/downgrade with force install).
- Right-click **Exclude from updates** (adds program name + package ID to exclusions).
- Right-click **Copy** for cell / package ID / name / versions / full row (Ctrl+C supported).
- Busy overlay with **progress %**, status text, and cancel.
- Per-item **Progress** and **Last updated** columns.
- Tabs: **Programs**, **Drivers**, **Windows Updates / CVE**.
- Empty-state UI: **“No updates available”** with **Show update history** and **Scan again** (Drivers / Windows Updates).
- Update **history** from Windows Update Agent (installed entries); **Back to available updates** banner.
- GitHub feedback / repo / releases links under **Help** and **About**.
- Optional **desktop shortcut** for the app (Options buttons; not forced on every update).
- MSI feature option for desktop shortcut at install time.
- Admin elevation requested once on launch (decline continues; installers may still prompt).

#### Package sources
- **GitHub Releases** tracking (repos list + optional PAT + self-update settings for this app).
- **Windows Update / Drivers** via Windows Update Agent COM API.
- Improved winget **unknown-version** repair (force reinstall, remember fixed versions).
- Winget install of a chosen version; Chocolatey elevation + clearer errors.
- Prefer winget over Chocolatey for the same product to avoid double updates.
- Desktop shortcuts created by package installers can be stripped after update runs (default).

### Fixed
- Scan no longer marks false “Update available” from `winget list` Available column (e.g. WindowsAppRuntime); only **Check updates** (`winget upgrade`) sets update flags.
- List selection highlight stays in sync with checkboxes / multi-select.
- Dark mode coverage for window chrome strips, column headers, title bar, and scrollbars.
- History button no longer shows a blocking “wrong tab” dialog; loads history immediately.
- Installer runs more reliably when MSI is copied off network drives (`Y:\`) to a local path first.

### Changed
- Default exclusions cleared (no built-in program/keyword block list).
- Update confirmation dialog removed (select + Update is enough).
- Version **2.1.9** across app, MSI, and Setup bootstrapper.

### Notes for installers
- Build: `powershell -ExecutionPolicy Bypass -File .\build-installer.ps1`
- Artifacts: `dist\WindowsPatchManager.msi`, `dist\WindowsPatchManager-Setup.exe`
- If `msiexec` fails on a network path, copy the MSI to `%TEMP%` then install.

---

## [2.1.0] — 2026-07-20

### Added
- Full **.NET 8 / WPF** rewrite of Windows Patch Manager (replaces the Python edition).
- Scan / check / update via **winget** and **Chocolatey**.
- GUI with selection, export, activity log, Options.
- CLI flags (`--scan`, `--check-updates`, `--update-all`, `--export`, schedule helpers).
- WiX MSI + Burn Setup packaging (`build-installer.ps1`).
- Publisher branding: **Skywere Industries**.

### Changed
- No Python runtime required; native Windows executable.
- Config under `%APPDATA%\Skywere Industries\WindowsPatchManager\`.

---

## [1.x] — Python edition (retired)

Previous Python-based Patch Manager (GUI + winget/Chocolatey helpers). Superseded by the .NET release.

---

[2.1.10]: https://github.com/SparksSkywere/WindowsPatchManager/releases/tag/v2.1.10
[2.1.9]: https://github.com/SparksSkywere/WindowsPatchManager/releases/tag/v2.1.9
[2.1.0]: https://github.com/SparksSkywere/WindowsPatchManager/releases/tag/v2.1.0
