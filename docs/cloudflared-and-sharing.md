---
layout: page
title: Cloudflared & sharing
---

## Cloudflared tunnels

Each drive can go through a Cloudflare tunnel instead of connecting directly, so your servers stay reachable without opening ports. The app spawns and supervises `cloudflared` for you.

- **Access TCP tunnel** — the app runs `cloudflared access tcp` for the drive's hostname, and connects through the local tunnel port.
- **Service token headers** — the app injects `CF-Access-Client-Id` / `CF-Access-Client-Secret` headers for Cloudflare Access-protected hosts.
- **Both** — tunnel plus headers, for hosts that need both.

The `cloudflared` binary path is configurable per drive; by default the app looks for `cloudflared` on your `PATH`. Service-token secrets are encrypted on your device with Windows DPAPI.

## Local Disk modes

By default a mounted drive appears as a network drive. Turning on **Local Disk** makes Windows treat it as a local disk instead, which some applications prefer.

- **Private** — the drive is visible in your session only.
- **Public** — the drive is registered system-wide, so Windows Explorer's Sharing tab can share it on your local network. Public mode applies permissive NTFS permissions to make sharing work.

## Sharing a drive on your LAN

1. Mount the drive as **Local Disk + Public**.
2. In Windows Explorer, right-click the drive → **Properties → Sharing → Advanced Sharing**.
3. Share it like any other folder.

Only share drives you mean to share: anyone on your LAN with access to the share can read and write the files on it, subject to the Windows sharing permissions you set.
