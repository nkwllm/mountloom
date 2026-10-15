# 安全问题

安全修复优先提供给最新发布版本。报告时请说明应用版本、Windows 版本、协议类型、复现步骤和影响范围，并使用合成文件及测试账户重现。

## 报告方式

如果仓库的 **Security → Report a vulnerability** 入口可用，请通过该入口私下报告。若没有此入口，请只在 Issue 中请求私下联系渠道，不要公开漏洞利用细节、真实服务地址或凭据。维护者取得完整信息后再协调修复和公开说明。

不要在 Issue、Pull Request 或附件中提交：

- 连接配置及其备份，包括含 DPAPI 密文的 `config.json`。
- 密码、访问令牌、S3 密钥、SSH 私钥或私钥口令。
- 未脱敏的运行日志、远程文件名、账户名、内网地址或缓存文件。

## 安全边界

凭据由 Windows DPAPI 绑定当前用户保护。这不防御已能以同一用户身份运行的恶意程序；配置中的主机、用户名和路径等元数据也不等于密文。VFS 缓存可能保存远程文件内容和待上传内容，应按原始文件的敏感程度保护。

SFTP 要求经过独立验证的 `known_hosts`。优先使用 HTTPS WebDAV、SFTP 或 HTTPS S3 Endpoint；普通 FTP 和 HTTP 不提供传输机密性。不要通过关闭主机密钥或证书校验绕过连接失败。

下载和修复组件会校验固定的上游 SHA-256。公开发布时，第三方组件继续遵守各自许可证，详见 [第三方声明](THIRD-PARTY-NOTICES.md)。

WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos — [WinFsp repository](https://github.com/winfsp/winfsp).
