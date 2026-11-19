---
layout: page
title: CloudDrive Mount
tagline: Your cloud storage, as a drive letter.
tag: Open source · GPL-3.0
---

CloudDrive Mount turns remote storage into ordinary Windows drives. Connect an SFTP server, an FTP site, a WebDAV share, your Google Drive, or a Cloudflare R2 bucket, and it shows up in Explorer as W:, X:, or whatever letter you pick — read and write, just like a local disk.

It runs in the system tray, reconnects your drives when Windows starts, and works through Cloudflare tunnels, so your servers stay reachable without opening ports. No account, no cloud service of its own — your files go straight from your computer to your storage.

<div class="links">
<a class="btn" href="https://github.com/jmez11/clouddrive-mount">GitHub</a>
<a class="btn ghost" href="https://github.com/jmez11/clouddrive-mount/releases">Releases</a>
</div>

## What you get

- **Five storage providers** — SFTP (password or private key, with an optional sudo superuser mode), FTP/FTPS (explicit or implicit TLS), WebDAV (with file locking), Google Drive, and Cloudflare R2 (bucket-scoped tokens).
- **Real drive letters with custom Explorer icons** — mounts as W:, X:, or any free letter, with a per-protocol icon and label in Explorer.
- **Cloudflared built in** — per-drive tunnels (`cloudflared access tcp`) or Cloudflare Access service-token headers, supervised by the app.
- **Fast repeat access** — files you open are cached locally, and browsing a folder doesn't download anything until you open a file.
- **LAN sharing** — mount as a Local Disk and share the drive across your home network from Explorer's Sharing tab.
- **Portable single file** — one `CloudDriveMount.exe`, no installer. Settings can live next to the executable.
- **System tray** — lives in the tray and reconnects your drives when Windows starts.

## Quick start

1. Download `CloudDriveMount-win-x64.zip` from [GitHub Releases](https://github.com/jmez11/clouddrive-mount/releases), extract it, and run `CloudDriveMount.exe`. WinFsp is bundled and installed automatically if it's missing.
2. Add a drive: pick a letter, a protocol, and your server details.
3. Click Connect — the drive appears in Explorer.

## Google Drive

Google Drive needs no setup — just add a Google Drive drive and sign in. Google Docs, Sheets, and Slides show up as shortcuts that open in your browser.

When you sign in, Google shows an "unverified app" warning. That's expected — this app isn't verified by Google. Click **Advanced**, then **Go to** continue.

Full setup steps are in the [Google Drive docs](google-drive.html).

## Documentation

- [Getting started](getting-started.html) — Install the app, add your first drive, reconnect at startup, portable mode.
- [Providers](providers.html) — What each storage provider needs: SFTP, FTP/FTPS, WebDAV, Google Drive, and Cloudflare R2.
- [Google Drive](google-drive.html) — Sign in, scopes, and what the unverified-app warning means.
- [Cloudflared & sharing](cloudflared-and-sharing.html) — Tunnels, Access service tokens, Local Disk modes, and sharing a drive on your LAN.
- [Troubleshooting](troubleshooting.html) — WinFsp, sign-in timeouts, stale mounts, and where the app keeps its files.

## Privacy

CloudDrive Mount has no telemetry, no analytics, and no account. Your credentials are encrypted on your device with Windows DPAPI, and your files go only to the storage you configure.

[Privacy Policy](https://github.com/jmez11/clouddrive-mount/blob/main/PRIVACY_POLICY.md) · [Terms of Service](https://github.com/jmez11/clouddrive-mount/blob/main/TERMS_OF_SERVICE.md) · [License (GPL-3.0)](https://github.com/jmez11/clouddrive-mount/blob/main/LICENSE)
