# Mountloom

Mountloom 是一款 Windows 桌面远程存储挂载工具，可以将 WebDAV、SFTP、S3 和 FTP 连接挂载为资源管理器中的盘符，通过熟悉的文件操作访问远程目录。

A Windows desktop app for mounting WebDAV, SFTP, S3 and FTP storage as drive letters.

项目使用 WPF 和 .NET 构建桌面界面，由 rclone 处理远程存储访问，WinFsp 提供文件系统挂载支持。

## 主要功能

- **连接管理**：集中管理多个连接，自定义名称、盘符、远程路径与缓存设置。
- **独立设置窗口**：新建连接和编辑连接均在独立窗口完成，保存后生效，取消时放弃草稿。
- **挂载与连接测试**：支持测试连接、挂载、卸载、重新挂载，以及直接打开盘符。
- **状态监控**：显示挂载状态、上传队列和错误信息。
- **可折叠日志**：日志区域默认收起，支持筛选、复制和导出，收起时仍继续记录。
- **托盘与自动挂载**：可关闭窗口后保持挂载，支持登录 Windows 时启动和应用启动后自动挂载指定连接。
- **凭据保护**：使用 Windows DPAPI 保护保存在配置中的密码、S3 密钥和 SFTP 私钥口令。
- **组件管理**：随发布包提供 rclone，支持组件检查与修复，并提供 WinFsp 安装入口。

## 支持的协议

| 协议 | 连接信息 |
| --- | --- |
| WebDAV | 完整的 HTTP/HTTPS 地址、用户名、密码和远程路径 |
| SFTP | 主机、端口、密码或私钥认证、已验证的 known_hosts 文件和远程路径 |
| S3 | Bucket、Prefix、Access Key ID、Secret Access Key 和 Region；兼容服务可指定 Endpoint |
| FTP | 主机、端口、用户名、密码和远程路径 |

SFTP 连接需要事先验证服务器主机密钥并将其写入 known_hosts 文件；应用不会自动接受未知密钥。

## 开始使用

当前发布目标为 **Windows x64**。便携包包含 .NET 运行时和 rclone，无须另外安装 .NET 或配置 rclone 路径。挂载盘符还需要 WinFsp，缺少时可在“应用设置与环境”中安装。

也可使用 `Mountloom-Setup-x64.exe` 安装到系统目录。安装器内含运行时和 WinFsp，首次安装驱动需要管理员权限；启动应用时使用普通用户账户。安装器要求 Windows 10 22H2 x64 或 Windows 11 x64。当前内置的 WinFsp 2026 Beta4 是上游预发布版本，具体行为与依赖来源见 [安装器说明](installer/README.md)。

1. 解压完整便携包，运行 `Mountloom.App.exe`。
2. 点击“新建连接”，填写名称、协议、连接信息和一个空闲盘符，然后保存。
3. 选中连接，点击“测试连接”；测试成功后点击“挂载”。
4. 在资源管理器中访问挂载盘符，或使用“打开盘符”按钮。

点击连接名称、双击连接条目，或选中后按 Enter，即可打开连接设置窗口。资源管理器中的网络盘名称使用连接名称；修改名称后重新挂载即可更新。

使用完毕后，关闭盘符中的文件并点击“卸载”。正常卸载会检查并等待待上传内容完成；结束应用请使用“退出并卸载”。默认情况下，关闭或最小化窗口会隐藏到托盘并保持挂载。

## 配置与本地数据

| 内容 | 默认位置 |
| --- | --- |
| 连接与应用配置 | `%APPDATA%\Mountly\config.json` |
| 配置备份 | `%APPDATA%\Mountly\config.json.bak` |
| VFS 缓存 | `%LOCALAPPDATA%\Mountly\Cache\<connection-id>\` |
| 运行日志 | `%LOCALAPPDATA%\Mountly\Logs\` |
| 自动修复的 rclone 运行时 | `%LOCALAPPDATA%\Mountly\Runtime\rclone\` |

便携包仍将配置和缓存保存在当前 Windows 用户目录。凭据保护与当前用户关联，将配置文件复制到其他用户或电脑不能直接恢复凭据。

项目原名 Mountly。为兼容已有配置、凭据和待上传文件，Mountloom 继续使用上述数据目录和内部缓存标识；无需搬迁或重命名数据文件。更新前请在旧版中正常“退出并卸载”，再运行 `Mountloom.App.exe`。若以前启用了登录 Windows 时启动，请在新版设置中重新勾选一次，让原启动项指向新程序。

断网或强制卸载后，缓存中可能保留待上传内容。请使用原连接设置与缓存目录重新挂载以继续上传，完成前不要删除缓存或更换连接身份。

## 从源码构建

在 Windows PowerShell 中进入项目根目录后执行：

```powershell
# 准备项目指定的 .NET SDK
.\scripts\bootstrap.ps1

# 构建并运行测试
.\scripts\build.ps1 -Configuration Release -Test

# 构建、测试并生成 Windows x64 便携包
.\scripts\publish.ps1

# 生成内含 WinFsp 的离线安装器
.\scripts\build-installer.ps1 -AppDirectory .\artifacts\Mountloom-win-x64
```

构建工具下载到项目的 `.tools` 目录，首次准备依赖需要联网。发布产物位于 `artifacts`，默认目录名为 `Mountloom-win-x64`；发布成功后会清理旧发布版本。更新正在运行的版本前，请先正常“退出并卸载”。

## 参与贡献与安全反馈

构建、测试与提交约定见 [CONTRIBUTING.md](CONTRIBUTING.md)。安全问题的报告方式和凭据、缓存的保护边界见 [SECURITY.md](SECURITY.md)。反馈问题前请删除日志中的服务地址、账户信息和远程文件名，不要上传连接配置或缓存文件。

## 第三方组件

依赖来源和许可说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。发布包中的第三方许可与声明位于 `third-party` 目录；各组件保留自己的许可证。

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — [WinFsp repository](https://github.com/winfsp/winfsp).

## 许可证

Mountloom 自有源码采用 **GNU General Public License v3.0 only（GPL-3.0-only）**，完整条款见 [LICENSE](LICENSE)。允许商业使用；分发本软件或其衍生作品时，须遵守 GPL-3.0 对许可证、版权声明及对应源码等方面的要求。第三方组件仍按各自许可证分发，详见上面的第三方声明。
