using Kfx2Epub.Core;

namespace Kfx2Epub.Kfx;

public sealed class KfxContainer : YjContainer
{
    public static readonly byte[] Signature = "CONT"u8.ToArray();
    public static readonly byte[] DrmSignature = { 0xea, (byte)'D', (byte)'R', (byte)'M', (byte)'I', (byte)'O', (byte)'N', 0xee };
    public const int Version = 2;
    public const int MinLength = 18;
    public const int DefaultChunkSize = 4096;

    private static readonly HashSet<int> MainContainerFragmentIdnums = new() { 259, 260, 538 };
    private static readonly HashSet<int> MetadataContainerFragmentIdnums = new() { 258, 419, 490, 585 };
    private static readonly HashSet<int> AttachableContainerFragmentIdnums = new() { 417 };

    public List<KfxContainerEntity> Entities { get; } = new();
    public IonAnnotation DocSymbols { get; private set; }
    public IonAnnotation FormatCapabilities { get; private set; }
    public IonAnnotation ContainerInfo { get; private set; }
    public string ContainerId { get; private set; }

    public KfxContainer(IonSymbolTable symtab, DataFile dataFile = null, YjFragmentList fragments = null)
        : base(symtab, dataFile, fragments)
    {
    }

    public override void Deserialize()
    {
        DocSymbols = null;
        FormatCapabilities = null;
        ContainerInfo = null;
        Entities.Clear();
        Fragments.Clear();

        var data = DataFile.GetData();
        if (data.Length < MinLength)
        {
            throw new KfxCorruptException($"KFX 容器太短（{data.Length} bytes）");
        }

        var header = new Deserializer(data);
        var signature = header.Extract(4);
        var version = header.UnpackUInt16();
        var headerLen = header.UnpackUInt32();

        if (!signature.AsSpan().SequenceEqual(Signature))
        {
            throw new KfxCorruptException($"KFX 容器 signature 不正確（{IonUtil.BytesToSeparatedHex(signature)}）");
        }

        if (version is not (1 or 2))
        {
            Log.Warning($"KFX 容器版本非預期：{version}");
        }

        if (headerLen < MinLength)
        {
            throw new KfxCorruptException($"KFX 容器 header 太短（{headerLen} bytes）");
        }

        var containerInfoOffset = header.UnpackUInt32();
        var containerInfoLength = header.UnpackUInt32();
        var infoData = data.AsSpan((int)containerInfoOffset, (int)containerInfoLength).ToArray();
        var containerInfoValue = new IonBinary(Symtab).DeserializeSingleValue(infoData);
        if (containerInfoValue is not IonStruct containerInfo)
        {
            throw new KfxCorruptException("KFX container info 不是 IonStruct");
        }

        ContainerId = containerInfo.Pop("$409", "").ToStr() ?? "";
        containerInfo.Pop("$410", 0);
        containerInfo.Pop("$411", 0);

        var docSymbolOffset = containerInfo.TryGetValue("$415", out var dso) ? Convert.ToInt32(dso) : 0;
        var docSymbolLength = containerInfo.TryGetValue("$416", out var dsl) ? Convert.ToInt32(dsl) : 0;
        if (docSymbolLength > 0)
        {
            var docData = data.AsSpan(docSymbolOffset, docSymbolLength).ToArray();
            DocSymbols = new IonBinary(Symtab).DeserializeAnnotatedValue(docData, "$ion_symbol_table");

            if (DocSymbols.Value is IonStruct symbolTable && symbolTable.TryGetValue("imports", out var importsObj) &&
                importsObj is IEnumerable<object> imports)
            {
                foreach (var importObj in imports.OfType<IonStruct>())
                {
                    if (importObj.TryGetValue("max_id", out var maxIdObj))
                    {
                        importObj["max_id"] = Convert.ToInt32(maxIdObj) - YjSymbolCatalog.SystemSymbolTable.Symbols.Count;
                    }
                }
            }

            Symtab.Create((IonStruct)DocSymbols.Value);
        }

        containerInfo.Pop("$412", DefaultChunkSize);

        if (version > 1)
        {
            var formatOffset = containerInfo.TryGetValue("$594", out var fo) ? Convert.ToInt32(fo) : 0;
            var formatLength = containerInfo.TryGetValue("$595", out var fl) ? Convert.ToInt32(fl) : 0;
            if (formatLength > 0)
            {
                var formatData = data.AsSpan(formatOffset, formatLength).ToArray();
                FormatCapabilities = new IonBinary(Symtab).DeserializeAnnotatedValue(formatData, "$593");
            }
        }

        var typeIdnums = new HashSet<int>();
        var indexTableOffset = containerInfo.TryGetValue("$413", out var ito) ? Convert.ToInt32(ito) : 0;
        var indexTableLength = containerInfo.TryGetValue("$414", out var itl) ? Convert.ToInt32(itl) : 0;

        if (indexTableLength > 0)
        {
            var entityTable = new Deserializer(data.AsSpan(indexTableOffset, indexTableLength).ToArray());
            while (entityTable.Remaining > 0)
            {
                var idIdnum = (int)entityTable.UnpackUInt32();
                var typeIdnum = (int)entityTable.UnpackUInt32();
                var entityOffset = entityTable.UnpackUInt64();
                var entityLen = entityTable.UnpackUInt64();
                typeIdnums.Add(typeIdnum);

                var entityStart = (long)headerLen + (long)entityOffset;
                if (entityStart + (long)entityLen > data.Length)
                {
                    throw new KfxCorruptException($"KFX 容器資料不足（需要 offset {entityStart + (long)entityLen}）");
                }

                Entities.Add(new KfxContainerEntity(Symtab, idIdnum, typeIdnum,
                    serializedData: data.AsSpan((int)entityStart, (int)entityLen).ToArray()));
            }
        }

        var containerFormat = "KFX unknown";
        if (typeIdnums.Overlaps(MainContainerFragmentIdnums)) containerFormat = "KFX main";
        else if (typeIdnums.Overlaps(MetadataContainerFragmentIdnums) || docSymbolLength > 0) containerFormat = "KFX metadata";
        else if (typeIdnums.Overlaps(AttachableContainerFragmentIdnums)) containerFormat = "KFX attachable";

        var normalizedInfo = new IonStruct
        {
            ["$409"] = ContainerId,
            ["$412"] = DefaultChunkSize,
            ["$410"] = 0,
            ["$411"] = 0,
            ["$161"] = containerFormat,
            ["version"] = (int)version,
            ["$181"] = Entities.Select(e => new List<object> { e.TypeIdnum, e.IdIdnum }).Cast<object>().ToList()
        };
        ContainerInfo = new IonAnnotation(new[] { new IonSymbol("$270") }, normalizedInfo);
    }

    public override YjFragmentList GetFragments()
    {
        if (Fragments.Count == 0)
        {
            if (DocSymbols is not null) Fragments.Add(FragmentFromAnnotation(DocSymbols));
            if (ContainerInfo is not null) Fragments.Add(FragmentFromAnnotation(ContainerInfo));
            if (FormatCapabilities is not null) Fragments.Add(FragmentFromAnnotation(FormatCapabilities));

            foreach (var entity in Entities)
            {
                Fragments.Add(entity.Deserialize());
            }
        }

        return Fragments;
    }

    private static YjFragment FragmentFromAnnotation(IonAnnotation annotation)
    {
        var annotations = annotation.Annotations.Select(a => a.Value).ToList();
        var ftype = annotations[^1];
        var fid = annotations.Count > 1 ? annotations[0] : ftype;
        return new YjFragment(ftype, fid, annotation.Value);
    }
}

public sealed class KfxContainerEntity
{
    public static readonly byte[] Signature = "ENTY"u8.ToArray();
    public const int Version = 1;
    public const int MinLength = 10;

    private readonly IonSymbolTable _symtab;
    public int IdIdnum { get; }
    public int TypeIdnum { get; }
    public object Value { get; set; }
    public byte[] SerializedData { get; }

    public KfxContainerEntity(IonSymbolTable symtab, int idIdnum = 0, int typeIdnum = 0,
        object value = null, byte[] serializedData = null)
    {
        _symtab = symtab;
        IdIdnum = idIdnum;
        TypeIdnum = typeIdnum;
        Value = value;
        SerializedData = serializedData;
    }

    public YjFragment Deserialize()
    {
        var data = SerializedData ?? throw new KfxCorruptException("KFX entity 沒有序列化資料");
        var entity = new Deserializer(data);
        var signature = entity.Extract(4);
        var version = entity.UnpackUInt16();
        var headerLen = entity.UnpackUInt32();

        if (!signature.AsSpan().SequenceEqual(Signature))
        {
            throw new KfxCorruptException($"KFX entity signature 不正確（{IonUtil.BytesToSeparatedHex(signature)}）");
        }

        if (version != Version)
        {
            Log.Warning($"KFX entity 版本非預期：{version}");
        }

        if (headerLen < MinLength)
        {
            throw new KfxCorruptException("KFX entity header 太短");
        }

        var infoData = entity.Extract(upto: (int)headerLen);
        var entityInfo = (IonStruct)new IonBinary(_symtab).DeserializeSingleValue(infoData);
        entityInfo.Pop("$410", 0);
        entityInfo.Pop("$411", 0);

        var entityData = entity.Extract();
        var fid = _symtab.GetSymbol(IdIdnum).Value;
        var ftype = _symtab.GetSymbol(TypeIdnum).Value;

        object value;
        if (FragmentTypes.RawFragmentTypes.Contains(ftype))
        {
            value = new IonBLOB(entityData);
        }
        else
        {
            value = new IonBinary(_symtab).DeserializeSingleValue(entityData);
        }

        if (value is IonAnnotation annotation)
        {
            if (annotation.IsAnnotation(ftype) && fid == "$348")
            {
                fid = ftype;
                value = annotation.Value;
            }
            else
            {
                Log.Error($"Entity 值帶有非預期 annotation：{annotation}");
            }
        }

        return new YjFragment(ftype, fid == "$348" ? null : fid, value);
    }

    public override string ToString() => $"${TypeIdnum}/${IdIdnum}";
}
