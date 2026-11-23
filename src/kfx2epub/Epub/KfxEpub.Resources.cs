using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

public sealed class ResourceObj
{
    public byte[] RawMedia { get; set; }
    public string Filename { get; set; }
    public string Extension { get; set; }
    public string Format { get; set; }
    public string Mime { get; set; }
    public string Location { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public List<string> ReferredResources { get; set; } = new();
    public ManifestEntry ManifestEntry { get; set; }
}

public partial class KfxEpub
{
    public Dictionary<string, ResourceObj> ResourceCache { get; } = new();
    public HashSet<string> UsedRawMedia { get; } = new();
    public bool SaveResources = true;
    public Dictionary<(string Location, string Suffix), string> LocationFilenames { get; } = new();
    public int IllustrationImageIndex = 1;
    public int PageImageIndex = 1;
    public int IllustrationImageSequenceWidth = 3;
    public int PageImageSequenceWidth = 3;

    public ResourceObj GetExternalResource(string resourceName, bool ignoreVariants = false)
    {
        if (ResourceCache.TryGetValue(resourceName, out var cached)) return cached;

        var resource = (IonStruct)GetFragment("$164", resourceName);
        var intResourceName = resource.Pop("$175", "").ToStr() ?? "";
        if (intResourceName != resourceName) Log.Error($"資源 {resourceName} 的內部名稱不正確：{intResourceName}");

        var resourceFormat = resource.Pop("$161").ToStr();
        // $66/$67 也是圖片尺寸的來源，必須先保留再判斷，否則會被第一個 Pop 消耗掉。
        // $422/$423 可能是 IonInt 或 quantity struct，需用 PixelValue 轉換。
        var fixedHeight = PixelValue(resource.Pop("$67"), adjust: false) ?? 0;
        var fixedWidth = PixelValue(resource.Pop("$66"), adjust: false) ?? 0;
        var resourceHeight = PixelValue(resource.Pop("$423"), adjust: false) ??
                             (fixedHeight > 0 ? fixedHeight : null);
        var resourceWidth = PixelValue(resource.Pop("$422"), adjust: false) ??
                            (fixedWidth > 0 ? fixedWidth : null);

        string location;
        byte[] rawMedia;
        if (resource.ContainsKey("$636"))
        {
            var yjTiles = (List<object>)resource.Pop("$636");
            var tileHeight = Convert.ToInt32(resource.Pop("$638"));
            var tileWidth = Convert.ToInt32(resource.Pop("$637"));
            var tilePadding = Convert.ToInt32(resource.Pop("$797", 0));
            var tileRows = yjTiles.Select(row => ((List<object>)row).Select(x => x.ToStr() ?? "").ToList()).ToList();
            location = tileRows[0][0].Split("-tile")[0];

            var tilesRawMedia = new List<byte[]>();
            foreach (var row in tileRows)
            {
                foreach (var tileLocation in row) tilesRawMedia.Add(LocateRawMedia(tileLocation, resourceFormat));
            }

            (rawMedia, resourceFormat) = ImageUtil.CombineTiles(resourceName, resourceHeight ?? 0, resourceWidth ?? 0,
                resourceFormat, tileHeight, tileWidth, tilePadding, tileRows, tilesRawMedia, ignoreVariants);
        }
        else
        {
            location = resource.Pop("$165").ToStr();
            var searchPath = resource.Pop("$166", location).ToStr();
            if (searchPath != location) Log.Error($"圖片資源 {resourceName} 的 location 與 search_path 不同");
            rawMedia = LocateRawMedia(location, resourceFormat);
        }

        if (ignoreVariants && rawMedia is null) return null;

        var extension = resourceFormat is not null && ImageUtil.SymbolFormats.TryGetValue(resourceFormat, out var ext)
            ? "." + ext
            : ".bin";
        var mime = resource.Pop("$162").ToStr();
        if (mime is not null && ImageUtil.ExtsOfMimetype.TryGetValue(mime, out var exts))
        {
            if (extension is ".pobject" or ".bin")
            {
                extension = mime == "figure" ? ImageUtil.ImageFileExt(rawMedia) : exts[0];
            }
        }

        var locationFn = location ?? "";
        locationFn = resource.Pop("yj.conversion.source_resource_filename", locationFn).ToStr() ?? locationFn;
        locationFn = resource.Pop("yj.authoring.source_file_name", locationFn).ToStr() ?? locationFn;
        if (extension is ".pobject" or ".bin" && locationFn.Contains('.'))
        {
            extension = "." + locationFn[(locationFn.LastIndexOf('.') + 1)..];
        }

        if (!locationFn.EndsWith(extension))
        {
            locationFn = locationFn.Split('.')[0] + extension;
        }

        resource.Pop("$597");
        resource.Pop("$70");
        resource.Pop("$57");
        resource.Pop("$56");
        resource.Pop("$499");
        resource.Pop("$500");
        resource.Pop("$137");
        resource.Pop("$136");
        var referred = resource.Pop("$167", new List<object>());
        var referredResources = referred is List<object> list
            ? list.Select(item => item.ToStr()).Where(item => item is not null).ToList()
            : new List<string>();

        if (resource.ContainsKey("$214"))
        {
            var thumbnailResource = resource.Pop("$214").ToStr();
            if (thumbnailResource is not null)
            {
                ProcessExternalResource(thumbnailResource, save: false);
            }
        }

        if (resourceFormat == "$548" && rawMedia is not null)
        {
            (rawMedia, resourceFormat) = ImageUtil.ConvertJxrToJpegOrPng(rawMedia, locationFn);
            extension = "." + ImageUtil.SymbolFormats[resourceFormat];
            locationFn = locationFn[..locationFn.LastIndexOf('.')] + extension;
        }

        var suffix = "";
        if (resourceFormat == "$565" && rawMedia is not null)
        {
            // PDF 轉圖功能未移植；保留原始 PDF 資源。
            suffix = resource.ContainsKey("$564") ? $"-page{Convert.ToInt32(resource.Pop("$564")) + 1}" : "";
        }

        var filename = ResourceLocationFilename(locationFn, suffix, ImageFilepath);

        if (!ignoreVariants && resource.Pop("$635") is List<object> variants)
        {
            foreach (var variantName in variants.Select(item => item.ToStr()).Where(item => item is not null))
            {
                var variant = GetExternalResource(variantName, ignoreVariants: true);
                if (variant is not null && variant.Width > resourceWidth && variant.Height > resourceHeight)
                {
                    rawMedia = variant.RawMedia;
                    filename = variant.Filename;
                    resourceWidth = variant.Width;
                    resourceHeight = variant.Height;
                }
            }
        }

        if (resource.ContainsKey("$564"))
        {
            filename += "#page=" + (Convert.ToInt32(resource.Pop("$564")) + 1);
        }

        CheckEmpty(resource, $"resource {resourceName}");
        var result = new ResourceObj
        {
            RawMedia = rawMedia,
            Filename = filename,
            Extension = extension,
            Format = resourceFormat,
            Mime = mime,
            Location = location,
            Width = resourceWidth ?? fixedWidth,
            Height = resourceHeight ?? fixedHeight,
            ReferredResources = referredResources
        };
        ResourceCache[resourceName] = result;
        return result;
    }

    public ResourceObj ProcessExternalResource(string resourceName, bool save = true, bool processReferred = false,
        bool? saveReferred = false, bool? isPlugin = false, bool isReferred = false, string imageRole = null)
    {
        var resource = GetExternalResource(resourceName);
        if (save && SaveResources && resource.RawMedia is not null)
        {
            if (resource.ManifestEntry is null)
            {
                var filename = isReferred ? PathUtil.RootPath(resource.Location ?? "") : resource.Filename;
                var fragment = "";
                var hash = filename.IndexOf('#');
                if (hash >= 0)
                {
                    fragment = filename[hash..];
                    filename = filename[..hash];
                }

                if (!isReferred && imageRole is "cover" or "illustration" or "page")
                {
                    filename = NumberedImageFilename(imageRole, Path.GetExtension(filename));
                }

                var baseFilename = filename;
                var count = 0;
                while (OebpsFiles.ContainsKey(filename))
                {
                    if (OebpsFiles[filename].BinaryData.AsSpan().SequenceEqual(resource.RawMedia))
                    {
                        if (!ManifestFiles.TryGetValue(filename, out var existing))
                        {
                            throw new InvalidOperationException($"被引用的檔案不在 manifest：{filename}");
                        }

                        resource.ManifestEntry = existing;
                        ReferenceResource(existing);
                        break;
                    }

                    if (isReferred && count == 0) Log.Error($"資源 {resource.Location} 有多個被引用檔案");
                    var baseName = Path.GetFileNameWithoutExtension(baseFilename);
                    filename = $"{baseName}_{count++}{Path.GetExtension(baseFilename)}";
                }

                if (resource.ManifestEntry is null)
                {
                    resource.ManifestEntry = ManifestResource(filename, data: resource.RawMedia,
                        height: resource.Height, width: resource.Width,
                        mimetype: isReferred ? resource.Mime : null);
                }

                resource.Filename = filename + fragment;
            }
            else
            {
                ReferenceResource(resource.ManifestEntry);
            }
        }

        if (processReferred || saveReferred == true)
        {
            foreach (var referred in resource.ReferredResources)
            {
                ProcessExternalResource(referred, save: saveReferred == true, isReferred: true);
            }
        }

        return resource;
    }

    public string NumberedImageFilename(string imageRole, string extension)
    {
        if (imageRole == "cover") return ImageFilepath.Replace("%s", "cover" + extension);

        string prefix;
        int index;
        int width;
        if (imageRole == "illustration")
        {
            prefix = "i";
            index = IllustrationImageIndex;
            width = IllustrationImageSequenceWidth;
        }
        else
        {
            prefix = "p";
            index = PageImageIndex;
            width = PageImageSequenceWidth;
        }

        while (true)
        {
            if (imageRole == "illustration") IllustrationImageIndex++;
            else PageImageIndex++;

            var filename = ImageFilepath.Replace("%s",
                $"{prefix}-{TextUtil.SequenceNumber(index, minimumDigits: width)}{extension}");
            if (!OebpsFiles.ContainsKey(filename) && !ManifestFiles.ContainsKey(filename)) return filename;
        }
    }

    public byte[] LocateRawMedia(string location, string format = null, bool reportMissing = true)
    {
        if (location is null) return null;
        if (BookData.TryGetValue("$417", out var raw) && raw is FragmentMap map && map.TryGetValue(location, out var value))
        {
            UsedRawMedia.Add(location);
            return value is IonBLOB blob ? blob.Data : null;
        }

        if (reportMissing) Log.Error($"缺少 {format} bcRawMedia {location}");
        return null;
    }

    public string ResourceLocationFilename(string location, string suffix, string filepathTemplate, bool isSymbol = true)
    {
        location ??= "";
        var key = (location, suffix);
        if (LocationFilenames.TryGetValue(key, out var cached)) return cached;

        if (location.StartsWith('/')) location = "_" + location[1..];
        var safe = Regex.Replace(location, @"[^A-Za-z0-9_/.-]", "_").Replace("//", "/x/");
        var slash = safe.LastIndexOf('/');
        var path = slash >= 0 ? safe[..(slash + 1)] : "";
        var name = slash >= 0 ? safe[(slash + 1)..] : safe;
        var dot = name.LastIndexOf('.');
        var root = dot >= 0 ? name[..dot] : name;
        var ext = dot >= 0 ? name[dot..] : "";
        var resourceType = ImageUtil.ResourceTypeOfExt.GetValueOrDefault(ext, "resource");

        if (isSymbol)
        {
            var uniquePart = UniquePartOfLocalSymbol(root);
            root = PrefixUniquePartOfSymbol(uniquePart, resourceType);
        }

        var prefixes = new[] { "resource/", filepathTemplate[1..].Split('/')[0] + "/" };
        foreach (var prefix in prefixes)
        {
            if (path.StartsWith(prefix)) path = path[prefix.Length..];
        }

        var filename = filepathTemplate.Replace("%s", path + root + suffix + ext);
        var uniqueCount = 0;
        while (OebpsFiles.Keys.Any(k => string.Equals(k, filename, StringComparison.OrdinalIgnoreCase)))
        {
            filename = filepathTemplate.Replace("%s", $"{path}{root}{suffix}-{uniqueCount++}{ext}");
        }

        LocationFilenames[key] = filename;
        return filename;
    }

    public void ProcessFonts()
    {
        if (!BookData.TryGetValue("$262", out var fontsValue) || fontsValue is not FragmentMap fonts) return;
        BookData.Remove("$262");
        var rawFonts = BookData.TryGetValue("$418", out var rf) && rf is FragmentMap rfm ? rfm : new FragmentMap();
        if (BookData.ContainsKey("$418")) BookData.Remove("$418");
        var rawMedia = BookData.TryGetValue("$417", out var rm) && rm is FragmentMap rmm ? rmm : new FragmentMap();
        var usedFonts = new Dictionary<string, string>();

        foreach (var fontValue in fonts.Values.OfType<IonStruct>())
        {
            var location = fontValue.Pop("$165").ToStr();
            if (location is null) continue;

            if (usedFonts.TryGetValue(location, out var usedFilename))
            {
                fontValue["src"] = CssUrl(PathUtil.UrlRelativePath(usedFilename, StylesCssFilepath));
            }
            else if (rawFonts.TryGetValue(location, out var rawFontValue) ||
                     (Book.IsKpfPrepub && rawMedia.TryGetValue(location, out rawFontValue)))
            {
                var raw = rawFontValue is IonBLOB blob ? blob.Data : null;
                var filename = location;
                if (!filename.Contains('.'))
                {
                    var ext = ImageUtil.FontFileExt(raw);
                    if (string.IsNullOrEmpty(ext)) ext = ".font";
                    filename += ext;
                }

                filename = ResourceLocationFilename(filename, "", FontFilepath);
                if (!OebpsFiles.ContainsKey(filename)) ManifestResource(filename, data: raw);
                fontValue["src"] = CssUrl(PathUtil.UrlRelativePath(filename, StylesCssFilepath));
                usedFonts[location] = filename;
            }

            foreach (var prop in new[] { "$15", "$12", "$13" })
            {
                if (fontValue.GetValueOrDefault(prop).ToStr() == "$350") fontValue.Pop(prop);
            }

            if (fontValue.TryGetValue("$11", out var fontName) && fontName is string name)
            {
                FixFontName(name, add: true);
                FontFaces.Add(ConvertYjProperties(fontValue));
            }
        }

        foreach (var pair in rawFonts)
        {
            Log.Warning($"未使用的字型檔：{pair.Key}");
            var filename = ResourceLocationFilename(pair.Key, "", FontFilepath);
            ManifestResource(filename, data: pair.Value is IonBLOB blob ? blob.Data : null);
        }
    }

    public string UriReference(string uri, bool save = true, bool? saveReferred = null, bool manifestExternalRefs = false)
    {
        if (uri.StartsWith("kfx:"))
        {
            var target = Uri.UnescapeDataString(uri[4..]);
            return ProcessExternalResource(target, save: save, saveReferred: saveReferred).Filename;
        }

        if (uri.StartsWith("navto:") || uri.StartsWith("navt:"))
        {
            var body = uri[(uri.IndexOf(':') + 1)..];
            var fragment = body.Contains('#') ? body[(body.IndexOf('#') + 1)..] : "";
            var name = Uri.UnescapeDataString(fragment.Length > 0 ? body[..body.IndexOf('#')] : body);
            var offset = fragment.Length > 0 ? double.Parse(fragment, System.Globalization.CultureInfo.InvariantCulture) : 0.0;
            if (NavtoAnchor.TryGetValue((name, offset), out var anchor)) return AnchorAsUri(anchor);
            return "/MISSING_NAVTO_" + name + (fragment.Length > 0 ? "_" + fragment : "");
        }

        if (uri.StartsWith("http://") || uri.StartsWith("https://"))
        {
            if (manifestExternalRefs) ManifestResource(uri, external: true, reportDupe: false);
            return uri;
        }

        return uri;
    }

    public string UniqueFileId(string filename)
    {
        if (FileIds.TryGetValue(filename, out var existing)) return existing;
        var id = Regex.Replace(filename.Split('/').Last()[..Math.Min(64, filename.Split('/').Last().Length)], @"[^A-Za-z0-9.-]", "_");
        if (!char.IsAsciiLetter(id[0])) id = "id_" + id;
        if (FileIds.Values.Contains(id))
        {
            var baseId = id;
            var count = 0;
            while (FileIds.Values.Contains(id)) id = $"{baseId}_{count++}";
        }

        FileIds[filename] = id;
        return id;
    }

    public void PrepareDomImageSequenceWidths()
    {
        var seen = new HashSet<string>();
        var roleCounts = new Dictionary<string, int> { ["illustration"] = 0, ["page"] = 0 };
        foreach (var bookPart in BookParts)
        {
            foreach (var elem in bookPart.Body().DescendantsAndSelf())
            {
                var attr = ImageReferenceAttr(elem);
                if (attr is null) continue;
                var filename = PathUtil.GetUrlFilename(PathUtil.UrlAbsolutePath(elem.Get(attr), bookPart.Filename));
                filename = PathUtil.RemoveUrlFragment(filename ?? "");
                if (string.IsNullOrEmpty(filename) || seen.Contains(filename) || IsCoverImageFilename(filename)) continue;
                seen.Add(filename);
                var role = DomImageRole(elem, filename);
                roleCounts[role] = roleCounts.GetValueOrDefault(role) + 1;
            }
        }

        IllustrationImageSequenceWidth = Math.Max(3, roleCounts["illustration"].ToString().Length);
        PageImageSequenceWidth = Math.Max(3, roleCounts["page"].ToString().Length);
    }

    public void PrepareDomImageFilenames(BookPart bookPart)
    {
        foreach (var elem in bookPart.Body().DescendantsAndSelf())
        {
            var attr = ImageReferenceAttr(elem);
            if (attr is null) continue;
            var filename = PathUtil.GetUrlFilename(PathUtil.UrlAbsolutePath(elem.Get(attr), bookPart.Filename));
            filename = PathUtil.RemoveUrlFragment(filename ?? "");
            if (string.IsNullOrEmpty(filename) || DomImageFilenames.Contains(filename) || IsCoverImageFilename(filename)) continue;
            var newFilename = RenameImageFile(filename, DomImageRole(elem, filename));
            if (newFilename is not null)
            {
                DomImageFilenames.Add(filename);
                DomImageFilenames.Add(newFilename);
            }
        }
    }

    public string ImageReferenceAttr(XNode elem)
    {
        if (elem.IsLocal("img") && elem.Has("src")) return "src";
        if (elem.IsNs(XmlNamespaces.Svg, "image") && elem.HasNs(XmlNamespaces.XLink, "href")) return XlinkHrefAttr;
        return null;
    }

    public string OriginalImageRole(XNode elem)
    {
        foreach (var parent in elem.Ancestors())
        {
            if (parent.IsLocal("body")) break;
            if (parent.IsLocal("div") && !parent.Get("class", "").Split(' ').Contains("main")) return "illustration";
        }

        return "page";
    }

    public string DomImageRole(XNode elem, string filename)
    {
        if (elem.IsLocal("img") && elem.Get("class", "").Split(' ').Contains("fit")) return "illustration";
        return OriginalDomImageRoles.GetValueOrDefault(filename) ?? OriginalImageRole(elem);
    }

    public bool IsCoverImageFilename(string filename)
        => ManifestFiles.TryGetValue(PathUtil.RemoveUrlFragment(filename), out var entry) && entry.IsCoverImage;

    public string RenameImageFile(string oldFilename, string imageRole, Dictionary<string, XNode> htmlByFilename = null)
    {
        oldFilename = PathUtil.RemoveUrlFragment(oldFilename);
        if (!OebpsFiles.ContainsKey(oldFilename)) return null;
        var ext = Path.GetExtension(oldFilename);
        if (ImageUtil.ResourceTypeOfExt.GetValueOrDefault(ext) != "image") return null;

        var newFilename = imageRole == "cover"
            ? ImageFilepath.Replace("%s", "cover" + ext)
            : NumberedImageFilename(imageRole, ext);
        if (oldFilename == newFilename) return newFilename;

        var outputFile = OebpsFiles[oldFilename];
        OebpsFiles.Remove(oldFilename);
        OebpsFiles[newFilename] = outputFile;

        if (ManifestFiles.TryGetValue(oldFilename, out var entry))
        {
            ManifestFiles.Remove(oldFilename);
            entry.Filename = newFilename;
            UpdateManifestEntryId(entry, newFilename);
            ManifestFiles[newFilename] = entry;
        }

        htmlByFilename ??= BookParts.ToDictionary(bp => bp.Filename, bp => bp.Html);
        foreach (var (filename, html) in htmlByFilename)
        {
            foreach (var elem in html.DescendantsAndSelf())
            {
                foreach (var attr in new[] { "src", XlinkHrefAttr })
                {
                    var value = elem.Get(attr);
                    if (value is not null) elem.Set(attr, ReplaceResourceUrl(value, filename, oldFilename, newFilename));
                }
            }
        }

        return newFilename;
    }

    public void UpdateManifestEntryId(ManifestEntry entry, string filename)
    {
        var oldId = entry.Id;
        ManifestIds.Remove(oldId);
        entry.Id = NameUtil.MakeUniqueName(ManifestIdForFilename(filename), ManifestIds, "_");
        ManifestIds.Add(entry.Id);
    }

    public string ReplaceResourceUrl(string url, string refFrom, string oldFilename, string newFilename)
    {
        var abs = PathUtil.UrlAbsolutePath(url, refFrom);
        var filename = PathUtil.GetUrlFilename(abs);
        if (PathUtil.RemoveUrlFragment(filename ?? "") != oldFilename) return url;
        var fragment = abs.Split('#').ElementAtOrDefault(1);
        return PathUtil.UrlRelativePath(newFilename + (fragment is not null ? "#" + fragment : ""), refFrom);
    }
}
