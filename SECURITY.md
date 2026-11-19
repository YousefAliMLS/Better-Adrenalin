# Security Policy

## Reporting a Vulnerability

If you discover a security or safety issue in Better-Adrenalin (RadeonGuard), such as:
- Named Pipe ACL bypass (`RadeonGuard.IPC`)
- Path traversal or unsafe file handling in JSON import or backup restoration
- Validation bypasses capable of submitting unsafe hardware values to ADLX

Please report it privately using GitHub Security Advisories on the repository rather than opening a public issue with exploit details.

## Hardware & Safety Design

- **No Kernel Drivers:** Better-Adrenalin communicates strictly through AMD's documented user-mode `amdadlx64.dll` via `RadeonGuard.AdlxBridge.dll`.
- **Two-Stage Validation:** Fan curves and GPU clock/voltage/power parameters are validated against ADLX-reported min/max hardware bounds before persistence and again before hardware write.
- **Pre-Apply Quarantine:** Risky clock, voltage, VRAM, or power applies record a crash breadcrumb prior to execution; if an unclean shutdown occurs mid-apply, the profile is quarantined on startup while custom fan profiles remain preserved.
- **Scoped IPC & Registry:** The Named Pipe server grants access only to the current user SID and `BUILTIN\Administrators`. Optional Windows logon persistence writes strictly to the current user's own `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\RadeonGuardGuardian` value.
