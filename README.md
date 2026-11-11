# CloudDrive Mount

A native .NET 8 + WinFsp Windows desktop and System Tray application for mounting **SFTP**, **FTP/FTPS**, **WebDAV**, **Google Drive**, and **Cloudflare R2** as full Read/Write virtual drives (`W:`, `X:`, etc.), complete with custom Windows Explorer drive icons, native `cloudflared` tunnel/access proxying, and **Local Disk + Public** mounting for Windows Explorer LAN SMB sharing (`\\ComputerName\<drive>`).

## Features

- **5 Native Read/Write Storage Providers**:
  - **SFTP** (`SSH.NET` with password or private key authentication)
  - **FTP / FTPS** (`FluentFTP` with Explicit/Implicit TLS and Active/Passive modes)
  - **WebDAV** (RFC 4918 WebDAV with optional `LOCK`/`UNLOCK` File Lock support)
  - **Google Drive** (Google Drive v3 REST API with OAuth2 PKCE loopback sign-in and `.url` Internet Shortcut generation for Google Docs/Sheets/Slides)
  - **Cloudflare R2** (`AWSSDK.S3` configured for `https://<accountId>.r2.cloudflarestorage.com` with bucket discovery and bucket-scoped token verification)
- **Native `cloudflared` Integration**:
  - Automatically spawns and supervises `cloudflared access tcp --hostname <host> --url 127.0.0.1:<port>` per drive, or injects `CF-Access-Client-Id` / `CF-Access-Client-Secret` service token headers.
- **Lazy-Open & 512 MB LRU Disk Cache**:
  - Metadata-only opens (such as Windows Explorer scanning directory icons) require zero network downloads.
  - Files opened for reading/writing are cached in `%LocalAppData%\CloudDriveMount\Cache` and reused across handle opens until modified or evicted.
- **Local Disk (`Public` vs `Private`) & Windows LAN Sharing**:
  - Mounting with **Local Disk + Public** registers the drive with the Windows Kernel MountPointManager (`\GLOBAL??\<Drive>:`) and applies permissive NTFS ACLs so the drive can be shared across your LAN using Windows Explorer's native **Properties → Sharing → Advanced Sharing** tab.
- **Portable Single-File Executable**:
  - Publishes as a self-contained single-file `CloudDriveMount.exe`. Adding an empty `portable.mode` file (or `config\` folder) next to `CloudDriveMount.exe` stores all settings and cache alongside the executable.

## Project Structure

- `CloudDriveMount.sln`
- `lib/winfsp/`: Bundled WinFsp managed interop (`winfsp-msil.dll`), native loader (`winfsp-x64.dll`), and installer (`winfsp.msi`).
- `src/CloudDriveMount.Core/`:
  - `Models/DriveConfig.cs`: Configuration model for SFTP, FTP, WebDAV, Google Drive, and Cloudflare R2.
  - `FileSystem/CloudDriveFileSystem.cs`: Full R/W `Fsp.FileSystemBase` virtual filesystem with LRU disk cache, directory metadata caching, Read-Only & File Lock modes, and `Local Disk + Public` SDDL ACLs.
  - `Services/DriveMountManager.cs`: Orchestrates WinFsp mounting, `cloudflared` tunnels, Explorer icons, and Kernel MountPointManager registration.
  - `Services/CloudflaredTunnelManager.cs`: Spawns and supervises `cloudflared access tcp` tunnels and service token headers.
  - `Services/ExplorerIconManager.cs`: Renders multi-size (`16x16` to `256x256`) protocol `.ico` files and registers drive icons, labels, and Windows notification identity (`CloudDriveMount.App`).
  - `Providers/`: `SftpStorageProvider`, `FtpStorageProvider`, `WebDavStorageProvider`, `CloudflareR2StorageProvider`, and `GoogleDriveStorageProvider`.
- `src/CloudDriveMount.App/`: WPF + System Tray desktop UI.
- `tests/CloudDriveMount.Tests/`: xUnit test suite.

## Building & Running

```powershell
# Build and run unit tests
dotnet test

# Launch the desktop & system tray application
dotnet run --project src\CloudDriveMount.App\CloudDriveMount.App.csproj

# Build portable single-file executable
dotnet publish src\CloudDriveMount.App\CloudDriveMount.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o dist
```
