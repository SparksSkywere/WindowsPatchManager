# Windows Patch Manager

Windows software update manager from **Skywere Industries**. Scan installed applications and install available updates using **Windows Package Manager (winget)** and **Chocolatey**.

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
| `dist\WindowsPatchManager.msi` | Full Windows Installer wizard (Welcome → License → **Browse install folder** → Install) |
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
2. **Scan** loads installed packages
3. **Check updates** queries available upgrades
4. Select programs (or **Update all**) to install

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
