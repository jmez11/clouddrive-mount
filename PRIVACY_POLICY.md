# Privacy Policy for CloudDrive Mount

Last updated: November 18, 2026

CloudDrive Mount is a Windows desktop application by OceanSideVR that mounts remote storage — SFTP, FTP/FTPS, WebDAV, Google Drive, and Cloudflare R2 — as local drives on your computer.

## The short version

CloudDrive Mount has no servers, no accounts, and no telemetry. Everything the app knows about you stays on your computer. The only data that ever leaves your computer goes to the storage services you configure yourself.

## Information we collect

None. CloudDrive Mount does not collect, transmit, or sell any personal information to OceanSideVR or anyone else. The app contains no analytics, no telemetry, no crash reporting, no advertising, and no tracking of any kind. There is no OceanSideVR account, and the app never contacts OceanSideVR.

## Information stored on your device

To mount your drives, the app stores your drive configuration on your computer in a file called `drives.json` (in `%ProgramData%\CloudDriveMount\`, or next to the executable in portable mode). That file contains:

- Connection details: server addresses, ports, usernames, drive letters, labels, and folder or bucket names.
- Credentials: passwords, private-key passphrases, Cloudflare Access service-token secrets, Cloudflare R2 secret keys, and Google OAuth client secrets and tokens. These values are encrypted on your device using the Windows Data Protection API (DPAPI) before they are written to disk.

Your private key itself is never copied into the configuration — only the path to the key file on your computer is stored.

## File cache

Files you open through a mounted drive are cached locally on your computer (in your Windows temporary folder, under `CloudDriveMountCache`) so repeat access is faster. The cache is capped per mounted drive and evicts least-recently-used files first. Cached files stay on your computer until they are evicted or you delete them; you can delete the cache folder at any time.

## Information sent to third parties

CloudDrive Mount connects only to the services you configure:

- Your SFTP, FTP/FTPS, or WebDAV servers, using the addresses and credentials you provide.
- Google Drive, using the Google account you sign in with and the OAuth client credentials you provide. The app requests the `https://www.googleapis.com/auth/drive` scope, which allows it to see and manage your Google Drive files, because it mounts your Drive as a readable and writable drive. Sign-in uses OAuth 2.0 with PKCE and a loopback redirect on your own computer. Your Google access and refresh tokens are stored encrypted on your device and are sent only to Google. You can revoke the app's access at any time from your Google account's third-party access settings.
- Cloudflare R2, using the account ID, bucket, and API token you provide.
- Cloudflare tunnels, if you configure `cloudflared` proxying for a drive.

File contents and credentials are sent only to those endpoints, over the encrypted connections each protocol provides (SSH, TLS, or HTTPS). Nothing is sent to OceanSideVR.

## Google API Services User Data Policy

CloudDrive Mount's use and transfer to any other app of information received from Google APIs will adhere to the Google API Services User Data Policy, including the Limited Use requirements.

## Local network sharing

If you mount a drive as a Local Disk with Public visibility and share it through Windows Explorer, its contents become accessible to other devices on your local network. That sharing is controlled entirely by your Windows settings; CloudDrive Mount does not share anything on its own.

## Children

CloudDrive Mount is not directed at children under 13, and we do not knowingly collect information from children.

## Changes to this policy

We may update this policy from time to time. The current version will always be posted in the CloudDrive Mount repository, with the "Last updated" date above.

## Contact

Questions about this policy: OceanSideVR, james@oceanside-vr.com.
