# Local SFTP and S3 smoke tests

This optional console test runs the real `ConnectionTester`, `RcloneConfigWriter`,
`RcloneArguments` and `ProcessRunner` against two temporary rclone servers. It does
not mount drives. The servers listen only on `127.0.0.1` using random free ports,
generated credentials, and synthetic files below one random temporary directory.
SFTP host/client keys and `known_hosts` are generated there; existing user keys and
configuration are not used. Both servers and their files are removed in `finally`.

From the repository root in PowerShell, use the pinned project-local SDK and rclone:

```powershell
.\scripts\test-protocols.ps1
```

The script bootstraps .NET 10 and rclone locally, then builds the referenced Core
project and runs this optional console test in Release. Run it separately from other
builds or integration tests.

Prerequisites: rclone with `serve sftp` and `serve s3`, and Windows OpenSSH
`ssh-keygen.exe` / `ssh-keyscan.exe` in `Windows\System32\OpenSSH`. No administrator
rights or WinFsp installation are required. The executable path may be overridden
as the first argument. The runner reports `PASS`/`FAIL` and exits nonzero on failure.

Coverage:

- SFTP password and explicit private-key authentication, including host-key verification.
- Absolute and relative SFTP path strings reach nested sentinel files without losing their leading slash distinction.
- Incorrect passwords and mismatched host keys fail with an authentication/verification reason and without exposing generated secrets.
- S3 signed authentication reads only the selected bucket/prefix; both explicit bucket and bucket-in-path forms work.
- Incorrect S3 secrets and access keys are rejected.
- Temporary server processes and files are cleaned up.

Limits: `rclone serve sftp` exposes a virtual root, so relative and absolute paths
resolve within that root. This confirms correct argument preservation and nested
access, but does not simulate a production SSH account whose home directory differs
from `/`. `rclone serve s3` is a local compatibility check, not an AWS/cloud provider
conformance test. Agent-based SSH authentication and encrypted client private keys
are not covered here. Only disposable test servers are killed during cleanup; this
test does not exercise mount shutdown or pending VFS upload handling.

Mountloom's drive mounting uses WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos. [WinFsp project and source](https://github.com/winfsp/winfsp). These protocol tests do not load or install WinFsp.
