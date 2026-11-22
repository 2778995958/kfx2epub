using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kfx2Epub.Core;

public sealed class ImageInfo
{
    public string Format { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool Transparent { get; init; }
    public bool Animated { get; init; }
}

public static class ImageUtil
{
    public static readonly Dictionary<string, string> FormatSymbols = new()
    {
        ["bmp"] = "$599", ["gif"] = "$286", ["jpg"] = "$285", ["jxr"] = "$548", ["pbm"] = "$420",
        ["pdf"] = "$565", ["png"] = "$284", ["pobject"] = "$287", ["tiff"] = "$600", ["bpg"] = "$612"
    };

    public static readonly Dictionary<string, string> SymbolFormats =
        FormatSymbols.ToDictionary(p => p.Value, p => p.Key);

    public static readonly Dictionary<string, string> MimetypeOfExt = new()
    {
        [".aax"] = "audio/vnd.audible.aax", [".apnx"] = "application/x-apnx-sidecar",
        [".bin"] = "application/octet-stream", [".bmp"] = "image/bmp", [".css"] = "text/css",
        [".eot"] = "application/vnd.ms-fontobject", [".dfont"] = "application/x-dfont",
        [".epub"] = "application/epub+zip", [".gif"] = "image/gif", [".htm"] = "text/html",
        [".html"] = "text/html", [".ico"] = "image/x-icon", [".jpeg"] = "image/jpeg",
        [".jpg"] = "image/jpeg", [".js"] = "text/javascript", [".jxr"] = "image/vnd.ms-photo",
        [".kvg"] = "image/kvg", [".mp3"] = "audio/mpeg", [".mp4"] = "video/mp4",
        [".mpg"] = "video/mpeg", [".ncx"] = "application/x-dtbncx+xml",
        [".opf"] = "application/oebps-package+xml", [".otf"] = "font/otf",
        [".pfb"] = "application/x-font-type1", [".png"] = "image/png", [".pdf"] = "application/pdf",
        [".pobject"] = "application/azn-plugin-object", [".svg"] = "image/svg+xml",
        [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".ttf"] = "font/ttf",
        [".txt"] = "text/plain", [".webp"] = "image/webp", [".woff"] = "application/font-woff",
        [".woff2"] = "font/woff2", [".xhtml"] = "application/xhtml+xml", [".xml"] = "application/xml"
    };

    public static readonly Dictionary<string, string> ResourceTypeOfExt = new()
    {
        [".bmp"] = "image", [".css"] = "styles", [".eot"] = "font", [".dfont"] = "font",
        [".gif"] = "image", [".htm"] = "text", [".html"] = "text", [".ico"] = "image",
        [".jpg"] = "image", [".js"] = "text", [".jxr"] = "image", [".kvg"] = "image",
        [".mp3"] = "audio", [".mp4"] = "video", [".otf"] = "font", [".pdf"] = "image",
        [".png"] = "image", [".svg"] = "image", [".tif"] = "image", [".tiff"] = "image",
        [".ttf"] = "font", [".txt"] = "text", [".webp"] = "video", [".woff"] = "font",
        [".woff2"] = "font"
    };

    public static readonly Dictionary<string, string[]> ExtsOfMimetype = new()
    {
        ["application/azn-plugin-object"] = new[] { ".pobject" },
        ["application/epub+zip"] = new[] { ".epub" },
        ["application/font-sfnt"] = new[] { ".ttf", ".otf" },
        ["application/font-woff"] = new[] { ".woff" },
        ["application/font-woff2"] = new[] { ".woff2" },
        ["application/javascript"] = new[] { ".js", ".jsonp", ".json" },
        ["application/json"] = new[] { ".json" },
        ["application/oebps-package+xml"] = new[] { ".opf" },
        ["application/pdf"] = new[] { ".pdf" },
        ["application/vnd.adobe-page-template+xml"] = new[] { ".xpgt" },
        ["application/vnd.amazon.ebook"] = new[] { ".azw" },
        ["application/vnd.ms-fontobject"] = new[] { ".eot" },
        ["application/vnd.ms-opentype"] = new[] { ".otf", ".ttf" },
        ["application/x-mobipocket-ebook-mop"] = new[] { ".azw4" },
        ["application/x-font-otf"] = new[] { ".otf" },
        ["application/x-font-truetype"] = new[] { ".ttf" },
        ["application/x-font-ttf"] = new[] { ".ttf" },
        ["application/x-font-woff"] = new[] { ".woff" },
        ["application/x-html"] = new[] { ".html" },
        ["application/xhtml+xml"] = new[] { ".xhtml", ".html", ".htm" },
        ["application/xml"] = new[] { ".xml" },
        ["audio"] = new[] { ".mp3" },
        ["audio/mpeg"] = new[] { ".mp3" },
        ["audio/mp4"] = new[] { ".mp4" },
        ["figure"] = new[] { ".figure" },
        ["font/otf"] = new[] { ".otf" },
        ["font/ttf"] = new[] { ".ttf" },
        ["font/woff"] = new[] { ".woff" },
        ["font/woff2"] = new[] { ".woff2" },
        ["image/bmp"] = new[] { ".bmp" },
        ["image/gif"] = new[] { ".gif" },
        ["image/jpeg"] = new[] { ".jpg", ".jpeg" },
        ["image/jpg"] = new[] { ".jpg", ".jpeg" },
        ["image/jxr"] = new[] { ".jxr" },
        ["image/png"] = new[] { ".png" },
        ["image/svg+xml"] = new[] { ".svg" },
        ["image/tiff"] = new[] { ".tif", ".tiff" },
        ["image/vnd.ms-photo"] = new[] { ".jxr" },
        ["image/vnd.jxr"] = new[] { ".jxr" },
        ["image/webp"] = new[] { ".webp" },
        ["image/x-icon"] = new[] { ".ico" },
        ["plugin/kfx-html-article"] = new[] { ".html" },
        ["res/img"] = new[] { ".png" },
        ["res/kvg"] = new[] { ".kvg" },
        ["text/css"] = new[] { ".css" },
        ["text/html"] = new[] { ".html", ".htm" },
        ["text/javascript"] = new[] { ".js" },
        ["text/plain"] = new[] { ".txt" },
        ["text/xml"] = new[] { ".xml" },
        ["video"] = new[] { ".mp4" },
        ["video/mp4"] = new[] { ".mp4" },
        ["video/mpeg"] = new[] { ".mpg" }
    };

    public static ImageInfo GetImageInfo(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var format = GuessFormat(data);
            var transparent = frame.Format.ToString().Contains("Bgra", StringComparison.OrdinalIgnoreCase) ||
                              frame.Format.ToString().Contains("Rgba", StringComparison.OrdinalIgnoreCase) ||
                              frame.Format.ToString().Contains("Pbgra", StringComparison.OrdinalIgnoreCase);
            var animated = decoder.Frames.Count > 1;
            return new ImageInfo
            {
                Format = format,
                Width = frame.PixelWidth,
                Height = frame.PixelHeight,
                Transparent = transparent,
                Animated = animated
            };
        }
        catch
        {
            var (width, height) = GetDimensionsFromHeader(data);
            return new ImageInfo
            {
                Format = GuessFormat(data),
                Width = width,
                Height = height,
                Transparent = false,
                Animated = false
            };
        }
    }

    public static string GuessFormat(byte[] data)
    {
        if (StartsWith(data, "GIF87a") || StartsWith(data, "GIF89a")) return "gif";
        if (StartsWith(data, new byte[] { 0xff, 0xd8, 0xff })) return "jpg";
        if (StartsWith(data, new byte[] { 0x49, 0x49, 0xbc, 0x01 })) return "jxr";
        if (StartsWith(data, new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "png";
        if (StartsWith(data, new byte[] { 0x25, 0x50, 0x44, 0x46 })) return "pdf";
        if (StartsWith(data, new byte[] { 0x49, 0x49, 0x2a, 0x00 }) ||
            StartsWith(data, new byte[] { 0x4d, 0x4d, 0x00, 0x2a })) return "tif";
        if (data.Length >= 12 && StartsWith(data, "RIFF") && data.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return "webp";
        return "";
    }

    public static string ImageFileExt(byte[] data, string defaultValue = "")
    {
        if (StartsWith(data, "GIF87a") || StartsWith(data, "GIF89a")) return ".gif";
        if (StartsWith(data, new byte[] { 0xff, 0xd8, 0xff })) return ".jpg";
        if (StartsWith(data, new byte[] { 0x49, 0x49, 0xbc, 0x01 })) return ".jxr";
        if (StartsWith(data, new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return ".png";
        if (StartsWith(data, new byte[] { 0x25, 0x50, 0x44, 0x46 })) return ".pdf";
        if (StartsWith(data, new byte[] { 0x49, 0x49, 0x2a, 0x00 }) ||
            StartsWith(data, new byte[] { 0x4d, 0x4d, 0x00, 0x2a })) return ".tif";
        return defaultValue;
    }

    public static string FontFileExt(byte[] data, string defaultValue = "")
    {
        if (data.Length < 4) return defaultValue;
        if (data.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 1, 0, 0 }) ||
            data.AsSpan(0, 4).SequenceEqual("true"u8) ||
            data.AsSpan(0, 4).SequenceEqual("typ1"u8)) return ".ttf";
        if (data.AsSpan(0, 4).SequenceEqual("OTTO"u8)) return ".otf";
        if (data.AsSpan(0, 4).SequenceEqual("wOFF"u8)) return ".woff";
        if (data.AsSpan(0, 4).SequenceEqual("wOF2"u8)) return ".woff2";
        if (data.Length >= 36 && data.AsSpan(34, 2).SequenceEqual(new byte[] { 0x4c, 0x50 }) &&
            (data.AsSpan(8, 4).SequenceEqual(new byte[] { 0, 0, 1, 0 }) ||
             data.AsSpan(8, 4).SequenceEqual(new byte[] { 1, 0, 2, 0 }) ||
             data.AsSpan(8, 4).SequenceEqual(new byte[] { 2, 0, 2, 0 }))) return ".eot";
        if (data.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0, 1, 0 })) return ".dfont";
        if (data.Length >= 24 && data.AsSpan(0, 2).SequenceEqual(new byte[] { 0x80, 0x01 }) &&
            data.AsSpan(6, 18).SequenceEqual("%!PS-AdobeFont-1.0"u8)) return ".pfb";
        return defaultValue;
    }

    public static (byte[] Data, string Format) ConvertJxrToJpegOrPng(byte[] jxrData, string resourceName)
    {
        try
        {
            using var stream = new MemoryStream(jxrData);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var hasAlpha = frame.Format.ToString().Contains("Bgra", StringComparison.OrdinalIgnoreCase) ||
                           frame.Format.ToString().Contains("Rgba", StringComparison.OrdinalIgnoreCase) ||
                           frame.Format.ToString().Contains("Pbgra", StringComparison.OrdinalIgnoreCase);
            using var outStream = new MemoryStream();

            if (hasAlpha)
            {
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(frame));
                png.Save(outStream);
                return (outStream.ToArray(), "$284");
            }

            BitmapSource source = frame;
            if (frame.Format != PixelFormats.Bgr24 && frame.Format != PixelFormats.Bgr32)
            {
                source = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
            }

            var jpeg = new JpegBitmapEncoder { QualityLevel = 95 };
            jpeg.Frames.Add(BitmapFrame.Create(source));
            jpeg.Save(outStream);
            return (outStream.ToArray(), "$285");
        }
        catch (Exception ex)
        {
            Log.Error($"JPEG-XR 轉換失敗（{resourceName}）：{ex.Message}");
            return (jxrData, "$548");
        }
    }

    public static (byte[] Data, string Format) CombineTiles(
        string resourceName, int resourceHeight, int resourceWidth, string resourceFormat,
        int tileHeight, int tileWidth, int tilePadding, List<List<string>> yjTiles,
        List<byte[]> tilesRawMedia, bool ignoreVariants)
    {
        if (resourceWidth <= 0 || resourceHeight <= 0)
        {
            return (null, null);
        }

        var frames = new List<BitmapSource>();
        var missing = new List<(int X, int Y)>();
        var tileNum = 0;
        foreach (var row in yjTiles)
        {
            for (var x = 0; x < row.Count; x++)
            {
                var raw = tileNum < tilesRawMedia.Count ? tilesRawMedia[tileNum] : null;
                if (raw is null)
                {
                    frames.Add(null);
                    missing.Add((x, frames.Count / Math.Max(1, yjTiles[0].Count)));
                }
                else
                {
                    try
                    {
                        var decoder = BitmapDecoder.Create(new MemoryStream(raw), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        frames.Add(decoder.Frames[0]);
                    }
                    catch
                    {
                        frames.Add(null);
                    }
                }

                tileNum++;
            }
        }

        if (missing.Count > 0)
        {
            Log.Error($"資源 {resourceName} 缺少圖磚：{string.Join(",", missing)}");
            if (ignoreVariants) return (null, null);
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, resourceWidth, resourceHeight));
            var index = 0;
            for (var y = 0; y < yjTiles.Count; y++)
            {
                var topPadding = y == 0 ? 0 : tilePadding;
                var bottomPadding = Math.Min(tilePadding, resourceHeight - tileHeight * (y + 1));
                for (var x = 0; x < yjTiles[y].Count; x++)
                {
                    var leftPadding = x == 0 ? 0 : tilePadding;
                    var rightPadding = Math.Min(tilePadding, resourceWidth - tileWidth * (x + 1));
                    var frame = frames[index++];
                    if (frame is not null)
                    {
                        var crop = new CroppedBitmap(frame, new System.Windows.Int32Rect(
                            leftPadding, topPadding, Math.Max(1, tileWidth), Math.Max(1, tileHeight)));
                        dc.DrawImage(crop, new System.Windows.Rect(x * tileWidth, y * tileHeight, tileWidth, tileHeight));
                    }
                }
            }
        }

        var bitmap = new RenderTargetBitmap(resourceWidth, resourceHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        using var output = new MemoryStream();
        var fmt = SymbolFormats.TryGetValue(resourceFormat ?? "$284", out var ext) ? ext : "png";
        if (fmt is "jpg" or "jpeg")
        {
            var flattened = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
            var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
            encoder.Frames.Add(BitmapFrame.Create(flattened));
            encoder.Save(output);
            return (output.ToArray(), "$285");
        }

        var pngEncoder = new PngBitmapEncoder();
        pngEncoder.Frames.Add(BitmapFrame.Create(bitmap));
        pngEncoder.Save(output);
        return (output.ToArray(), "$284");
    }

    private static bool StartsWith(byte[] data, string text)
        => data.AsSpan().StartsWith(System.Text.Encoding.ASCII.GetBytes(text));

    private static bool StartsWith(byte[] data, byte[] prefix) => data.AsSpan().StartsWith(prefix);

    private static (int Width, int Height) GetDimensionsFromHeader(byte[] data)
    {
        if (data.Length >= 24 && StartsWith(data, new byte[] { 0x89, 0x50, 0x4e, 0x47 }))
        {
            var width = (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19];
            var height = (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23];
            return (width, height);
        }

        if (data.Length >= 10 && (StartsWith(data, "GIF87a") || StartsWith(data, "GIF89a")))
        {
            return (data[6] | (data[7] << 8), data[8] | (data[9] << 8));
        }

        if (data.Length >= 4 && StartsWith(data, new byte[] { 0xff, 0xd8 }))
        {
            var index = 2;
            while (index + 9 < data.Length)
            {
                if (data[index] != 0xff)
                {
                    index++;
                    continue;
                }

                var marker = data[index + 1];
                if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
                {
                    var height = (data[index + 5] << 8) | data[index + 6];
                    var width = (data[index + 7] << 8) | data[index + 8];
                    return (width, height);
                }

                var length = (data[index + 2] << 8) | data[index + 3];
                index += 2 + Math.Max(2, length);
            }
        }

        return (0, 0);
    }
}
