# Windows Patch Manager

Windows software update manager from **Skywere Industries**. Scan installed applications and install available updates using **Windows Package Manager (winget)**, **Chocolatey**, **GitHub Releases**, plus tabs for **driver updates** and **Windows Update / CVE patches**.

This is the .NET release of Windows Patch Manager (replacing the earlier Python edition).

## Requirements

- Windows 10/11
- [Windows Package Manager (winget)](https://learn.microsoft.com/windows/package-manager/winget/) — recommended  
- [Chocolatey](https://chocolatey.org/) — optional  
- For building: [.NET 8 SDK](https://dotnet.microsoft.com/download)

## Install (release packages)

Build installers:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

| File | Notes |
|------|--------|
| `dist\WindowsPatchManager.msi` | Full Windows Installer wizard (Welcome → License → **features** — optional **Desktop shortcut**, install folder → Install) |
| `dist\WindowsPatchManager-Setup.exe` | WiX Burn setup; use **Options** to set the install folder |

```text
msiexec /i WindowsPatchManager.msi
WindowsPatchManager-Setup.exe
msiexec /i WindowsPatchManager.msi /qn
msiexec /x WindowsPatchManager.msi
```

Default location: `Program Files\WindowsPatchManager`.  
Publisher in Apps & Features: **Skywere Industries**.

## Usage

### GUI

1. Launch **Windows Patch Manager**
2. Use tabs: **Programs** · **Drivers** · **Windows Updates / CVE**
3. **Scan** loads the active tab
4. **Check updates** queries upgrades (winget / Chocolatey / GitHub / WU)
5. Columns show **Current**, **Available**, **Progress %**, **Last updated**
6. Select items (or **Update all**) to install

### GitHub projects
**Options → General → GitHub tracked projects** — one line per repo:

```text
owner/repo|Display Name|.exe
myorg/chronolog|Chronolog|Setup
```

Optional PAT for rate limits / private repos. Enable **self-update** with `owner/repo` for this app when hosted on GitHub.

Shortcuts: `F5` scan · `F6` check updates · `Ctrl+S` export · `Esc` cancel  

**Help → About Windows Patch Manager** shows version and publisher information.

### CLI

```text
WindowsPatchManager.exe --scan --no-ui
WindowsPatchManager.exe --check-updates --no-ui
WindowsPatchManager.exe --list-updates --no-ui
WindowsPatchManager.exe --update-all --no-confirm --no-ui
WindowsPatchManager.exe --export programs.json --no-ui
WindowsPatchManager.exe --schedule-create
```

## Configuration

`%APPDATA%\Skywere Industries\WindowsPatchManager\config.json`

## Architecture

```
src/ApplicationUpdater/          WPF application (WindowsPatchManager.exe)
installer/wix/                   WiX MSI + Burn Setup
build-installer.ps1              Release packaging
```

## Publisher

**Skywere Industries**
