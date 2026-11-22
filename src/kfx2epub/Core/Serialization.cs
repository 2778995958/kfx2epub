using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Kfx2Epub.Core;

public sealed class Deserializer
{
    private readonly byte[] _buffer;

    public int Offset { get; set; }

    public Deserializer(byte[] data)
    {
        _buffer = data;
    }

    public int Remaining => _buffer.Length - Offset;

    public byte[] Buffer => _buffer;

    public byte[] Extract(int? size = null, int? upto = null, bool advance = true)
    {
        var count = size ?? (upto.HasValue ? upto.Value - Offset : Remaining);
        if (count < 0 || Offset + count > _buffer.Length)
        {
            throw new InvalidOperationException($"Deserializer 資料不足（需要 {count} bytes，剩餘 {Remaining} bytes）");
        }

        var result = new byte[count];
        Array.Copy(_buffer, Offset, result, 0, count);
        if (advance)
        {
            Offset += count;
        }

        return result;
    }

    public ushort UnpackUInt16()
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(Offset));
        Offset += 2;
        return value;
    }

    public uint UnpackUInt32()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(Offset));
        Offset += 4;
        return value;
    }

    public ulong UnpackUInt64()
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(Offset));
        Offset += 8;
        return value;
    }

    public ushort UnpackUInt16BigEndian()
    {
        var value = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(Offset));
        Offset += 2;
        return value;
    }

    public ulong UnpackUInt64BigEndian()
    {
        var value = BinaryPrimitives.ReadUInt64BigEndian(_buffer.AsSpan(Offset));
        Offset += 8;
        return value;
    }

    public byte UnpackByte()
    {
        return _buffer[Offset++];
    }

    public string UnpackFourCC()
    {
        return Encoding.ASCII.GetString(Extract(4));
    }
}

public sealed class Serializer
{
    private readonly MemoryStream _stream = new();

    public int Length => (int)_stream.Length;

    public void Append(byte[] data) => _stream.Write(data, 0, data.Length);

    public void Pack(byte[] data) => Append(data);

    public void PackUInt16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void PackUInt32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public void PackUInt64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _stream.Write(buffer);
    }

    public int ReserveUInt32()
    {
        var position = (int)_stream.Position;
        PackUInt32(0);
        return position;
    }

    public void RepackUInt32(int position, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        var old = _stream.Position;
        _stream.Position = position;
        _stream.Write(buffer);
        _stream.Position = old;
    }

    public byte[] ToArray() => _stream.ToArray();

    public byte[] Sha1() => SHA1.HashData(ToArray());
}

public static class Hashing
{
    public static byte[] Sha1(byte[] data) => SHA1.HashData(data);

    public static string Sha1Hex(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
}

public static class PathUtil
{
    public static string RootPath(string path) => path.StartsWith('/') ? path : "/" + path;

    public static string UnrootPath(string path)
    {
        if (!path.StartsWith('/')) throw new InvalidOperationException($"路徑不是絕對形式：{path}");
        return path[1..];
    }

    public static string DirName(string filename)
    {
        if (!filename.StartsWith('/')) throw new InvalidOperationException($"路徑不是絕對形式：{filename}");
        var index = filename.LastIndexOf('/');
        return index <= 0 ? "/" : filename[..index];
    }

    public static string UrlAbsolutePath(string url, string refFrom = null, string workingDir = null)
    {
        if (refFrom is not null)
        {
            workingDir = DirName(refFrom);
        }

        var parsed = ParseUrl(url);
        if (parsed.Scheme is not null && parsed.Scheme != "internal-file" || parsed.Host.Length > 0)
        {
            return url;
        }

        return AbsolutePath(parsed.Path, workingDir) + (parsed.Fragment.Length > 0 ? "#" + parsed.Fragment : "");
    }

    public static string AbsolutePath(string relPath, string workingDir)
    {
        if (workingDir is null || !workingDir.StartsWith('/')) throw new InvalidOperationException("工作目錄必須是絕對路徑");
        if (relPath.StartsWith('/')) throw new InvalidOperationException("相對路徑不可為絕對路徑");

        var combined = workingDir.TrimEnd('/') + "/" + relPath;
        var segments = new List<string>();
        foreach (var part in combined.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..")
            {
                if (segments.Count == 0) throw new InvalidOperationException("相對路徑超出根目錄");
                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(part);
            }
        }

        return "/" + string.Join("/", segments);
    }

    public static string UrlRelativePath(string url, string refFrom = null, string workingDir = null, bool quote = true)
    {
        if (refFrom is not null)
        {
            workingDir = DirName(refFrom);
        }

        var parsed = ParseUrl(url);
        if (parsed.Scheme is not null && parsed.Scheme != "internal-file" || parsed.Host.Length > 0)
        {
            return url;
        }

        var rel = RelativePath(parsed.Path, workingDir) + (parsed.Fragment.Length > 0 ? "#" + parsed.Fragment : "");
        return quote ? Quote(rel) : rel;
    }

    public static string RelativePath(string absPath, string workingDir)
    {
        if (workingDir is null || !workingDir.StartsWith('/')) throw new InvalidOperationException("工作目錄必須是絕對路徑");
        if (!absPath.StartsWith('/')) throw new InvalidOperationException("絕對路徑必須以 / 開頭");

        var from = workingDir.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var to = absPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var common = 0;
        while (common < from.Length && common < to.Length && from[common] == to[common])
        {
            common++;
        }

        var parts = new List<string>();
        for (var i = common; i < from.Length; i++) parts.Add("..");
        for (var i = common; i < to.Length; i++) parts.Add(to[i]);
        var result = string.Join("/", parts);
        return result.Length == 0 ? "." : result;
    }

    public static string GetUrlFilename(string url)
    {
        var parsed = ParseUrl(url);
        if (parsed.Scheme is not null && parsed.Scheme != "internal-file" || parsed.Host.Length > 0) return null;
        var path = Uri.UnescapeDataString(parsed.Path);
        return path.StartsWith('/') ? path : null;
    }

    public static string RemoveUrlFragment(string filename) => filename.Split('#')[0];

    private static string Quote(string value)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' or '/' or '#')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2"));
            }
        }

        return sb.ToString();
    }

    private readonly record struct ParsedUrl(string Scheme, string Host, string Path, string Fragment);

    private static ParsedUrl ParseUrl(string url)
    {
        var fragment = "";
        var hash = url.IndexOf('#');
        if (hash >= 0)
        {
            fragment = url[(hash + 1)..];
            url = url[..hash];
        }

        string host = "";
        var scheme = "";
        var colon = url.IndexOf(':');
        var slash = url.IndexOf('/');
        if (colon > 0 && (slash < 0 || colon < slash))
        {
            var candidate = url[..colon];
            if (candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
            {
                scheme = candidate.ToLowerInvariant();
                url = url[(colon + 1)..];
            }
        }

        if (url.StartsWith("//"))
        {
            var end = url.IndexOf('/', 2);
            if (end < 0)
            {
                host = url[2..];
                url = "";
            }
            else
            {
                host = url[2..end];
                url = url[end..];
            }
        }

        if (scheme.Length == 0) scheme = "internal-file";
        return new ParsedUrl(scheme, host, url, fragment);
    }
}
