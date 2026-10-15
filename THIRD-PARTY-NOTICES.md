# Third-party software

Mountloom's own source code is licensed under GPL-3.0-only; see `LICENSE`. It uses the components below, whose licenses apply independently. License texts and upstream copyright notices must accompany redistributed components.

| Component | Use | License and provenance |
| --- | --- | --- |
| [.NET Runtime](https://github.com/dotnet/runtime/tree/v10.0.13), [WPF](https://github.com/dotnet/wpf/tree/v10.0.13), [Windows Forms](https://github.com/dotnet/winforms/tree/v10.0.13) 10.0.13 | Self-contained Windows runtime and desktop UI | MIT and component-specific notices in `third-party/dotnet/{runtime,wpf,winforms}`. The distribution also retains the Microsoft .NET Library terms and notices shipped with the SDK. |
| [System.Security.Cryptography.ProtectedData](https://github.com/dotnet/runtime/tree/v10.0.0/src/libraries/System.Security.Cryptography.ProtectedData) 10.0.0 | Windows DPAPI credential protection | MIT; .NET Runtime 10.0.0 license and notices are in `third-party/dotnet/protecteddata`. |
| [rclone](https://github.com/rclone/rclone/tree/v1.75.2) 1.75.2 | Separate process for remote access and mounting | MIT. The portable package retains the upstream `README.txt`, `git-log.txt` and the license section from that README under `third-party/rclone`. See also [upstream licensing information](https://rclone.org/licence/). |
| [WinFsp](https://github.com/winfsp/winfsp/tree/v2.2B4) 2.2.26215 (2026 Beta4) | Shared Windows file-system prerequisite; original MSI bundled only by the installer | GPL-3.0 with the upstream additional permissions for FLOSS. Original license and source references are in `installer/third-party/winfsp` in source and `third-party/winfsp` in the installer payload. |

The pinned .NET SDK is 10.0.402. Runtime notices correspond to 10.0.13; when upgrading the SDK or runtime, update the corresponding notices together. Original source URLs and downloaded file hashes are recorded in `third-party/dotnet/SOURCE.txt`.

## WinFsp attribution and redistribution

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — [WinFsp repository](https://github.com/winfsp/winfsp).

The upstream FLOSS exception permits linking with the platform DLL and redistribution of the unmodified official installer under its stated conditions. These include an eligible open-source license for the distributing software, this attribution and repository link in the user interface and user-facing documentation, and restrictions on combining it with proprietary software. Mountloom's license does not remove WinFsp's conditions. Read the [complete upstream license](https://github.com/winfsp/winfsp/blob/v2.2B4/License.txt) before redistributing a modified bundle.

The portable archive does not contain the WinFsp driver. The offline installer embeds the original, hash-verified MSI and retains its license and source references. WinFsp 2026 Beta4 is an upstream prerelease.

## Build and test tools

[Inno Setup](https://github.com/jrsoftware/issrc/tree/is-7_1_0) 7.1.0 builds the installer; its compiler is cached locally in `.tools` and is not part of the source checkout. Its license is available [upstream](https://github.com/jrsoftware/issrc/blob/is-7_1_0/license.txt).

[Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest), [xUnit](https://github.com/xunit/xunit) and its [Visual Studio runner](https://github.com/xunit/visualstudio.xunit) are test dependencies under their respective upstream licenses. They are not included in the application publication. No font files are bundled; the UI references Windows system fonts.
