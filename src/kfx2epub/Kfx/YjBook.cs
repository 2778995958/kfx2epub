using System.IO.Compression;
using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Kfx;

public sealed class YjBook
{
    public DataFile DataFile { get; }
    public IonSymbolTable Symtab { get; }
    // KFX 沒有官方修改時間；採用 zip 內所有檔案最晚的 DOS 時間（naive，無時區）。
    public DateTime SourceModifiedTime { get; }
    public YjFragmentList Fragments { get; } = new();
    public List<YjContainer> YjContainers { get; } = new();
    public List<DataFile> ContainerDatafiles { get; private set; } = new();

    public bool IsKpfPrepub { get; set; }
    public bool IsDictionary { get; set; }
    public bool IsScribeNotebook { get; set; }
    public bool IsMagazine { get; set; }
    public bool IsPrintReplica { get; set; }
    public bool IsSample { get; set; }
    public bool IsKfxV1 { get; set; }
    public bool IsIllustratedLayout { get; set; }
    public bool IsPdfBackedFixedLayout { get; set; }

    public YjBook(string file)
    {
        DataFile = new DataFile(file);
        Symtab = new IonSymbolTable("YJ_symbols");
        SourceModifiedTime = GetSourceModifiedTime(DataFile);
    }

    private static DateTime GetSourceModifiedTime(DataFile dataFile)
    {
        try
        {
            if (!dataFile.IsZipFile) return DateTime.UtcNow;

            using var archive = dataFile.AsZipFile();
            DateTime? latest = null;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;

                var time = entry.LastWriteTime.DateTime;
                if (time.Year < 1980) continue;

                if (latest is null || time > latest) latest = time;
            }

            return latest ?? DateTime.UtcNow;
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    public void DecodeBook(bool retainYjLocals = false)
    {
        if (Fragments.Count > 0) return;

        LocateBookDatafiles();

        foreach (var datafile in ContainerDatafiles)
        {
            Log.Info($"Processing container: {datafile.Name}");
            var container = GetContainer(datafile);
            if (container is null) continue;
            container.Deserialize();
            YjContainers.Add(container);
        }

        foreach (var container in YjContainers)
        {
            foreach (var fragment in container.GetFragments())
            {
                Fragments.Add(fragment);
            }
        }
    }

    public void FinalActions()
    {
    }

    private void LocateBookDatafiles()
    {
        ContainerDatafiles = new List<DataFile>();

        if (DataFile.IsRealFile && Directory.Exists(DataFile.Name))
        {
            LocateFilesFromDir(DataFile.Name);
        }
        else if (DataFile.Ext is ".azw8" or ".ion" or ".kfx" or ".kpf")
        {
            ContainerDatafiles.Add(DataFile);

            if (DataFile.Ext == ".kfx" && DataFile.IsRealFile)
            {
                var sdr = Path.ChangeExtension(DataFile.Name, ".sdr");
                if (Directory.Exists(sdr)) LocateFilesFromDir(sdr);
            }
        }
        else if (DataFile.Ext is ".kfx-zip" or ".zip")
        {
            using var archive = DataFile.AsZipFile();
            if (archive.Entries.Any(e => Path.GetFileName(e.FullName).ToLowerInvariant() is "book.ion" or "book.kdf"))
            {
                ContainerDatafiles.Add(DataFile);
            }
            else
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/')) continue;
                    using var stream = entry.Open();
                    using var memory = new MemoryStream();
                    stream.CopyTo(memory);
                    CheckLocatedFile(entry.FullName, memory.ToArray(), DataFile);
                }
            }
        }
        else
        {
            throw new InvalidOperationException($"未知的主要檔案類型 {DataFile.Ext}。必須是 azw8、ion、kfx、kfx-zip、kpf 或 zip。");
        }

        if (ContainerDatafiles.Count == 0)
        {
            throw new InvalidOperationException("找不到 KFX 容器，這本書不是 KFX 格式。");
        }

        ContainerDatafiles.Sort();
    }

    private void LocateFilesFromDir(string directory)
    {
        foreach (var filename in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            CheckLocatedFile(filename);
        }
    }

    private void CheckLocatedFile(string name, byte[] data = null, DataFile parent = null)
    {
        var basename = Path.GetFileName(name.Replace('\\', '/')).ToLowerInvariant();
        var ext = Path.GetExtension(basename);

        if (basename.StartsWith("._") || basename == "bookmanifest.kfx") return;

        if (ext is ".azw" or ".azw8" or ".azw9" or ".kfx" or ".md" or ".res" or ".yj" || basename == "nbk")
        {
            ContainerDatafiles.Add(new DataFile(name, data, parent));
        }
        else if (basename == "nbk-journal")
        {
            // Scribe notebook journal；目前不支援，略過。
        }
    }

    private YjContainer GetContainer(DataFile datafile)
    {
        var data = datafile.GetData();

        if (data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(KfxContainer.Signature))
        {
            return new KfxContainer(Symtab, datafile);
        }

        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(KfxContainer.DrmSignature))
        {
            throw new InvalidOperationException($"書籍容器 {datafile.Name} 有 DRM，無法轉換");
        }

        if (data.Length >= 0x44 && (data.AsSpan(0x3c, 8).SequenceEqual("BOOKMOBI"u8) ||
                                    data.AsSpan(0x3c, 8).SequenceEqual("RBINCONT"u8)))
        {
            throw new InvalidOperationException($"檔案格式是 MOBI（不是 KFX）：{datafile.Name}");
        }

        throw new InvalidOperationException($"無法判斷 KFX 容器類型：{datafile.Name}（{IonUtil.BytesToSeparatedHex(data.AsSpan(0, Math.Min(8, data.Length)))}）");
    }

    public bool HasIllustratedLayoutConditionalPageTemplate()
        => GetFeatureValue("yj.conditional_structure") is not null ||
           (Convert.ToInt32(GetFeatureValue("reflow-style", defaultValue: 0)) == 5 && !IsMagazine) ||
           HasIllustratedLayoutPageTemplateCondition();

    public bool HasIllustratedLayoutPageTemplateCondition()
    {
        foreach (var section in Fragments.GetAll("$260"))
        {
            if (section.Value is not IonStruct data) continue;
            if (data.GetValueOrDefault("$141") is not List<object> templates) continue;
            if (templates.OfType<IonStruct>().Any(t => t.ContainsKey("$171"))) return true;
        }

        return false;
    }

    public object GetFeatureValue(string feature, string ns = "com.amazon.yjconversion", object defaultValue = null)
    {
        if (ns == "format_capabilities")
        {
            var fragment = Fragments.Get("$593", first: true);
            if (fragment?.Value is List<object> capabilities)
            {
                foreach (var fc in capabilities.OfType<IonStruct>())
                {
                    if (fc.GetValueOrDefault("$492").ToStr() == feature) return fc.GetValueOrDefault("version", "");
                }
            }
        }
        else
        {
            var fragment = Fragments.Get("$585", first: true);
            if (fragment?.Value is IonStruct contentFeatures)
            {
                foreach (var cf in contentFeatures.GetValueOrDefault("$590") as List<object> ?? new List<object>())
                {
                    if (cf is not IonStruct fs || fs.GetValueOrDefault("$586").ToStr() != ns ||
                        fs.GetValueOrDefault("$492").ToStr() != feature) continue;
                    var vi = fs.GetValueOrDefault("$589") as IonStruct;
                    var version = vi?.GetValueOrDefault("version") as IonStruct;
                    var major = Convert.ToInt32(version?.GetValueOrDefault("$587", 0) ?? 0);
                    var minor = Convert.ToInt32(version?.GetValueOrDefault("$588", 0) ?? 0);
                    return minor == 0 ? major : (major, minor);
                }
            }
        }

        return defaultValue;
    }

    public string ClassifySymbol(string name)
    {
        if (Symtab.IsSharedSymbol(name)) return SymTypes.Shared;

        if (name is "$APPROXIMATE_PAGE_LIST" or "crop_bleed" or "dictionary_rules" or "mkfx_id" or
            "page_list_entry" or "srl_created_by_stampler" or "yj.dictionary.text" or "note_template_collection" or
            "cx" or "cy" or "end_x" or "end_y" or "ellipse" or "line" or "polygon" or "polyline" or
            "radius_x" or "radius_y" or "rectangle" or "shape_dimensions" or "start_x" or "start_y" or
            "vertex_list" or "x" or "y" ||
            Regex.IsMatch(name, @"^content_[0-9]+$") ||
            Regex.IsMatch(name, @"^eidbucket_[0-9]+$") ||
            Regex.IsMatch(name, @"^PAGE_LIST_[0-9]{10,}$") ||
            Regex.IsMatch(name, @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$") ||
            Regex.IsMatch(name, @"^yj\.(authoring|conversion|print|semantics)\.") ||
            Regex.IsMatch(name, @"^nmdl\.") ||
            name.StartsWith("kfx_cover_image"))
        {
            return SymTypes.Common;
        }

        if (Regex.IsMatch(name, @"^G[0-9]+(-spm)?$") || name.StartsWith("yj.dictionary.")) return SymTypes.Dictionary;

        if (Regex.IsMatch(name, @"^V_[0-9]_[0-9](-PARA|-CHAR)?-[0-9]_[0-9]_[0-9a-f]{12,16}_[0-9a-f]{1,5}") ||
            Regex.IsMatch(name, @"^(fonts/|images/|resource/)?(res|resource)_[0-9]_[0-9]_[0-9a-f]{12,16}_[0-9a-f]{1,5}_") ||
            Regex.IsMatch(name, @"^(anchor-|section-|story-|style-|navContainer|navUnit)[0-9]_[0-9]_[0-9a-f]{12,16}_[0-9a-f]{1,5}") ||
            Regex.IsMatch(name, @"^anchor-[a-z0-9_-]+-[0-9]{17,19}-[0-9]{1,2}$") ||
            Regex.IsMatch(name, @"^anchor-[a-z0-9_-]+_[0-9]_[0-9a-f]{12,16}_[0-9a-f]{1,5}$") ||
            Regex.IsMatch(name, @"^(LANDMARKS_|TOC_)[0-9]{10,}$") ||
            Regex.IsMatch(name, @"^(LazyLoadStoryLineForPage-|TargetSectionForPage-|TargetStoryLineForPage-)[0-9]+$") ||
            Regex.IsMatch(name, @"^slice_[0-9]+\.pdf$") ||
            Regex.IsMatch(name, @"^Target_pg_[0-9]+_g_2$") ||
            Regex.IsMatch(name, @"^KFXConditionalNavGroupUnit_[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$") ||
            name is "order-1" or "TargetReadingOrder" or "PageLabels" or "toc_entry")
        {
            return SymTypes.Original;
        }

        if (Regex.IsMatch(name,
                @"^(resource/|[ctliz])?[A-Za-z0-9_-]{22}[A-Z0-9]{0,6}" +
                @"((-hd|-first-frame|-thumb)?(-resized-[0-9]+-[0-9]+|-hd-tile-[0-9]+-[0-9]+)?|" +
                @"-ad|-spm|_thumbnail|-transcoded|(_thumb)?\.jpg|\.ttf|\.otf|\.woff|\.woff2|\.eot|\.dfont|\.bin)?$"))
        {
            return SymTypes.Base64;
        }

        if (Regex.IsMatch(name,
                @"^(resource/rsrc|resource/e|rsrc|[a-z])[A-Z0-9]{1,6}" +
                @"((-hd|-first-frame|-thumb)?(-resized-[0-9]+-[0-9]+|-hd-tile-[0-9]+-[0-9]+)?|" +
                @"-ad|-spm|_thumbnail|-transcoded|(_thumb)?\.jpg|\.ttf|\.otf|\.woff|\.woff2|\.eot|\.dfont|\.bin)?$"))
        {
            return SymTypes.Short;
        }

        return SymTypes.Unknown;
    }
}

public static class SymTypes
{
    public const string Common = "common";
    public const string Dictionary = "dictionary";
    public const string Original = "original";
    public const string Base64 = "base64";
    public const string Short = "short";
    public const string Shared = "shared";
    public const string Unknown = "unknown";
}
