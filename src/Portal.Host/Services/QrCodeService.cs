using System;
using System.IO;
using System.Windows.Media.Imaging;
using Portal.Common;
using QRCoder;

namespace Portal.Host.Services;

public class QrCodeService
{
    public BitmapImage? GenerateQrCode(string payload)
    {
        try
        {
            using var qrGenerator = new QRCodeGenerator();
            using var qrCodeData = qrGenerator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new BitmapByteQRCode(qrCodeData);
            var qrBytes = qrCode.GetGraphic(5); // 5 pixels per module

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(qrBytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            Logger.Log($"[QrCodeService] QR code generated: {payload}");
            return bitmap;
        }
        catch (Exception ex)
        {
            Logger.LogError("[QrCodeService] QR code generation failed", ex);
            return null;
        }
    }
}
