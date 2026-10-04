using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CommentsApi.Tests.Infrastructure;

/// <summary>Tiny deterministic image factory backed by ImageSharp (transitive via the backend project).</summary>
public static class ImageFixtures
{
    public static byte[] Png(int width, int height) => Save(width, height, "png");

    public static byte[] Jpg(int width, int height) => Save(width, height, "jpg");

    public static byte[] Gif(int width, int height) => Save(width, height, "gif");

    public static byte[] Bmp(int width, int height) => Save(width, height, "bmp");

    public static byte[] Tiff(int width, int height) => Save(width, height, "tiff");

    /// <summary>A minimal RIFF/WEBP container — an unsupported format for the uploader.</summary>
    public static byte[] Webp()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(18); // file size after this field
        writer.Write("WEBP"u8.ToArray());
        writer.Write("VP8 "u8.ToArray());
        writer.Write(6); // chunk size
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x9d, 0x01, 0x2a });
        writer.Flush();
        return ms.ToArray();
    }

    public static byte[] Save(int width, int height, string format)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32((byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256));
            }
        }

        using var ms = new MemoryStream();
        switch (format.ToLowerInvariant())
        {
            case "png":
                image.SaveAsPng(ms);
                break;
            case "jpg":
            case "jpeg":
                image.SaveAsJpeg(ms);
                break;
            case "gif":
                image.SaveAsGif(ms);
                break;
            case "bmp":
                image.SaveAsBmp(ms);
                break;
            case "tif":
            case "tiff":
                image.SaveAsTiff(ms);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported fixture format.");
        }

        return ms.ToArray();
    }

    /// <summary>Incompressible random PNG, used to build a real image larger than 5 MB.</summary>
    public static byte[] RandomNoisePng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height);
        var random = new Random(20250522);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        }

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    public static (int Width, int Height) Size(byte[] bytes)
    {
        using var image = Image.Load(bytes);
        return (image.Width, image.Height);
    }
}
