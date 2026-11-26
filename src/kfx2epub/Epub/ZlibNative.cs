using System.Runtime.InteropServices;

namespace Kfx2Epub.Epub;

// 直接綁定與 Python zlib 相同實作的 zlib1.dll（官方原始碼 + /MT 建置），
// 讓 C# 產生的 deflate 位元流與 Python 逐位元相同。
internal static class ZlibNative
{
    private const string Library = "zlib1.dll";
    private const int ZOk = 0;
    private const int ZStreamEnd = 1;
    private const int ZDeflated = 8;
    private const int ZFinish = 4;
    private const int DefaultLevel = 6;

    // z_stream 的 uLong 在 Windows 是 32 位元，欄位順序依 zlib.h。
    [StructLayout(LayoutKind.Sequential)]
    private struct ZStream
    {
        public IntPtr NextIn;
        public uint AvailIn;
        public uint TotalIn;
        public IntPtr NextOut;
        public uint AvailOut;
        public uint TotalOut;
        public IntPtr Msg;
        public IntPtr State;
        public IntPtr Zalloc;
        public IntPtr Zfree;
        public IntPtr Opaque;
        public int DataType;
        public uint Adler;
        public uint Reserved;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr zlibVersion();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int deflateInit2_(ref ZStream stream, int level, int method,
        int windowBits, int memLevel, int strategy, IntPtr version, int streamSize);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int deflate(ref ZStream stream, int flush);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int deflateEnd(ref ZStream stream);

    public static byte[] Deflate(byte[] data, int level = DefaultLevel)
    {
        try
        {
            return DeflateCore(data, level);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "找不到 zlib1.dll，請確認它與 kfx2epub 執行檔放在同一目錄。", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new InvalidOperationException(
                "zlib1.dll 版本不相容（缺少 deflate 相關進入點）。", ex);
        }
    }

    private static byte[] DeflateCore(byte[] data, int level)
    {
        var stream = new ZStream();
        var version = zlibVersion();
        var code = deflateInit2_(ref stream, level, ZDeflated, -15, 8, 0, version,
            Marshal.SizeOf<ZStream>());
        if (code != ZOk) throw new InvalidOperationException($"deflateInit2_ 失敗：{code}");

        var inputSize = Math.Max(data.Length, 1);
        var input = Marshal.AllocHGlobal(inputSize);
        var buffer = Marshal.AllocHGlobal(262144);
        try
        {
            Marshal.Copy(data, 0, input, data.Length);
            stream.NextIn = input;
            stream.AvailIn = (uint)data.Length;

            using var output = new MemoryStream();
            while (true)
            {
                stream.NextOut = buffer;
                stream.AvailOut = 262144;
                code = deflate(ref stream, ZFinish);
                var produced = 262144 - (int)stream.AvailOut;
                if (produced > 0)
                {
                    var chunk = new byte[produced];
                    Marshal.Copy(buffer, chunk, 0, produced);
                    output.Write(chunk, 0, produced);
                }

                if (code == ZStreamEnd) break;
                if (code != ZOk) throw new InvalidOperationException($"deflate 失敗：{code}");
            }

            return output.ToArray();
        }
        finally
        {
            deflateEnd(ref stream);
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(buffer);
        }
    }
}
