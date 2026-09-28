using ChamXanh.Api.Common;
using SkiaSharp;

namespace ChamXanh.Api.Modules.Media;

public record ImageVariant(string Name, byte[] Data, int Width, int Height);
public record ProcessedImage(IReadOnlyList<ImageVariant> Variants, ulong PHash, int Width, int Height);

public static class ImageProcessor
{
    public static readonly (string Name, int MaxSize, bool Watermark)[] Sizes = [("thumb", 200, false), ("card", 600, true), ("full", 1600, true)];
    public const int MaxBytes = 15 * 1024 * 1024;

    /// <summary>Giải mã (kiểm tra định dạng thật bằng nội dung, không tin phần mở rộng), sinh 3 biến thể WebP.
    /// Việc mã hóa lại ảnh đồng thời loại bỏ toàn bộ EXIF, kể cả GPS (BR-LST-08).</summary>
    public static ProcessedImage Process(byte[] input, string? watermark)
    {
        if (input.Length > MaxBytes) throw new DomainException("FILE_TOO_LARGE", "Ảnh tối đa 15MB");
        using var codec = SKCodec.Create(new MemoryStream(input))
            ?? throw new DomainException("UNSUPPORTED_IMAGE", "File không phải ảnh hợp lệ (JPEG, PNG, WebP, HEIF)");
        using var decoded = SKBitmap.Decode(codec) ?? throw new DomainException("UNSUPPORTED_IMAGE", "Không đọc được ảnh");
        using var source = ApplyOrientation(decoded, codec.EncodedOrigin);
        if (source.Width < 300 || source.Height < 300) throw new DomainException("IMAGE_TOO_SMALL", "Ảnh quá nhỏ, tối thiểu 300×300 px");

        var variants = Sizes.Select(s =>
        {
            using var resized = Resize(source, s.MaxSize);
            if (s.Watermark && !string.IsNullOrWhiteSpace(watermark)) DrawWatermark(resized, watermark);
            using var img = SKImage.FromBitmap(resized);
            using var data = img.Encode(SKEncodedImageFormat.Webp, 80);
            return new ImageVariant(s.Name, data.ToArray(), resized.Width, resized.Height);
        }).ToList();

        return new ProcessedImage(variants, DHash(source), source.Width, source.Height);
    }

    static SKBitmap Resize(SKBitmap src, int maxSize)
    {
        var scale = Math.Min(1f, (float)maxSize / Math.Max(src.Width, src.Height));
        var info = new SKImageInfo(Math.Max(1, (int)(src.Width * scale)), Math.Max(1, (int)(src.Height * scale)));
        return src.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? src.Copy();
    }

    static void DrawWatermark(SKBitmap bmp, string text)
    {
        using var canvas = new SKCanvas(bmp);
        var size = Math.Max(12, bmp.Width / 28f);
        using var font = new SKFont(SKTypeface.Default, size);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 90), IsAntialias = true };
        using var paint = new SKPaint { Color = new SKColor(255, 255, 255, 170), IsAntialias = true };
        var label = $"Chạm Xanh · {text}";
        var x = bmp.Width - size * 0.6f;
        var y = bmp.Height - size * 0.6f;
        canvas.DrawText(label, x + 1, y + 1, SKTextAlign.Right, font, shadow);
        canvas.DrawText(label, x, y, SKTextAlign.Right, font, paint);
    }

    static SKBitmap ApplyOrientation(SKBitmap bmp, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft) return bmp.Copy();
        var rotated = origin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(rotated ? bmp.Height : bmp.Width, rotated ? bmp.Width : bmp.Height);
        using var c = new SKCanvas(result);
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight: c.RotateDegrees(180, bmp.Width / 2f, bmp.Height / 2f); break;
            case SKEncodedOrigin.RightTop: c.Translate(result.Width, 0); c.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: c.Translate(0, result.Height); c.RotateDegrees(270); break;
        }
        using var image = SKImage.FromBitmap(bmp);
        c.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return result;
    }

    /// <summary>Difference hash 64 bit: ảnh gần giống nhau (nén lại, đổi cỡ) cho khoảng cách Hamming nhỏ.</summary>
    public static ulong DHash(SKBitmap src)
    {
        using var small = src.Resize(new SKImageInfo(9, 8, SKColorType.Gray8), new SKSamplingOptions(SKFilterMode.Linear)) ?? throw new InvalidOperationException();
        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++, bit++)
                if (small.GetPixel(x, y).Red > small.GetPixel(x + 1, y).Red) hash |= 1UL << bit;
        return hash;
    }

    public static int HammingDistance(ulong a, ulong b) => System.Numerics.BitOperations.PopCount(a ^ b);
}
