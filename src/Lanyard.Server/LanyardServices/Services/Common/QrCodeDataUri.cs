using QRCoder;

namespace Lanyard.Application.Services.Common;

// PNG QR code as a data: URI, for dropping straight into an <img src>. Shared by authenticator
// enrolment (two-factor) and the clock-in terminal's rotating code.
public static class QrCodeDataUri
{
    public static string Create(string content, int pixelsPerModule = 10)
    {
        using QRCodeGenerator generator = new();
        using QRCodeData data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        PngByteQRCode code = new(data);
        byte[] bytes = code.GetGraphic(pixelsPerModule);

        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }
}
