using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Portal.Common;

namespace Portal.Host.Helpers;

internal static class AvatarImageLoader
{
    private static readonly HttpClient HttpClient = new();

    public static async Task<BitmapSource?> LoadAs96DpiAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;

            var frame = BitmapFrame.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var source = EnsureBgra32(frame);

            var width = source.PixelWidth;
            var height = source.PixelHeight;
            var stride = (width * source.Format.BitsPerPixel + 7) / 8;
            var pixels = new byte[stride * height];
            source.CopyPixels(pixels, stride, 0);

            var normalizedBitmap = BitmapSource.Create(
                width,
                height,
                96,
                96,
                source.Format,
                source.Palette,
                pixels,
                stride);
            normalizedBitmap.Freeze();

            return normalizedBitmap;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[AvatarImageLoader] Failed to load avatar from '{url}'.", ex);
            return null;
        }
    }

    private static BitmapSource EnsureBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32 || source.Format == PixelFormats.Pbgra32)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = source;
        converted.DestinationFormat = PixelFormats.Bgra32;
        converted.EndInit();
        converted.Freeze();
        return converted;
    }
}
