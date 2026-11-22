using System.IO.Compression;

namespace Kfx2Epub.Core;

public sealed class DataFile : IComparable<DataFile>
{
    public string Name { get; }
    public string RelativeName { get; }
    public string Ext { get; }
    public DataFile Parent { get; }
    public bool IsRealFile { get; }
    private byte[] _data;
    private bool _loaded;

    public DataFile(string name, byte[] data = null, DataFile parent = null)
    {
        RelativeName = name;
        Name = name;
        Ext = Path.GetExtension(name).ToLowerInvariant();
        Parent = parent;
        _data = data;
        IsRealFile = data is null && parent is null;
        _loaded = data is not null;
    }

    public byte[] GetData()
    {
        if (!_loaded)
        {
            _data = File.ReadAllBytes(Name);
            _loaded = true;
        }

        return _data ?? Array.Empty<byte>();
    }

    public bool IsZipFile => Ext is ".azk" or ".kfx-zip" or ".kpf" or ".zip" ||
                             GetData().AsSpan().StartsWith(new byte[] { 0x50, 0x4b, 0x03, 0x04 });

    public ZipArchive AsZipFile()
    {
        if (IsRealFile)
        {
            return ZipFile.OpenRead(Name);
        }

        return new ZipArchive(new MemoryStream(GetData()), ZipArchiveMode.Read);
    }

    public DataFile RelativeDataFile(string relName)
    {
        if (IsRealFile)
        {
            var directory = Path.GetDirectoryName(Name);
            if (!string.IsNullOrEmpty(directory))
            {
                relName = Path.Combine(directory, relName);
            }

            return new DataFile(relName);
        }

        if (Parent is not null)
        {
            relName = relName.Replace('\\', '/');
            var directory = Path.GetDirectoryName(RelativeName)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory))
            {
                relName = directory + "/" + relName;
            }

            using var archive = Parent.AsZipFile();
            var entry = archive.GetEntry(relName) ?? throw new FileNotFoundException(relName);
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return new DataFile(relName, memory.ToArray(), Parent);
        }

        throw new InvalidOperationException($"無法相對於未知父容器取得檔案：{relName}");
    }

    public int CompareTo(DataFile other) => string.CompareOrdinal(Name, other?.Name);

    public override string ToString() => Name;
}
