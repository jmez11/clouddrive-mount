using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Services;

namespace CloudDriveMount.App.Helpers;

public static class IconImageHelper
{
    private static readonly Dictionary<(StorageProtocol, bool), ImageSource> Cache = new();

    public static ImageSource GetProtocolImage(StorageProtocol protocol, bool isLocalDisk = false)
    {
        var key = (protocol, isLocalDisk);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        using var bmp = ExplorerIconManager.RenderProtocolBitmap(protocol, 64, isLocalDisk);
        var source = BitmapToImageSource(bmp);
        Cache[key] = source;
        return source;
    }

    public static ImageSource GetAppLogoImage()
    {
        using var bmp = ExplorerIconManager.RenderAppLogoBitmap(64);
        return BitmapToImageSource(bmp);
    }

    private static BitmapImage BitmapToImageSource(System.Drawing.Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
