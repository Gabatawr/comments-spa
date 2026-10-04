using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace CommentsApi.Services;

/// <summary>
/// Renders a real PNG CAPTCHA using a built-in 5x7 bitmap glyph table and ImageSharp
/// (pixel drawing only: gradient background, noise lines/dots and rotated glyphs).
/// The code is stored in <see cref="IMemoryCache"/> for 5 minutes, is one-time use and is
/// never returned to clients.
/// </summary>
public sealed class CaptchaService : ICaptchaService
{
    public const int CodeLength = 6;
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    /// <summary>Latin uppercase + digits without ambiguous 0/O/1/I.</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public const int ImageWidth = 220;
    public const int ImageHeight = 70;

    private const int GlyphScale = 5;

    private static readonly Rgba32[] GlyphColors =
    {
        new(31, 41, 55),
        new(55, 65, 81),
        new(17, 24, 39),
        new(30, 58, 138),
        new(120, 53, 15),
        new(88, 28, 135),
        new(15, 118, 110),
    };

    private readonly IMemoryCache _cache;

    public CaptchaService(IMemoryCache cache)
    {
        _cache = cache;
    }

    private static string CacheKey(string captchaId) => $"captcha:{captchaId}";

    public CaptchaChallenge Generate()
    {
        var code = GenerateCode();
        var captchaId = Guid.NewGuid().ToString();
        var png = RenderPng(code);

        _cache.Set(
            CacheKey(captchaId),
            code,
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Ttl });

        return new CaptchaChallenge
        {
            CaptchaId = captchaId,
            Image = "data:image/png;base64," + Convert.ToBase64String(png),
            ExpiresInSeconds = (int)Ttl.TotalSeconds,
            Code = code,
        };
    }

    public bool Validate(string? captchaId, string? answer)
    {
        if (string.IsNullOrWhiteSpace(captchaId) || string.IsNullOrWhiteSpace(answer))
        {
            return false;
        }

        var key = CacheKey(captchaId);
        if (!_cache.TryGetValue(key, out string? code) || code is null)
        {
            return false;
        }

        // One-time use: consumed on the first attempt, even if the answer is wrong.
        _cache.Remove(key);
        return string.Equals(code, answer.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public string? PeekAnswer(string? captchaId)
    {
        if (string.IsNullOrWhiteSpace(captchaId))
        {
            return null;
        }

        return _cache.TryGetValue(CacheKey(captchaId), out string? code) ? code : null;
    }

    private static string GenerateCode()
    {
        var chars = new char[CodeLength];
        for (var i = 0; i < CodeLength; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    private static byte[] RenderPng(string code)
    {
        using var image = new Image<Rgba32>(ImageWidth, ImageHeight);

        // Background: soft vertical gradient.
        for (var y = 0; y < ImageHeight; y++)
        {
            var t = ImageHeight == 1 ? 0f : y / (float)(ImageHeight - 1);
            var r = (byte)(247 - (int)(22 * t));
            var g = (byte)(249 - (int)(18 * t));
            var b = (byte)(252 - (int)(10 * t));
            for (var x = 0; x < ImageWidth; x++)
            {
                image[x, y] = new Rgba32(r, g, b);
            }
        }

        // Background noise: dots + light lines.
        for (var i = 0; i < 320; i++)
        {
            var x = Random.Shared.Next(ImageWidth);
            var y = Random.Shared.Next(ImageHeight);
            var shade = (byte)Random.Shared.Next(195, 235);
            image[x, y] = new Rgba32(shade, shade, (byte)Math.Min(255, shade + 6));
        }

        for (var i = 0; i < 9; i++)
        {
            var x1 = Random.Shared.Next(ImageWidth);
            var y1 = Random.Shared.Next(ImageHeight);
            var x2 = Random.Shared.Next(ImageWidth);
            var y2 = Random.Shared.Next(ImageHeight);
            var shade = (byte)Random.Shared.Next(160, 205);
            DrawLine(image, x1, y1, x2, y2, new Rgba32(shade, shade, shade));
        }

        // Glyphs, each rotated slightly and vertically jittered.
        var cellWidth = ImageWidth / CodeLength;
        for (var i = 0; i < code.Length; i++)
        {
            var angle = (float)((Random.Shared.NextDouble() - 0.5) * 0.5); // ~±14°
            var centerX = (i * cellWidth) + (cellWidth / 2f);
            var centerY = (ImageHeight / 2f) + (float)((Random.Shared.NextDouble() - 0.5) * 8);
            var color = GlyphColors[Random.Shared.Next(GlyphColors.Length)];
            DrawGlyph(image, code[i], centerX, centerY, angle, color);
        }

        // Foreground noise lines on top of the text.
        for (var i = 0; i < 4; i++)
        {
            var x1 = Random.Shared.Next(ImageWidth);
            var y1 = Random.Shared.Next(ImageHeight);
            var x2 = Random.Shared.Next(ImageWidth);
            var y2 = Random.Shared.Next(ImageHeight);
            DrawLine(image, x1, y1, x2, y2, new Rgba32(150, 160, 175));
        }

        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static void DrawGlyph(Image<Rgba32> image, char c, float centerX, float centerY, float angle, Rgba32 color)
    {
        var mask = GlyphTable.Get(c);
        var glyphWidth = GlyphTable.Width * GlyphScale;
        var glyphHeight = GlyphTable.Height * GlyphScale;
        var cos = MathF.Cos(angle);
        var sin = MathF.Sin(angle);

        for (var gy = 0; gy < GlyphTable.Height; gy++)
        {
            for (var gx = 0; gx < GlyphTable.Width; gx++)
            {
                if (!mask[gy, gx])
                {
                    continue;
                }

                for (var sy = 0; sy < GlyphScale; sy++)
                {
                    for (var sx = 0; sx < GlyphScale; sx++)
                    {
                        var lx = (gx * GlyphScale) + sx - (glyphWidth / 2f) + 0.5f;
                        var ly = (gy * GlyphScale) + sy - (glyphHeight / 2f) + 0.5f;

                        var rx = (lx * cos) - (ly * sin);
                        var ry = (lx * sin) + (ly * cos);

                        var px = (int)MathF.Round(centerX + rx);
                        var py = (int)MathF.Round(centerY + ry);

                        if (px >= 0 && px < image.Width && py >= 0 && py < image.Height)
                        {
                            image[px, py] = color;
                        }
                    }
                }
            }
        }
    }

    private static void DrawLine(Image<Rgba32> image, int x0, int y0, int x1, int y1, Rgba32 color)
    {
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;

        while (true)
        {
            if (x0 >= 0 && x0 < image.Width && y0 >= 0 && y0 < image.Height)
            {
                image[x0, y0] = color;
            }

            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }
}
