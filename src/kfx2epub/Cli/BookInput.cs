namespace Kfx2Epub.Cli;

public static class BookInput
{
    private static readonly byte[] ZipSignature = { 0x50, 0x4B, 0x03, 0x04 };
    private static readonly byte[] KfxSignature = "CONT"u8.ToArray();

    public static string Normalize(string input) => Path.GetFullPath(input);

    public static void ValidateFile(string path)
    {
        if (Directory.Exists(path))
        {
            throw new CliException("INPUT_NOT_KFX", "輸入是資料夾，probe／convert 需要單一 KFX／KFX-ZIP 檔案。");
        }

        if (!File.Exists(path))
        {
            throw new CliException("INPUT_NOT_FOUND", $"找不到輸入檔案：{path}");
        }

        if (new FileInfo(path).Length == 0)
        {
            throw new CliException("INPUT_EMPTY", $"輸入檔案是空的：{path}");
        }
    }

    // 依副檔名與檔頭判斷；無法辨識回 null。
    public static string DetectFormat(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".kfx-zip" or ".zip" or ".azk") return "kfx-zip";
        if (ext is ".kfx" or ".azw8" or ".ion") return "kfx";
        if (ext == ".kpf") return "kpf";

        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        if (read >= 4 && header.SequenceEqual(ZipSignature)) return "kfx-zip";
        if (read >= 4 && header.SequenceEqual(KfxSignature)) return "kfx";
        return null;
    }

    public static string RequireSupportedFormat(string path)
    {
        var format = DetectFormat(path);
        if (format is null)
        {
            throw new CliException("INPUT_NOT_KFX", "無法辨識輸入格式；只支援 kfx 與 kfx-zip。");
        }

        if (format is "kpf")
        {
            throw new CliException("UNSUPPORTED_FORMAT", "此版本不支援 KPF 輸入。");
        }

        return format;
    }

    public static bool StartsWith(byte[] data, byte[] signature)
        => data.Length >= signature.Length && data.AsSpan(0, signature.Length).SequenceEqual(signature);

    public static bool IsZipHeader(byte[] data) => StartsWith(data, ZipSignature);

    public static string ToHex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}
