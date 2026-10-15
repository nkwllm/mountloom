# 参与贡献

Mountloom 是面向 Windows x64 的 WPF 远程存储挂载工具。提交较大的功能修改前，请先通过 Issue 说明使用场景和预期行为。

## 开发与验证

需要 Windows x64、Git 和 PowerShell。在项目根目录执行：

```powershell
.\scripts\bootstrap.ps1
.\scripts\build.ps1 -Configuration Release -Test
```

脚本使用项目 `.tools` 中固定版本的 .NET SDK，不要求修改全局 PATH。常规测试使用临时数据和替身，不连接个人远程存储，也不挂载实际盘符。

生成便携包与安装器：

```powershell
.\scripts\publish.ps1
.\scripts\build-installer.ps1 -AppDirectory .\artifacts\Mountloom-win-x64
```

发布脚本只保留最新生成版本；正在使用的旧版本会保留到正常退出后再清理。不要强制结束挂载进程来替换文件。

可选的本地协议验证见 [协议测试说明](tests/Mountloom.ProtocolIntegration/README.md)。真实挂载、安装和卸载测试应由测试者在可丢弃的 Windows 环境中执行，使用合成文件和专用测试账户；它们不属于常规 CI。

## 提交修改

- 说明问题、修改后的行为和已执行的验证。涉及上传缓存、凭据或卸载流程时，请说明对异常退出和数据保留的影响。
- 保留源码、维护中的测试、构建脚本和第三方许可证；不要提交 `.tools`、`artifacts`、`bin`、`obj`、测试报告、调试日志或 QA 截图。
- 使用虚构主机、测试凭据和合成文件。不要附上个人 `config.json`、rclone 配置、SSH 私钥、访问密钥或真实远程目录清单。
- 新增或更新依赖时同步更新固定版本、下载校验值和 [第三方声明](THIRD-PARTY-NOTICES.md)。

安全问题请按照 [SECURITY.md](SECURITY.md) 报告。

## 许可与兼容性

提交的自有代码贡献按项目的 [GPL-3.0-only 许可证](LICENSE) 分发；引入第三方代码时必须保留其许可和来源，并确认可与项目一起分发。

项目曾名为 Mountly。代码中的旧数据目录、DPAPI entropy、rclone remote 名、VFS cache 身份文件、启动项名称及进程互斥锁是兼容性约定。修改可见品牌时，不要批量替换这些持久化标识，也不要迁移或删除现有用户的缓存。

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — [WinFsp repository](https://github.com/winfsp/winfsp).
