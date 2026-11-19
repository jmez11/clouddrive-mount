---
layout: page
title: Providers
---

CloudDrive Mount mounts five kinds of storage. Each drive uses one provider.

## SFTP

Connect to any SSH server with SFTP enabled.

- **Host, port** (default 22), **username**, and either a **password** or a **private key** (with an optional passphrase).
- **Superuser Mode** runs the SFTP subsystem as root via `sudo` on the server. Use it only on servers you administer.
- Credentials are encrypted on your device with Windows DPAPI. Your private key itself is never copied into the configuration — only the path to the key file is stored.

## FTP / FTPS

Connect to FTP servers, with or without TLS.

- **Host, port** (default 21), **username**, and **password** — or **anonymous** sign-in.
- **Explicit or implicit TLS**, **active or passive** mode, and TLS 1.2 or 1.3.
- Plain FTP sends your password unencrypted. Prefer FTPS whenever the server supports it.

## WebDAV

Connect to any RFC 4918 WebDAV server.

- **Host, port**, **username**, and **password**, with optional TLS.
- Optional **file locking** (`LOCK`/`UNLOCK`) for servers that support it.

## Google Drive

Mount your Google Drive as a drive letter, using your own OAuth client.

- You create a Desktop-app OAuth client in Google Cloud Console and import it into the app — see [Google Drive](google-drive.html) for the full steps.
- Mount your whole Drive, files shared with you, or a single folder.
- Google Docs, Sheets, and Slides appear as `.url` shortcuts that open in your browser.
- When you sign in, Google shows an "unverified app" warning. That's expected — click **Advanced**, then **Go to** continue.

## Cloudflare R2

Mount an R2 bucket as a drive, over the S3-compatible API.

- **Account ID**, **bucket name**, and an **API token** (access key ID + secret).
- Bucket-scoped tokens are supported: if your token can't list buckets, type the bucket name directly and connect.
- The app verifies the token against the bucket before mounting.
