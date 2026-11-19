---
layout: page
title: Troubleshooting
---

## WinFsp isn't installed

WinFsp is bundled with the app and installed automatically if it's missing. If the bundled installer can't be found, the app opens the WinFsp download page instead — install WinFsp from there and try mounting again.

## A drive won't mount

- Check the drive letter isn't already in use, and pick a free one if it is.
- Check the host, port, and credentials, and that the server is reachable.
- For Cloudflare R2, a bucket-scoped token can't list buckets — type the bucket name directly instead of picking it from the list.

## Google sign-in times out

The browser sign-in window lasts 3 minutes. If it expires, click **Sign in with Google...** again.

## Google shows an "unverified app" warning

That's expected — this app isn't verified by Google. Click **Advanced**, then **Go to** continue. See [Google Drive](google-drive.html) for the full sign-in steps.

## A mounted drive starts returning I/O errors

Tokens refresh automatically, and the app recovers from expired Google tokens on its own. If a mount goes stale anyway, disconnect the drive and reconnect it.

## Some file names look different on the drive

Characters Windows doesn't allow in file names are replaced automatically, so files from other systems always open cleanly.

## Where the app keeps its files

- Drive configuration: `%ProgramData%\CloudDriveMount\drives.json` — or `drives.json` next to the executable in portable mode.
- File cache: `%TEMP%\CloudDriveMountCache` — one folder per drive. You can delete it at any time to free space; files are re-downloaded on demand.

## Still stuck

File an issue at [github.com/jmez11/clouddrive-mount/issues](https://github.com/jmez11/clouddrive-mount/issues) with the drive type, what you were doing, and any error message the app showed.
