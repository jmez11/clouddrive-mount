---
layout: page
title: Getting started
---

## Requirements

64-bit Windows 10 or 11. The app is self-contained — you don't need to install .NET separately. WinFsp (the virtual filesystem driver) is bundled and installed automatically if it's missing.

## Install

1. Download `CloudDriveMount-win-x64.zip` from [GitHub Releases](https://github.com/jmez11/clouddrive-mount/releases).
2. Extract it anywhere you like.
3. Run `CloudDriveMount.exe`. There is no installer.

The app lives in the system tray. Open it from the tray icon to manage your drives.

## Add your first drive

1. In the app, add a drive and pick a drive letter (for example `W:`).
2. Pick a protocol — SFTP, FTP, WebDAV, Google Drive, or Cloudflare R2 — and fill in the server details. See [Providers](providers.html) for what each one needs.
3. Click **Connect**. The drive appears in Windows Explorer like any other drive.

## Reconnect at startup

Drives reconnect automatically when Windows starts, unless you turn **Reconnect at start** off for a drive. You can also connect or disconnect all drives at once from the tray menu.

## Portable mode

To keep the app's settings next to the executable instead of in `%ProgramData%`, place a `drives.json` file next to `CloudDriveMount.exe`. The app uses it directly when it finds it there. (The file cache always lives in your Windows temp folder — see [Troubleshooting](troubleshooting.html).)

## Uninstall

Disconnect your drives, then delete the folder you extracted the app into. To remove everything, also delete:

- `%ProgramData%\CloudDriveMount\drives.json` (your drive configuration)
- `%TEMP%\CloudDriveMountCache` (cached files)
