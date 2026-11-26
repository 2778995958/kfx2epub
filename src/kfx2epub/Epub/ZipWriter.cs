using System.Buffers.Binary;
using System.Text;

namespace Kfx2Epub.Epub;

public readonly record struct ZipItem(string Name, byte[] Data, bool Stored);

// 逐位元對齊 Python zipfile 的 ZIP 寫入器：
// mimetype 以 STORED 寫入，其餘以 deflate（等級 6）寫入，
// header 欄位（版本、旗標、外部屬性、時間）與 CPython zipfile 相同。
public static class ZipWriter
{
    private const ushort VersionMadeBy = 20; // 0x0014：create_system = 0
    private const ushort VersionNeeded = 20;
    private const uint ExternalAttributes = 0x01800000; // 0o600 << 16
    private const ushort Utf8Flag = 0x0800;

    public static byte[] Write(IReadOnlyList<ZipItem> items, DateTime timestamp)
    {
        if (items.Count > ushort.MaxValue)
        {
            throw new InvalidOperationException("ZIP entry 數量超過 65535，需要 ZIP64。");
        }

        var (dosTime, dosDate) = EncodeDosTimestamp(timestamp);
        using var output = new MemoryStream();
        var records = new List<EntryRecord>(items.Count);

        foreach (var item in items)
        {
            var name = Encoding.UTF8.GetBytes(item.Name);
            var flags = IsAscii(name) ? (ushort)0 : Utf8Flag;
            var method = item.Stored ? (ushort)0 : (ushort)8;
            var payload = item.Stored ? item.Data : ZlibNative.Deflate(item.Data);
            var crc = Crc32.Compute(item.Data);
            var offset = checked((uint)output.Position);

            if (payload.Length > int.MaxValue || item.Data.Length > int.MaxValue)
            {
                throw new InvalidOperationException("單一 entry 過大，需要 ZIP64。");
            }

            WriteLocalHeader(output, name, flags, method, dosTime, dosDate, crc,
                payload.Length, item.Data.Length);
            output.Write(payload, 0, payload.Length);
            records.Add(new EntryRecord(name, flags, method, crc, (uint)payload.Length,
                (uint)item.Data.Length, offset));
        }

        var directoryOffset = checked((uint)output.Position);
        foreach (var record in records)
        {
            WriteCentralHeader(output, record, dosTime, dosDate);
        }

        var directorySize = checked((uint)output.Position - directoryOffset);
        WriteEndOfCentralDirectory(output, (ushort)records.Count, directorySize, directoryOffset);
        return output.ToArray();
    }

    private readonly record struct EntryRecord(byte[] Name, ushort Flags, ushort Method,
        uint Crc, uint CompressedSize, uint UncompressedSize, uint Offset);

    private static void WriteLocalHeader(Stream output, byte[] name, ushort flags, ushort method,
        ushort dosTime, ushort dosDate, uint crc, int compressedSize, int uncompressedSize)
    {
        Span<byte> header = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], 0x04034B50);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], method);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], dosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[14..], crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[18..], (uint)compressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[22..], (uint)uncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0);
        output.Write(header);
        output.Write(name, 0, name.Length);
    }

    private static void WriteCentralHeader(Stream output, EntryRecord record,
        ushort dosTime, ushort dosDate)
    {
        Span<byte> header = stackalloc byte[46];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], 0x02014B50);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], VersionMadeBy);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], record.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], record.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], dosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], dosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], record.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], record.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], record.UncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], (ushort)record.Name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header[30..], 0); // extra
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 0); // comment
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..], 0); // disk
        BinaryPrimitives.WriteUInt16LittleEndian(header[36..], 0); // internal attributes
        BinaryPrimitives.WriteUInt32LittleEndian(header[38..], ExternalAttributes);
        BinaryPrimitives.WriteUInt32LittleEndian(header[42..], record.Offset);
        output.Write(header);
        output.Write(record.Name, 0, record.Name.Length);
    }

    private static void WriteEndOfCentralDirectory(Stream output, ushort count,
        uint directorySize, uint directoryOffset)
    {
        Span<byte> header = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..], 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 0);  // disk
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);  // directory disk
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], count);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], count);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], directorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], directoryOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], 0); // comment
        output.Write(header);
    }

    // Python zipfile 的 DOS 時間沒有時區，直接使用來源時間的欄位。
    private static (ushort Time, ushort Date) EncodeDosTimestamp(DateTime value)
    {
        var time = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (time.Year < 1980)
        {
            time = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        }
        else if (time.Year > 2107)
        {
            time = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Unspecified);
        }

        var dosTime = (ushort)((time.Hour << 11) | (time.Minute << 5) | (time.Second / 2));
        var dosDate = (ushort)(((time.Year - 1980) << 9) | (time.Month << 5) | time.Day);
        return (dosTime, dosDate);
    }

    private static bool IsAscii(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if (value >= 0x80) return false;
        }

        return true;
    }

    // zlib.crc32 對應的標準 CRC-32（reflected, polynomial 0xEDB88320）。
    private static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var value in data)
            {
                crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
            }

            return ~crc;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (var i = 0; i < 256; i++)
            {
                var value = (uint)i;
                for (var bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
                }

                table[i] = value;
            }

            return table;
        }
    }
}
