using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CloudDriveMount.Core.Models;
using Microsoft.Win32;

namespace CloudDriveMount.Core.Services;

public static class ExplorerIconManager
{
    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256];

    public static string GetIconsDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CloudDriveMount",
            "icons");
        try
        {
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "CloudDriveMount",
                "icons");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string EnsureProtocolIconFile(StorageProtocol protocol, char driveLetter, bool isLocalDisk = false)
    {
        var dir = GetIconsDirectory();
        var fileName = $"drive_{protocol.ToString().ToLowerInvariant()}_{char.ToLowerInvariant(driveLetter)}.ico";
        var fullPath = Path.Combine(dir, fileName);

        using (var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            WriteMultiSizeIcon(fs, size => RenderProtocolBitmap(protocol, size, isLocalDisk));
        }

        // Also mirror to CommonApplicationData\CloudDriveMount\icons if writable so Session 0 / HKLM paths resolve too
        try
        {
            var commonDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "CloudDriveMount",
                "icons");
            Directory.CreateDirectory(commonDir);
            var commonPath = Path.Combine(commonDir, fileName);
            if (!string.Equals(fullPath, commonPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(fullPath, commonPath, overwrite: true);
            }
        }
        catch { }

        return fullPath;
    }

    public const string AppUserModelId = "CloudDriveMount.App";
    public const string AppDisplayName = "CloudDrive Mount";

    public static string EnsureAppTrayIconFile()
    {
        var dir = GetIconsDirectory();
        var fullPath = Path.Combine(dir, "clouddrive_tray_v2.ico");
        if (!File.Exists(fullPath))
        {
            SaveAppIconFile(fullPath);
        }
        return fullPath;
    }

    public static string EnsureAppLogoPngFile()
    {
        var dir = GetIconsDirectory();
        var fullPath = Path.Combine(dir, "clouddrive_logo_256.png");
        if (!File.Exists(fullPath))
        {
            using var bmp = RenderAppLogoBitmap(256);
            bmp.Save(fullPath, ImageFormat.Png);
        }
        return fullPath;
    }

    public static void SaveAppIconFile(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteMultiSizeIcon(fs, RenderAppLogoBitmap);
    }

    public static void EnsureAppNotificationIdentity(string? executablePathOverride = null)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        }
        catch { }

        var pngPath = EnsureAppLogoPngFile();

        // 1. Register HKCU\Software\Classes\AppUserModelId\CloudDriveMount.App
        try
        {
            using var aumidKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppUserModelId}", writable: true);
            aumidKey?.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
            aumidKey?.SetValue("IconUri", pngPath, RegistryValueKind.String);
            aumidKey?.SetValue("IconBackgroundColor", "00000000", RegistryValueKind.String);
        }
        catch { }

        // 2. Heal any auto-generated NotifyIconGeneratedAumid_* or Microsoft.Explorer.Notification.* entries for this app
        try
        {
            using var notifySettingsRoot = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: false);
            if (notifySettingsRoot != null)
            {
                foreach (var subName in notifySettingsRoot.GetSubKeyNames())
                {
                    using var sub = notifySettingsRoot.OpenSubKey(subName, writable: false);
                    var exe = sub?.GetValue("ExecutablePath") as string ?? string.Empty;
                    var tooltip = sub?.GetValue("InitialTooltip") as string ?? string.Empty;
                    if (exe.Contains("CloudDriveMount", StringComparison.OrdinalIgnoreCase) ||
                        tooltip.StartsWith("CloudDrive Mount", StringComparison.OrdinalIgnoreCase))
                    {
                        using var genKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\NotifyIconGeneratedAumid_{subName}", writable: true);
                        genKey?.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
                        genKey?.SetValue("IconUri", pngPath, RegistryValueKind.String);
                    }
                }
            }

            using var aumidRoot = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId", writable: true);
            if (aumidRoot != null)
            {
                foreach (var subName in aumidRoot.GetSubKeyNames())
                {
                    using var sub = aumidRoot.OpenSubKey(subName, writable: true);
                    if (sub == null) continue;
                    var existingDisplay = sub.GetValue("DisplayName") as string;
                    if (string.Equals(existingDisplay, "CloudDriveMount", StringComparison.OrdinalIgnoreCase) ||
                        (subName.StartsWith("Microsoft.Explorer.Notification.", StringComparison.OrdinalIgnoreCase) &&
                         string.IsNullOrWhiteSpace(existingDisplay)))
                    {
                        sub.SetValue("DisplayName", AppDisplayName, RegistryValueKind.String);
                        sub.SetValue("IconUri", pngPath, RegistryValueKind.String);
                    }
                }
            }
        }
        catch { }
    }

    public static void RegisterDriveIconAndLabel(DriveConfig config, string? uncPrefix = null)
    {
        var letter = config.NormalizedDriveLetterChar;
        var iconPath = EnsureProtocolIconFile(config.Protocol, letter, config.IsLocalDisk);
        var label = string.IsNullOrWhiteSpace(config.Label) ? config.ProtocolDisplayName : config.Label.Trim();

        // 1. HKCU\Software\Classes\Applications\Explorer.exe\Drives\<Letter>
        //    Read by Windows 10/11 Explorer (HKEY_CLASSES_ROOT) WITHOUT requiring Administrator privileges.
        var classesDrivesPath = $@"Software\Classes\Applications\Explorer.exe\Drives\{letter}";
        ApplyRegistryDriveIcon(Registry.CurrentUser, classesDrivesPath, iconPath, label);

        // 2. HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\<Letter>
        var explorerDriveIconsPath = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{letter}";
        ApplyRegistryDriveIcon(Registry.CurrentUser, explorerDriveIconsPath, iconPath, label);

        // 3. HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\<Letter>
        try
        {
            ApplyRegistryDriveIcon(Registry.LocalMachine, explorerDriveIconsPath, iconPath, label);
        }
        catch
        {
            // If non-elevated and a stale HKLM DriveIcons entry exists from a previous mount on this letter,
            // copy our .ico file to the path referenced by HKLM if writable so Explorer never shows a blank page icon.
            TryHealStaleHklmDriveIconFile(explorerDriveIconsPath, iconPath);
        }

        // 4. HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2
        //    Sets _LabelFromReg so Network Drives show "Plex Max (P:)" instead of "PlexMax_P (\\CloudDriveMount) (P:)".
        ApplyMountPoints2Label(letter, label, uncPrefix);

        RefreshExplorerDrive(letter);
    }

    public static void UnregisterDriveIconAndLabel(char driveLetter, string? uncPrefix = null)
    {
        var letter = char.ToUpperInvariant(driveLetter);
        var classesDrivesPath = $@"Software\Classes\Applications\Explorer.exe\Drives\{letter}";
        var explorerDriveIconsPath = $@"Software\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\{letter}";

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(classesDrivesPath, throwOnMissingSubKey: false);
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(explorerDriveIconsPath, throwOnMissingSubKey: false);
        }
        catch { }

        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(explorerDriveIconsPath, throwOnMissingSubKey: false);
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(uncPrefix))
        {
            try
            {
                var mpKeyName = "##" + uncPrefix.TrimStart('\\').Replace('\\', '#');
                using var mp2 = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2", writable: true);
                mp2?.DeleteSubKeyTree(mpKeyName, throwOnMissingSubKey: false);
            }
            catch { }
        }

        RefreshExplorerDrive(letter);
    }

    private static void ApplyRegistryDriveIcon(RegistryKey root, string subKeyPath, string iconPath, string label)
    {
        using var iconKey = root.CreateSubKey($@"{subKeyPath}\DefaultIcon", writable: true);
        iconKey?.SetValue(null, $"{iconPath},0", RegistryValueKind.String);

        if (!string.IsNullOrWhiteSpace(label))
        {
            using var labelKey = root.CreateSubKey($@"{subKeyPath}\DefaultLabel", writable: true);
            labelKey?.SetValue(null, label.Trim(), RegistryValueKind.String);
        }
    }

    private static void TryHealStaleHklmDriveIconFile(string explorerDriveIconsPath, string sourceIconPath)
    {
        try
        {
            using var hklmIconKey = Registry.LocalMachine.OpenSubKey($@"{explorerDriveIconsPath}\DefaultIcon", writable: false);
            var rawVal = hklmIconKey?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(rawVal))
                return;

            var commaIdx = rawVal.LastIndexOf(',');
            var targetFile = (commaIdx > 0 ? rawVal[..commaIdx] : rawVal).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(targetFile))
                return;

            var dir = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.Copy(sourceIconPath, targetFile, overwrite: true);
        }
        catch { }
    }

    private static void ApplyMountPoints2Label(char letter, string label, string? uncPrefix)
    {
        try
        {
            using var mp2 = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2",
                writable: true);
            if (mp2 == null)
                return;

            using (var letterKey = mp2.CreateSubKey(letter.ToString(), writable: true))
            {
                letterKey?.SetValue("_LabelFromReg", label, RegistryValueKind.String);
            }

            if (!string.IsNullOrWhiteSpace(uncPrefix))
            {
                var mpKeyName = "##" + uncPrefix.TrimStart('\\').Replace('\\', '#');
                using var uncKey = mp2.CreateSubKey(mpKeyName, writable: true);
                uncKey?.SetValue("_LabelFromReg", label, RegistryValueKind.String);
            }
        }
        catch { }
    }

    public static void RefreshExplorerDrive(char driveLetter)
    {
        try
        {
            var path = $"{char.ToUpperInvariant(driveLetter)}:\\";
            var pathPtr = Marshal.StringToHGlobalUni(path);
            try
            {
                SHChangeNotify(SHCNE_DRIVEADD, SHCNF_PATHW, pathPtr, IntPtr.Zero);
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, pathPtr, IntPtr.Zero);
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pathPtr);
            }
        }
        catch
        {
            // Ignore shell notify errors in headless environments
        }
    }

    public static Bitmap RenderProtocolBitmap(StorageProtocol protocol, int size, bool isLocalDisk = false)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float pad = size * 0.04f;
        float diameter = size - pad * 2f;
        var circleRect = new RectangleF(pad, pad, diameter, diameter);

        // Base circular badge (#54B258 left / #479B4C right split circle)
        Color leftBase = protocol == StorageProtocol.CloudflareR2
            ? Color.FromArgb(244, 129, 32)   // Cloudflare warm orange-amber accent
            : Color.FromArgb(84, 178, 88);   // Signature green (#54B258)
        Color rightBase = protocol == StorageProtocol.CloudflareR2
            ? Color.FromArgb(219, 108, 18)
            : Color.FromArgb(71, 155, 76);

        using (var path = new GraphicsPath())
        {
            path.AddEllipse(circleRect);
            using var clipRegion = new Region(path);
            var oldClip = g.Clip;
            g.Clip = clipRegion;

            using (var leftBrush = new SolidBrush(leftBase))
                g.FillRectangle(leftBrush, pad, pad, diameter * 0.52f, diameter);
            using (var rightBrush = new SolidBrush(rightBase))
                g.FillRectangle(rightBrush, pad + diameter * 0.5f, pad, diameter * 0.52f, diameter);

            g.Clip = oldClip;
        }

        // Draw white protocol emblem in the center
        switch (protocol)
        {
            case StorageProtocol.Sftp:
                DrawSftpShield(g, size);
                break;
            case StorageProtocol.Ftp:
                DrawFtpDocument(g, size);
                break;
            case StorageProtocol.WebDav:
                DrawWebDavGlobe(g, size);
                break;
            case StorageProtocol.GoogleDrive:
                DrawGoogleDriveTriangle(g, size);
                break;
            case StorageProtocol.CloudflareR2:
                DrawCloudflareR2Emblem(g, size);
                break;
        }

        return bmp;
    }

    public static Bitmap RenderAppLogoBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float pad = size * 0.045f;
        float boxSize = size - pad * 2f;
        float cornerRadius = size * 0.24f;
        var badgeRect = new RectangleF(pad, pad, boxSize, boxSize);

        // 1. Rounded-squircle background with deep navy -> electric cyan-teal gradient
        using (var squircle = CreateRoundedRectPath(badgeRect, cornerRadius))
        {
            using var bgBrush = new LinearGradientBrush(
                badgeRect,
                Color.FromArgb(13, 37, 76),    // Deep midnight navy top-left
                Color.FromArgb(0, 151, 167),   // Rich electric cyan-teal bottom-right
                45f);
            g.FillPath(bgBrush, squircle);

            // Subtle inner glow rim
            float borderW = Math.Max(1f, size * 0.032f);
            using var rimPen = new Pen(Color.FromArgb(140, 77, 208, 225), borderW);
            g.DrawPath(rimPen, squircle);
        }

        // 2. Crisp white/ice-blue Cloud in the upper-middle portion
        float cloudW = size * 0.62f;
        float cloudH = size * 0.34f;
        float cloudLeft = (size - cloudW) * 0.5f;
        float cloudTop = size * 0.19f;

        using (var cloudShadowBrush = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
        {
            float sh = Math.Max(1f, size * 0.025f);
            FillCloudGeometry(g, cloudShadowBrush, cloudLeft, cloudTop + sh, cloudW, cloudH);
        }

        using (var cloudBrush = new LinearGradientBrush(
            new RectangleF(cloudLeft, cloudTop - cloudH * 0.3f, cloudW, cloudH * 1.3f),
            Color.White,
            Color.FromArgb(225, 245, 254),
            90f))
        {
            FillCloudGeometry(g, cloudBrush, cloudLeft, cloudTop, cloudW, cloudH);
        }

        // 3. Sleek dark-slate Local SSD / Drive Chassis at the bottom
        float driveW = size * 0.68f;
        float driveH = size * 0.20f;
        float driveLeft = (size - driveW) * 0.5f;
        float driveTop = size * 0.64f;
        float driveCorner = size * 0.055f;
        var driveRect = new RectangleF(driveLeft, driveTop, driveW, driveH);

        using (var drivePath = CreateRoundedRectPath(driveRect, driveCorner))
        {
            using var driveBrush = new LinearGradientBrush(
                driveRect,
                Color.FromArgb(30, 41, 59),
                Color.FromArgb(15, 23, 42),
                90f);
            g.FillPath(driveBrush, drivePath);

            using var driveBorderPen = new Pen(Color.FromArgb(180, 148, 163, 184), Math.Max(1f, size * 0.024f));
            g.DrawPath(driveBorderPen, drivePath);
        }

        // Drive bay slot line & glowing activity LEDs (emerald + Cloudflare amber)
        float ledY = driveTop + driveH * 0.5f;
        float slotLeft = driveLeft + driveW * 0.12f;
        float slotRight = driveLeft + driveW * 0.56f;
        using (var slotPen = new Pen(Color.FromArgb(100, 148, 163, 184), Math.Max(1f, size * 0.028f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        {
            g.DrawLine(slotPen, slotLeft, ledY, slotRight, ledY);
        }

        float ledR = Math.Max(1.3f, size * 0.036f);
        float led1X = driveLeft + driveW * 0.72f;
        float led2X = driveLeft + driveW * 0.85f;
        using (var amberBrush = new SolidBrush(Color.FromArgb(255, 145, 0)))
        using (var greenBrush = new SolidBrush(Color.FromArgb(0, 230, 118)))
        {
            g.FillEllipse(amberBrush, led1X - ledR, ledY - ledR, ledR * 2f, ledR * 2f);
            g.FillEllipse(greenBrush, led2X - ledR, ledY - ledR, ledR * 2f, ledR * 2f);
        }

        // 4. Emerald-cyan Mount Connector Arrow linking the Cloud and the Drive
        float cx = size * 0.5f;
        float arrowTop = size * 0.34f;
        float arrowBottom = size * 0.62f;
        float arrowHalfW = size * 0.11f;
        float shaftHalfW = size * 0.042f;
        float headH = size * 0.10f;

        PointF[] biArrow =
        [
            new PointF(cx, arrowTop),                                  // Top tip
            new PointF(cx + arrowHalfW, arrowTop + headH),             // Top-right wing
            new PointF(cx + shaftHalfW, arrowTop + headH),             // Shaft top-right
            new PointF(cx + shaftHalfW, arrowBottom - headH),          // Shaft bottom-right
            new PointF(cx + arrowHalfW, arrowBottom - headH),          // Bottom-right wing
            new PointF(cx, arrowBottom),                               // Bottom tip
            new PointF(cx - arrowHalfW, arrowBottom - headH),          // Bottom-left wing
            new PointF(cx - shaftHalfW, arrowBottom - headH),          // Shaft bottom-left
            new PointF(cx - shaftHalfW, arrowTop + headH),             // Shaft top-left
            new PointF(cx - arrowHalfW, arrowTop + headH)              // Top-left wing
        ];

        using (var arrowOutlinePen = new Pen(Color.FromArgb(220, 13, 37, 76), Math.Max(1.2f, size * 0.03f))
        {
            LineJoin = LineJoin.Round
        })
        using (var arrowBrush = new LinearGradientBrush(
            new RectangleF(cx - arrowHalfW, arrowTop, arrowHalfW * 2f, arrowBottom - arrowTop),
            Color.FromArgb(0, 230, 118),
            Color.FromArgb(0, 176, 255),
            90f))
        {
            g.DrawPolygon(arrowOutlinePen, biArrow);
            g.FillPolygon(arrowBrush, biArrow);
        }

        return bmp;
    }

    private static void FillCloudGeometry(Graphics g, Brush brush, float left, float top, float w, float h)
    {
        // Smooth pill base across the entire width of the cloud
        var baseRect = new RectangleF(left, top + h * 0.16f, w, h * 0.64f);
        using (var basePill = CreateRoundedRectPath(baseRect, h * 0.32f))
        {
            g.FillPath(brush, basePill);
        }
        // Upper cloud domes
        g.FillEllipse(brush, left + w * 0.16f, top - h * 0.26f, w * 0.46f, h * 0.88f);
        g.FillEllipse(brush, left + w * 0.46f, top - h * 0.08f, w * 0.38f, h * 0.76f);
    }

    private static GraphicsPath CreateRoundedRectPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawSftpShield(Graphics g, int size)
    {
        // White outer shield with blue/white quartered crest (matching Screenshot 4 SFTP icon)
        float cx = size * 0.5f;
        float top = size * 0.22f;
        float bottom = size * 0.79f;
        float halfW = size * 0.24f;

        using var shieldPath = CreateShieldPath(cx, top, bottom, halfW);
        using var whiteBrush = new SolidBrush(Color.White);
        g.FillPath(whiteBrush, shieldPath);

        // Inner blue crest quarters
        float innerTop = size * 0.29f;
        float innerBottom = size * 0.72f;
        float innerHalfW = size * 0.17f;
        using var innerShield = CreateShieldPath(cx, innerTop, innerBottom, innerHalfW);
        using var clipRegion = new Region(innerShield);
        var prevClip = g.Clip;
        g.Clip = clipRegion;

        float midY = (innerTop + innerBottom) * 0.48f;
        using var blueBrush = new SolidBrush(Color.FromArgb(2, 136, 209));
        // Top-left blue, bottom-right blue (checkerboard crest)
        g.FillRectangle(blueBrush, cx - innerHalfW - 2, innerTop - 2, innerHalfW + 2, midY - innerTop + 2);
        g.FillRectangle(blueBrush, cx, midY, innerHalfW + 2, innerBottom - midY + 2);

        g.Clip = prevClip;
    }

    private static GraphicsPath CreateShieldPath(float cx, float top, float bottom, float halfW)
    {
        var path = new GraphicsPath();
        float shoulderY = top + (bottom - top) * 0.12f;
        float midY = top + (bottom - top) * 0.55f;

        path.AddLines(
        [
            new PointF(cx, top),
            new PointF(cx + halfW, shoulderY),
            new PointF(cx + halfW * 0.92f, midY),
            new PointF(cx, bottom),
            new PointF(cx - halfW * 0.92f, midY),
            new PointF(cx - halfW, shoulderY)
        ]);
        path.CloseFigure();
        return path;
    }

    private static void DrawFtpDocument(Graphics g, int size)
    {
        // White document page with folded top-right corner (matching Screenshot 4 FTP icon)
        float left = size * 0.31f;
        float right = size * 0.69f;
        float top = size * 0.23f;
        float bottom = size * 0.77f;
        float fold = size * 0.13f;

        using var docPath = new GraphicsPath();
        docPath.AddPolygon(new[]
        {
            new PointF(left, top),
            new PointF(right - fold, top),
            new PointF(right, top + fold),
            new PointF(right, bottom),
            new PointF(left, bottom)
        });

        using var whiteBrush = new SolidBrush(Color.White);
        g.FillPath(whiteBrush, docPath);

        // Folded corner shadow
        using var foldBrush = new SolidBrush(Color.FromArgb(200, 230, 201));
        g.FillPolygon(foldBrush, new[]
        {
            new PointF(right - fold, top),
            new PointF(right, top + fold),
            new PointF(right - fold, top + fold)
        });
    }

    private static void DrawWebDavGlobe(Graphics g, int size)
    {
        float cx = size * 0.5f;
        float cy = size * 0.5f;
        float r = size * 0.25f;
        float stroke = Math.Max(1.5f, size * 0.045f);

        using var pen = new Pen(Color.White, stroke);
        // Outer circle
        g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        // Vertical meridian ellipse
        g.DrawEllipse(pen, cx - r * 0.48f, cy - r, r * 0.96f, r * 2);
        // Equator and latitude lines
        g.DrawLine(pen, cx - r, cy, cx + r, cy);
        g.DrawLine(pen, cx - r * 0.85f, cy - r * 0.48f, cx + r * 0.85f, cy - r * 0.48f);
        g.DrawLine(pen, cx - r * 0.85f, cy + r * 0.48f, cx + r * 0.85f, cy + r * 0.48f);
    }

    private static void DrawGoogleDriveTriangle(Graphics g, int size)
    {
        // Classic 3-ribbon Google Drive triangle in crisp white (matching Screenshot 4 Google Drive icon)
        float cx = size * 0.5f;
        float cy = size * 0.5f;
        float r = size * 0.27f;

        using var whiteBrush = new SolidBrush(Color.White);
        using var cutPen = new Pen(Color.FromArgb(84, 178, 88), Math.Max(1.5f, size * 0.04f));

        // Outer rounded hex-triangle
        PointF[] outer =
        [
            new PointF(cx - r * 0.30f, cy - r * 0.90f),
            new PointF(cx + r * 0.30f, cy - r * 0.90f),
            new PointF(cx + r * 0.98f, cy + r * 0.32f),
            new PointF(cx + r * 0.68f, cy + r * 0.85f),
            new PointF(cx - r * 0.68f, cy + r * 0.85f),
            new PointF(cx - r * 0.98f, cy + r * 0.32f)
        ];
        g.FillPolygon(whiteBrush, outer);

        // Inner triangular cutout
        PointF[] inner =
        [
            new PointF(cx, cy - r * 0.25f),
            new PointF(cx + r * 0.36f, cy + r * 0.38f),
            new PointF(cx - r * 0.36f, cy + r * 0.38f)
        ];
        using var innerBrush = new SolidBrush(Color.FromArgb(76, 166, 82));
        g.FillPolygon(innerBrush, inner);

        // Ribbon separator lines
        g.DrawLine(cutPen, cx + r * 0.30f, cy - r * 0.90f, cx - r * 0.36f, cy + r * 0.38f);
        g.DrawLine(cutPen, cx + r * 0.36f, cy + r * 0.38f, cx - r * 0.98f, cy + r * 0.32f);
        g.DrawLine(cutPen, cx, cy - r * 0.25f, cx + r * 0.68f, cy + r * 0.85f);
    }

    private static void DrawCloudflareR2Emblem(Graphics g, int size)
    {
        DrawCloudShape(g, size, Color.White);
    }

    private static void DrawCloudShape(Graphics g, int size, Color color)
    {
        using var brush = new SolidBrush(color);
        float w = size * 0.56f;
        float h = size * 0.34f;
        float left = (size - w) * 0.5f;
        float top = (size - h) * 0.52f;

        // Cloud puffs
        g.FillEllipse(brush, left + w * 0.18f, top - h * 0.25f, w * 0.46f, h * 0.85f);
        g.FillEllipse(brush, left + w * 0.48f, top - h * 0.05f, w * 0.36f, h * 0.70f);
        g.FillEllipse(brush, left, top + h * 0.12f, w * 0.38f, h * 0.68f);
        g.FillEllipse(brush, left + w * 0.58f, top + h * 0.18f, w * 0.42f, h * 0.62f);
        g.FillRectangle(brush, left + w * 0.16f, top + h * 0.32f, w * 0.66f, h * 0.48f);
    }

    private static void WriteMultiSizeIcon(Stream output, Func<int, Bitmap> renderer)
    {
        var payloads = new List<(int Size, byte[] Data)>();
        foreach (var size in IconSizes)
        {
            using var bmp = renderer(size);
            if (size >= 256)
            {
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                payloads.Add((size, ms.ToArray()));
            }
            else
            {
                payloads.Add((size, Encode32BppIconDib(bmp)));
            }
        }

        using var bw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        // ICONDIR header
        bw.Write((ushort)0); // Reserved
        bw.Write((ushort)1); // Type = 1 (ICO)
        bw.Write((ushort)payloads.Count);

        int offset = 6 + (16 * payloads.Count);
        foreach (var (size, data) in payloads)
        {
            bw.Write((byte)(size >= 256 ? 0 : size)); // Width
            bw.Write((byte)(size >= 256 ? 0 : size)); // Height
            bw.Write((byte)0);                        // ColorCount
            bw.Write((byte)0);                        // Reserved
            bw.Write((ushort)1);                      // Planes
            bw.Write((ushort)32);                     // BitCount
            bw.Write((uint)data.Length);              // BytesInRes
            bw.Write((uint)offset);                   // ImageOffset
            offset += data.Length;
        }

        foreach (var (_, data) in payloads)
        {
            bw.Write(data);
        }
    }

    private static byte[] Encode32BppIconDib(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        int xorStride = w * 4;
        int andRowBytes = ((w + 31) / 32) * 4;
        int xorBytes = xorStride * h;
        int andBytes = andRowBytes * h;

        using var ms = new MemoryStream(40 + xorBytes + andBytes);
        using var bw = new BinaryWriter(ms);

        // BITMAPINFOHEADER (biHeight is 2 * height in ICO DIBs to account for XOR + AND masks)
        bw.Write(40u);                  // biSize
        bw.Write(w);                    // biWidth
        bw.Write(h * 2);                // biHeight
        bw.Write((ushort)1);            // biPlanes
        bw.Write((ushort)32);           // biBitCount
        bw.Write(0u);                   // biCompression (BI_RGB)
        bw.Write((uint)(xorBytes + andBytes)); // biSizeImage
        bw.Write(0);                    // biXPelsPerMeter
        bw.Write(0);                    // biYPelsPerMeter
        bw.Write(0u);                   // biClrUsed
        bw.Write(0u);                   // biClrImportant

        var rect = new Rectangle(0, 0, w, h);
        var bmpData = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] rowBuf = new byte[xorStride];
            byte[] andMask = new byte[andBytes];

            // DIB rows are stored bottom-up
            for (int y = 0; y < h; y++)
            {
                int srcY = h - 1 - y;
                IntPtr rowPtr = bmpData.Scan0 + (srcY * bmpData.Stride);
                Marshal.Copy(rowPtr, rowBuf, 0, xorStride);
                bw.Write(rowBuf);

                int andRowOffset = y * andRowBytes;
                for (int x = 0; x < w; x++)
                {
                    byte alpha = rowBuf[x * 4 + 3];
                    if (alpha == 0)
                    {
                        andMask[andRowOffset + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    }
                }
            }

            bw.Write(andMask);
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }

        return ms.ToArray();
    }

    private const int SHCNE_DRIVEADD = 0x00000100;
    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;
    private const uint SHCNF_PATHW = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appID);
}
