# Windows installer

Build the self-contained package with `scripts/publish.ps1`, then run:

```powershell
.\scripts\build-installer.ps1 -AppDirectory .\artifacts\Mountloom-win-x64
```

Without `-AppDirectory`, the script publishes and tests first. `-SkipTests` applies to that publication only. Outputs are `artifacts/Mountloom-Setup-x64.exe`, its `.sha256`, and `.manifest.json`. Building never installs Mountloom or WinFsp on the build machine. The Inno Setup compiler is prepared locally in `.tools` using its official portable mode if necessary.

The installer embeds the self-contained .NET application, verified rclone 1.75.2, and WinFsp MSI. End-user installation therefore works without downloading dependencies or configuring PATH. Windows 10 22H2 x64 or Windows 11 x64 is required. Installation requests administrator rights, writes to Program Files, registers an uninstaller, and creates a Start menu shortcut; the desktop shortcut is optional. Open Mountloom from the Start menu after installation, using the normal user account.

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos. [WinFsp project and source](https://github.com/winfsp/winfsp). WinFsp is distributed under GPLv3 with its additional permission for FLOSS applications; see [the bundled license](third-party/winfsp/LICENSE.txt).

An existing WinFsp installation with its x64 runtime DLL is reused, including installations using `SxsDir` and either registry view. Setup does not upgrade it. If it is absent, Setup installs the embedded WinFsp runtime with MSI's default runtime features, suppressed restart, and Restart Manager shutdown disabled. MSI codes 3010 and 1641 mark installation as needing a restart; all other nonzero codes stop installation before app deployment. The original driver installer is hash-checked again after extraction. WinFsp logs retain the existing location `%ProgramData%\Mountly\InstallerLogs`.

Setup and uninstall refuse to continue while Mountloom, an older Mountly version, or a rclone process from the installation directory is running. They never terminate mount processes. Exit through the application's **Exit and unmount** command before upgrading or uninstalling. Uninstall removes the installed application files and shortcuts but keeps the shared WinFsp driver, user settings, protected credentials, and upload caches.

Mountloom keeps the existing installer AppId and setup mutex so it can upgrade Mountly safely. Upgrades reuse the previously installed directory; fresh installations default to `Program Files\Mountloom`. After the new application files are installed, Setup removes only the known old Mountly executables, assemblies, symbols and runtime metadata from that directory, plus old shortcuts whose targets still point to that installation. User configuration and caches keep their existing Mountly paths. Setup does not rewrite per-user startup registration; re-enable the application's startup option if upgrading from Mountly.

## Pinned build inputs

| Input | Official source | SHA-256 |
| --- | --- | --- |
| Inno Setup 7.1.0 x64 | [Immutable upstream release](https://github.com/jrsoftware/issrc/releases/tag/is-7_1_0) | `0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f` |
| WinFsp 2.2.26215 | [WinFsp 2026 Beta4 release and published checksums](https://github.com/winfsp/winfsp/releases/tag/v2.2B4) | `2ecb5c89405488a95bbd8a01875e02c48534fd37bbdfd84488f7590464d65944` |

WinFsp 2026 Beta4 is explicitly a prerelease. It is pinned here because the upstream release includes fixes for the vulnerabilities listed in its release notes. Already installed versions are preserved. The application package pins rclone independently and is checked again before compilation.

The compiler executable from that Inno Setup archive has SHA-256 `d06ebd38f38e3cee60a3c50cc45bd449d77e0bc6a5cabc607ea9886808e4de1a`. Downloads are staged, hash-checked, then cached. Source and license links for bundled WinFsp are installed under `third-party/winfsp`; .NET and rclone notices come from the application publication.

See Inno Setup's [portable-mode documentation](https://jrsoftware.org/ishelp/topic_technotes.htm), [prerequisite installation events](https://jrsoftware.org/ishelp/topic_scriptevents.htm), and the pinned [WinFsp MSI source](https://github.com/winfsp/winfsp/blob/v2.2B4/build/VStudio/installer/Product.wxs).

## Validation

Compiler success verifies the installer script and embedded payload. Inspect the generated manifest and checksum to identify the exact output. To validate installation behavior, use a disposable Windows VM and cover: clean offline install; reuse of existing WinFsp; active mounts blocking upgrade/uninstall; MSI failure; MSI restart-required result; normal upgrade; uninstall retaining user data. These scenarios must not be simulated by installing or uninstalling the real driver on a developer's machine.

Unattended deployments can run `Mountloom-Setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RESTARTEXITCODE=3010`. A restart-required result is reported as 3010; prerequisite failure returns Inno Setup's nonzero preparation error code. Setup does not launch the application as the elevated installer account.
