# Mountloom 0.3.0

Mountloom 是一款 Windows x64 桌面远程存储挂载工具，可将 WebDAV、SFTP、S3 和 FTP 连接挂载为资源管理器中的盘符。

## 开始使用

发布包已包含 .NET 运行时和 rclone。请保留完整目录，并使用普通 Windows 用户账户运行 `Mountloom.App.exe`。挂载需要 WinFsp；便携版可在“应用设置与环境”中安装，安装版会在缺少驱动时安装随包组件。安装 WinFsp 时可能需要管理员授权和重启。

1. 点击“新建连接”，填写名称、协议、连接信息和空闲盘符，然后保存。
2. 选中连接，点击“测试连接”；测试成功后点击“挂载”。
3. 在资源管理器中访问盘符，或点击“打开盘符”。点击连接名称、双击连接条目或按 Enter 可以编辑连接。
4. 使用完毕后关闭盘符中的文件，点击“卸载”，等待待上传内容完成。

关闭或最小化窗口默认会隐藏到托盘，挂载继续保持。结束应用请在窗口或托盘菜单中选择“退出并卸载”；不要通过强制结束进程清理挂载。

SFTP 需要事先验证服务器主机密钥并准备 `known_hosts` 文件。优先使用 HTTPS WebDAV、SFTP 或 HTTPS S3 Endpoint；普通 FTP 和 HTTP 不提供传输机密性。

## 更新与本地数据

项目原名 Mountly。Mountloom 保留原数据目录和内部缓存标识，以继续读取已有配置、凭据及待上传文件，无需搬迁或重命名数据文件。

| 内容 | 默认位置 |
| --- | --- |
| 连接与应用配置 | `%APPDATA%\Mountly\config.json` |
| 配置备份 | `%APPDATA%\Mountly\config.json.bak` |
| VFS 缓存 | `%LOCALAPPDATA%\Mountly\Cache\<connection-id>\` |
| 运行日志 | `%LOCALAPPDATA%\Mountly\Logs\` |
| 自动修复的 rclone 运行时 | `%LOCALAPPDATA%\Mountly\Runtime\rclone\` |

更新前请在旧版中正常“退出并卸载”，再运行 `Mountloom.App.exe`。若以前启用了登录 Windows 时启动，请在新版设置中取消后重新勾选该选项，让启动项指向新程序。

凭据由 Windows DPAPI 绑定当前用户保护；将配置复制到其他用户或电脑不能直接恢复凭据。缓存可能保存待上传内容，上传完成前不要删除缓存或更换连接身份。反馈问题时不要上传连接配置、凭据、缓存或未脱敏的日志。

## 源码、文档与许可证

[GitHub 项目与完整文档](https://github.com/nkwllm/mountloom)提供源码、构建说明、贡献方式及安全反馈说明。

Mountloom 自有源码采用 **GPL-3.0-only**，完整条款见本目录的 [LICENSE](LICENSE)。第三方组件继续按各自许可证分发，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 和 `third-party` 目录。安装器内置的 WinFsp 2026 Beta4 是上游预发布版本。

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — [WinFsp repository](https://github.com/winfsp/winfsp).
