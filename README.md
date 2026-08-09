# Windows Patch Manager

Windows software update manager from **Skywere Industries**. Scan installed applications and install available updates using **Windows Package Manager (winget)**, **Microsoft Store** (msstore), **Chocolatey**, **GitHub Releases**, **Windows Subsystem for Linux (WSL)**, **Microsoft Office / Microsoft 365** (Click-to-Run), plus tabs for **driver updates** and **Windows Update / CVE** security scanning (MSRC Critical/Important KBs).

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
2. Use tabs: **Programs** · **Drivers** · **Windows Update / CVE**
3. **Scan** loads the active tab (Windows Update tab also runs the CVE/KB security scan)
4. **Check updates** queries upgrades (winget / Microsoft Store / Chocolatey / GitHub / WSL / Office / WU)
5. Columns show **Current**, **Available**, **Progress %**, **Last updated**
6. Select items (or **Update all**) to install

### GitHub projects
**Options → General → GitHub tracked projects** — one line per repo:

```text
owner/repo|Display Name|.exe
myorg/chronolog|Chronolog|Setup
```

Optional PAT for rate limits / private repos. Enable **self-update** with `owner/repo` for this app when hosted on GitHub.

### Application self-update
On launch (when **Options → Check GitHub for Windows Patch Manager self-update** is on), the app:

1. Checks GitHub Releases for a newer version **before** scanning packages  
2. Prompts **Yes / No** if an update is available  
3. On **Yes** — downloads Setup/MSI, starts the installer (UAC), then exits  
4. On **No** — continues with the normal scan  

Also available under **Help → Check for application update…**.  
Requires a published GitHub Release with `WindowsPatchManager-Setup.exe` or `.msi` assets.

### Extra update sources (Options → Update sources)
| Source | How it works |
|--------|----------------|
| **Microsoft Store** | winget `msstore` source — Store apps are labeled and upgraded with `--source msstore` |
| **WSL** | Platform via `wsl --update` / `Microsoft.WSL`; optional apt/dnf/zypper/pacman upgrades inside distros |
| **Microsoft Office** | Click-to-Run via `OfficeC2RClient.exe /update` when Office/Microsoft 365 is installed |
| **Windows Update / CVE** | Pending WU software patches with CVE + severity + KB; installed-KB inventory; optional [MSRC](https://api.msrc.microsoft.com/) gap scan for missing Critical/Important security KBs |

### CVE / security KB scan
On **Windows Update / CVE** (or initial full scan):

1. Queries Windows Update for pending software (Security / Critical categories first)
2. Extracts **KB** numbers, **CVE-####-…** IDs, and **MSRC severity**
3. Builds an inventory of **already installed KBs**
4. (Optional) Downloads recent MSRC monthly CVRF feeds and lists **required security KBs** for this Windows edition that are not installed and not already pending

Configure under **Options → Update sources** (CVE scanner, prioritize security, security-only, MSRC online, months to scan).

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
