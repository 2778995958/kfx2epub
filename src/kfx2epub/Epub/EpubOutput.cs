using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

public class OpfProperties
{
    public HashSet<string> Properties { get; }

    public OpfProperties(IEnumerable<string> properties = null)
    {
        Properties = properties is null ? new HashSet<string>() : new HashSet<string>(properties);
    }

    public bool IsFxl
    {
        get => Properties.Contains("rendition:layout-pre-paginated");
        set
        {
            if (value) Properties.Add("rendition:layout-pre-paginated");
            else Properties.Remove("rendition:layout-pre-paginated");
        }
    }

    public bool IsNav
    {
        get => Properties.Contains("nav");
        set
        {
            if (value) Properties.Add("nav");
            else Properties.Remove("nav");
        }
    }

    public bool IsCoverImage
    {
        get => Properties.Contains("cover-image");
        set
        {
            if (value) Properties.Add("cover-image");
            else Properties.Remove("cover-image");
        }
    }
}

public sealed class BookPart : OpfProperties
{
    public string Filename { get; set; }
    public int PartIndex { get; }
    public XNode Html { get; }
    public bool? Linear { get; }
    public bool Omit { get; }
    public string Idref { get; }
    public bool IsCoverPage { get; set; }
    public string NmdlTemplateId { get; set; }
    public string WritingModeValue { get; set; }
    public bool HasVerticalRlClass { get; set; }

    // KFX 這一頁的唯一內容是一個區塊圖片容器（$270 + 區塊圖片版面），
    // 語意上就是「整頁一張圖」，輸出時改用 fixed-layout SVG 頁面。
    public bool IsPageImage { get; set; }

    public BookPart(string filename, int partIndex, XNode html, IEnumerable<string> opfProperties = null,
        bool? linear = null, bool omit = false, string idref = null) : base(opfProperties)
    {
        Filename = filename;
        PartIndex = partIndex;
        Html = html;
        Linear = linear;
        Omit = omit;
        Idref = idref;
    }

    public XNode Head()
    {
        var head = Html.Element("head");
        if (head is null)
        {
            head = new XNode("head");
            Html.Insert(0, head);
        }

        return head;
    }

    public XNode Body()
    {
        var body = Html.Element("body");
        if (body is null)
        {
            body = new XNode("body");
            Html.Append(body);
        }

        return body;
    }
}

public sealed class ManifestEntry : OpfProperties
{
    public string Filename { get; set; }
    public bool? Linear { get; }
    public bool External { get; }
    public string Id { get; set; }
    public int ReferenceCount { get; set; }

    public ManifestEntry(string filename, IEnumerable<string> opfProperties, bool? linear, bool external, string id)
        : base(opfProperties)
    {
        Filename = filename;
        Linear = linear;
        External = external;
        Id = id;
    }
}

public sealed class TocEntry
{
    public string Title { get; set; }
    public string Target { get; set; }
    public List<TocEntry> Children { get; set; }
    public string Description { get; set; }
    public string Icon { get; set; }
    public string Anchor { get; set; }

    public TocEntry(string title, string target = null, List<TocEntry> children = null, string description = null,
        string icon = null, string anchor = null)
    {
        Title = title;
        Target = target;
        Children = children ?? new List<TocEntry>();
        Description = description;
        Icon = icon;
        Anchor = anchor;
    }
}

public sealed class GuideEntry
{
    public string GuideType { get; }
    public string Title { get; }
    public string Target { get; set; }
    public string Anchor { get; }

    public GuideEntry(string guideType, string title, string target = null, string anchor = null)
    {
        GuideType = guideType;
        Title = title;
        Target = target;
        Anchor = anchor;
    }
}

public sealed class PageMapEntry
{
    public string Label { get; }
    public string Target { get; set; }
    public string Anchor { get; }

    public PageMapEntry(string label, string target = null, string anchor = null)
    {
        Label = label;
        Target = target;
        Anchor = anchor;
    }
}

public sealed class OutputFile
{
    public byte[] BinaryData { get; }
    public string Mimetype { get; }
    public int? Height { get; }
    public int? Width { get; }

    public OutputFile(byte[] binaryData, string mimetype, int? height = null, int? width = null)
    {
        BinaryData = binaryData;
        Mimetype = mimetype;
        Height = height;
        Width = width;
    }
}

public partial class KfxEpub
{
    public const bool GenerateEpub2Compatible = false;
    public const bool PlaceFilesInSubdirs = true;
    public const string OebpsDir = "item";

    public const string OpfFilepath = "/standard.opf";
    public const string NcxFilepath = "/toc.ncx";
    public const string TextFilepath = "/xhtml/p-%s.xhtml";
    public const string FrontmatterTextFilepath = "/xhtml/p-fmatter-%s.xhtml";
    public const string SectionTextFilepath = "/xhtml/%s.xhtml";
    public const string CoverFilepath = "/xhtml/p-cover.xhtml";
    public const string TocFilepath = "/xhtml/p-toc.xhtml";
    public const string ColophonFilepath = "/xhtml/p-colophon.xhtml";
    public const string NavFilepath = "/navigation-documents%s.xhtml";
    public const string FontFilepath = "/fonts/%s";
    public const string ImageFilepath = "/image/%s";
    public const string StylesCssFilepath = "/style/book-style.css";
    public const string KfxCssFilepath = "/style/style-kfx.css";
    public const string ResetCssFilepath = "/style/style-reset.css";
    public const string StandardCssFilepath = "/style/style-standard.css";
    public const string AdvanceCssFilepath = "/style/style-advance.css";
    public const string CheckCssFilepath = "/style/style-check.css";
    public const string KadokawaCssFilepath = "/style/style-kadokawa.css";
    public const string KarcCssFilepath = "/style/style-karc.css";
    public const string FixedLayoutCssFilepath = "/style/fixed-layout-jp.css";
    public const string LayoutCssFilepath = "/style/layout%04d.css";

    public string EpubTypeAttr => XNode.AttrKey(XmlNamespaces.Epub, "type");
    public string XlinkHrefAttr => XNode.AttrKey(XmlNamespaces.XLink, "href");
    public string XmlLangAttr => XNode.AttrKey(XmlNamespaces.Xml, "lang");

    public Dictionary<string, OutputFile> OebpsFiles { get; } = new();
    public List<BookPart> BookParts { get; } = new();
    public List<TocEntry> NcxToc { get; } = new();
    public List<ManifestEntry> Manifest { get; } = new();
    public Dictionary<string, ManifestEntry> ManifestFiles { get; } = new();
    public HashSet<string> ManifestIds { get; } = new();
    public List<GuideEntry> Guide { get; } = new();
    public HashSet<string> CssFiles { get; } = new();
    public List<PageMapEntry> Pagemap { get; } = new();
    public HashSet<string> DomImageFilenames { get; } = new();
    public Dictionary<string, string> OriginalDomImageRoles { get; } = new();

    public string Asin = "";
    public string BookId = "";
    public string Title = "";
    public string TitlePronunciation = "";
    public List<string> Authors = new();
    public List<string> AuthorPronunciations = new();
    public string Publisher = "";
    public string Pubdate = "";
    public string DescriptionText = "";
    public string Subject = "";
    public string Rights = "";
    public string Language = "";
    public string SourceLanguage = "";
    public string TargetLanguage = "";
    public bool IsSample;
    public bool IsDictionary;
    public bool OverrideKindleFont;
    public string NcxLocation;
    public string TocNcxId;
    public string OrientationLock = "none";
    public decimal? MinAspectRatio;
    public decimal? MaxAspectRatio;
    public int? OriginalWidth;
    public int? OriginalHeight;
    public bool FixedLayout;
    public bool RegionMagnification;
    public bool VirtualPanels;
    public bool VirtualPanelsAllowed;
    public bool IllustratedLayout;
    public bool HtmlCover;
    public bool GuidedViewNative;
    public bool RemoveHtmlCover;
    public bool ScrolledContinuous;
    public string BookType;
    public bool IsChildren;
    public bool IsComic;
    public bool IsMagazine;
    public bool IsNotebook;
    public string WritingMode = "horizontal-tb";
    public string PageProgressionDirection = "ltr";
    public string Uid;
    public bool GenerateEpub2;
    public bool Epub2Desired;
    public bool ForceCover;
    public bool WillOutput = true;

    public void SetBookType(string bookType)
    {
        if (BookType is not null && BookType != bookType)
        {
            Log.Error($"書籍類型從 {BookType} 變成 {bookType}");
        }

        BookType = bookType;
        IsChildren = IsComic = IsMagazine = IsNotebook = false;
        switch (bookType)
        {
            case "children": IsChildren = true; break;
            case "comic": IsComic = true; break;
            case "magazine": IsMagazine = true; break;
            case "notebook": IsNotebook = true; break;
        }
    }

    public string ManifestIdForFilename(string filename)
    {
        var basename = filename.Split('/').Last();
        basename = Path.GetFileNameWithoutExtension(basename) is { Length: > 0 } name ? name : basename;
        if (filename.TrimStart('/').StartsWith("image/") && basename != "cover")
        {
            basename = "img-" + basename;
        }

        return NameUtil.FixHtmlId(basename[..Math.Min(64, basename.Length)], IllustratedLayout);
    }

    public ManifestEntry ManifestResource(string filename, IEnumerable<string> opfProperties = null, bool? linear = null,
        bool external = false, byte[] data = null, string mimetype = null, int? height = null, int? width = null,
        string idref = null, bool reportDupe = true)
    {
        if (ManifestFiles.ContainsKey(filename))
        {
            if (reportDupe) Log.Error($"manifest 中有重複檔名：{filename}");
            return ManifestFiles[filename];
        }

        var id = NameUtil.FixHtmlId(idref ?? ManifestIdForFilename(filename), IllustratedLayout);
        id = NameUtil.MakeUniqueName(id, ManifestIds, "_");
        var entry = new ManifestEntry(filename, opfProperties, linear, external, id);
        Manifest.Add(entry);
        ManifestFiles[filename] = entry;
        ManifestIds.Add(id);
        ReferenceResource(entry);

        if (data is not null)
        {
            AddOebpsFile(filename, data, mimetype ?? MimetypeOfFilename(filename), height, width);
        }

        return entry;
    }

    public ManifestEntry ReferenceResource(ManifestEntry entry)
    {
        entry.ReferenceCount++;
        return entry;
    }

    public void UnreferenceResource(string filename)
    {
        if (!ManifestFiles.TryGetValue(filename, out var entry))
        {
            Log.Error($"未參考的檔案不在 manifest：{filename}");
            return;
        }

        entry.ReferenceCount--;
        if (entry.ReferenceCount == 0)
        {
            Manifest.Remove(entry);
            ManifestFiles.Remove(filename);
            ManifestIds.Remove(entry.Id);
            RemoveOebpsFile(filename);
        }
    }

    public void AddGuideEntry(string guideType, string title = null, string target = null, string anchor = null)
    {
        var standard = guideType == "srl" ? "text" : guideType;
        Guide.Add(new GuideEntry(standard,
            string.IsNullOrEmpty(title) ? DefaultLabelOfGuideType.GetValueOrDefault(standard, guideType) : title,
            target, anchor));
    }

    public void AddPagemapEntry(string label, string target = null, string anchor = null)
        => Pagemap.Add(new PageMapEntry(label, target, anchor));

    public void AddOebpsFile(string filename, byte[] binaryData, string mimetype, int? height = null, int? width = null)
        => OebpsFiles[filename] = new OutputFile(binaryData, mimetype, height, width);

    public void RemoveOebpsFile(string filename) => OebpsFiles.Remove(filename);

    public byte[] GenerateEpub()
    {
        Uid = !string.IsNullOrEmpty(Asin) ? Asin : !string.IsNullOrEmpty(BookId) ? BookId : "urn:uuid:" + Guid.NewGuid();
        if (Authors.Count == 0) Authors = new List<string> { "Unknown" };
        if (string.IsNullOrEmpty(Title)) Title = "Unknown";
        if (IsSample) Title += " - Sample";

        IdentifyCover();
        if (RemoveHtmlCover) DoRemoveHtmlCover();
        if (ForceCover) AddGenericCoverPage();
        if (BookParts.Count == 0) throw new InvalidOperationException("書籍沒有任何內容");

        if (NcxToc.Count == 0)
        {
            var guide = Guide.OrderBy(g => TocPriorityOfGuideType.GetValueOrDefault(g.GuideType, 999)).FirstOrDefault();
            NcxToc.Add(guide is not null ? new TocEntry(guide.Title, target: guide.Target) : new TocEntry("Content", BookParts[0].Filename));
        }

        RemovePageMarkers();

        if (!GenerateEpub2)
        {
            if (!BookParts.Any(bp => bp.IsNav)) CreateEpub3Nav();
            RenameLandmarkBookParts();
            RenameColophonBookPart();
            RenameSpineBookParts();
        }

        ApplyPageTypeClasses();
        ApplyPublisherClassMapping();
        InlineRemainingGeneratedStyles();

        if (FixedLayout && (OriginalHeight is null || OriginalWidth is null) && (IsComic || IsChildren))
        {
            CompareFixedLayoutViewports();
        }

        SaveBookParts();
        CreateCssFiles();
        EnsureLinkedCssFiles();
        EpubDiagnostics.RecordCssFiles(this);
        EpubDiagnostics.RecordFinal(this);
        CreateOpf();
        return ZipEpub();
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newValue + text[(index + oldValue.Length)..];
    }

    public void RemovePageMarkers()
    {
        Pagemap.Clear();
        foreach (var bookPart in BookParts)
        {
            foreach (var elem in bookPart.Html.Descendants().ToList())
            {
                if (!Regex.IsMatch(elem.Get("id", ""), @"^page_[0-9]+$")) continue;
                elem.Pop("id");
                if (elem.IsLocal("span") && elem.Attributes.Count == 0 && elem.Children.Count == 0 && string.IsNullOrEmpty(elem.Text))
                {
                    var parent = elem.Parent;
                    if (parent is not null)
                    {
                        var index = parent.Children.IndexOf(elem);
                        var tail = elem.Tail;
                        parent.RemoveChild(elem);
                        if (!string.IsNullOrEmpty(tail))
                        {
                            if (index > 0)
                            {
                                var previous = parent.Children[index - 1];
                                previous.Tail = (previous.Tail ?? "") + tail;
                            }
                            else
                            {
                                parent.Text = (parent.Text ?? "") + tail;
                            }
                        }
                    }
                }
            }
        }
    }

    public BookPart NewBookPart(string filename = null, IEnumerable<string> opfProperties = null, bool? linear = true,
        bool omit = false, XNode html = null, string idref = null, bool first = false)
    {
        var partIndex = BookParts.Count;
        filename ??= TextFilepath.Replace("%s", TextUtil.SequenceNumber(partIndex));
        while (IsBookPartFilename(filename))
        {
            Log.Error($"BookPart 檔名不唯一：{filename}");
            filename = filename.Replace(".", "_.");
        }

        html ??= XhtmlUtil.NewXhtml();
        var bookPart = new BookPart(filename, partIndex, html, opfProperties, linear, omit, idref);
        if (first) BookParts.Insert(0, bookPart);
        else BookParts.Add(bookPart);
        return bookPart;
    }

    public bool IsBookPartFilename(string filename) => BookParts.Any(bp => bp.Filename == filename);

    public void RenameLandmarkBookParts()
    {
        var targets = new List<(string Type, string Href, string RefFrom)>();
        foreach (var navPart in BookParts.Where(bp => bp.IsNav))
        {
            foreach (var nav in navPart.Body().Elements("nav"))
            {
                if (nav.GetNs(XmlNamespaces.Epub, "type") != "landmarks") continue;
                foreach (var a in nav.Descendants().Where(e => e.IsLocal("a")))
                {
                    var types = a.GetNs(XmlNamespaces.Epub, "type")?.Split(' ') ?? Array.Empty<string>();
                    if (types.Contains("cover")) targets.Add(("cover", a.Get("href"), navPart.Filename));
                    else if (types.Contains("toc")) targets.Add(("toc", a.Get("href"), navPart.Filename));
                }
            }
        }

        var renamed = new Dictionary<string, string>();
        foreach (var (type, href, refFrom) in targets)
        {
            var targetFilename = LandmarkTargetFilename(href, refFrom);
            if (targetFilename is null) continue;
            var newFilename = type == "cover" ? CoverFilepath : TocFilepath;
            if (renamed.TryGetValue(targetFilename, out var existing))
            {
                if (existing != newFilename) Log.Warning($"landmark {type} 指向已重新命名的 {targetFilename}");
                continue;
            }

            if (RenameBookPartFile(targetFilename, newFilename)) renamed[targetFilename] = newFilename;
        }
    }

    public void RenameColophonBookPart()
    {
        foreach (var navPart in BookParts.Where(bp => bp.IsNav))
        {
            foreach (var nav in navPart.Body().Elements("nav"))
            {
                if (nav.GetNs(XmlNamespaces.Epub, "type") != "toc") continue;
                foreach (var a in nav.Descendants().Where(e => e.IsLocal("a")))
                {
                    if (a.IterText().Trim() != "奥付") continue;
                    var target = LandmarkTargetFilename(a.Get("href"), navPart.Filename);
                    if (target is not null && target != CoverFilepath && target != TocFilepath)
                    {
                        RenameBookPartFile(target, ColophonFilepath);
                    }

                    return;
                }
            }
        }
    }

    public void RenameSpineBookParts()
    {
        var spineParts = BookParts.Where(bp => bp.Linear is not null && bp.Filename.EndsWith(".xhtml")).ToList();
        if (spineParts.Count == 0) return;
        var tocInSpine = spineParts.Any(bp => bp.Filename == TocFilepath);
        var special = new HashSet<string> { CoverFilepath, TocFilepath, ColophonFilepath };
        var frontmatter = new List<BookPart>();
        var content = new List<BookPart>();
        var beforeToc = tocInSpine;

        foreach (var bookPart in spineParts)
        {
            if (bookPart.Filename == TocFilepath)
            {
                beforeToc = false;
                continue;
            }

            if (special.Contains(bookPart.Filename)) continue;
            if (beforeToc) frontmatter.Add(bookPart); else content.Add(bookPart);
        }

        var map = new Dictionary<string, string>();
        for (var i = 0; i < frontmatter.Count; i++)
        {
            var newFilename = FrontmatterTextFilepath.Replace("%s", TextUtil.SequenceNumber(i + 1, frontmatter.Count));
            if (frontmatter[i].Filename != newFilename) map[frontmatter[i].Filename] = newFilename;
        }

        for (var i = 0; i < content.Count; i++)
        {
            var newFilename = TextFilepath.Replace("%s", TextUtil.SequenceNumber(i + 1, content.Count));
            if (content[i].Filename != newFilename) map[content[i].Filename] = newFilename;
        }

        RenameBookPartFiles(map);
    }

    public void RenameBookPartFiles(Dictionary<string, string> renameMap)
    {
        if (renameMap.Count == 0) return;
        var byFilename = BookParts.ToDictionary(bp => bp.Filename);
        var filtered = new Dictionary<string, string>();
        var newFilenames = new HashSet<string>();
        foreach (var (oldFilename, newFilename) in renameMap)
        {
            if (!byFilename.ContainsKey(oldFilename) || oldFilename == newFilename) continue;
            if (newFilenames.Contains(newFilename))
            {
                Log.Warning($"無法將 {oldFilename} 重新命名為重複的 {newFilename}");
                continue;
            }

            if (byFilename.ContainsKey(newFilename) && !renameMap.ContainsKey(newFilename))
            {
                Log.Warning($"無法將 {oldFilename} 重新命名為既有檔案 {newFilename}");
                continue;
            }

            filtered[oldFilename] = newFilename;
            newFilenames.Add(newFilename);
        }

        var tempMap = new Dictionary<string, string>();
        var tempNames = new HashSet<string>();
        var index = 0;
        foreach (var oldFilename in filtered.Keys.ToList())
        {
            string temp;
            do
            {
                temp = SectionTextFilepath.Replace("%s", $"p-rename-tmp-{index:0000}");
                index++;
            } while (byFilename.ContainsKey(temp) || newFilenames.Contains(temp) || tempNames.Contains(temp));

            tempMap[oldFilename] = temp;
            tempNames.Add(temp);
            byFilename[oldFilename].Filename = temp;
            UpdateBookPartReferences(oldFilename, temp);
        }

        foreach (var (oldFilename, newFilename) in filtered)
        {
            var temp = tempMap[oldFilename];
            byFilename[oldFilename].Filename = newFilename;
            UpdateBookPartReferences(temp, newFilename);
        }
    }

    public string LandmarkTargetFilename(string href, string refFrom)
    {
        if (string.IsNullOrEmpty(href)) return null;
        var filename = PathUtil.GetUrlFilename(PathUtil.UrlAbsolutePath(href, refFrom));
        if (filename is null) return null;
        filename = PathUtil.RemoveUrlFragment(filename);
        return filename.EndsWith(".xhtml") ? filename : null;
    }

    public bool RenameBookPartFile(string oldFilename, string newFilename)
    {
        if (oldFilename == newFilename) return true;
        var bookPart = BookParts.FirstOrDefault(bp => bp.Filename == oldFilename);
        if (bookPart is null)
        {
            Log.Warning($"找不到 landmark 目標 book part：{oldFilename}");
            return false;
        }

        if (BookParts.Any(bp => bp.Filename == newFilename))
        {
            Log.Warning($"無法將 {oldFilename} 重新命名為既有檔案 {newFilename}");
            return false;
        }

        bookPart.Filename = newFilename;
        UpdateBookPartReferences(oldFilename, newFilename);
        return true;
    }

    public void UpdateBookPartReferences(string oldFilename, string newFilename)
    {
        foreach (var anchorName in AnchorUri.Keys.ToList())
        {
            if (PathUtil.RemoveUrlFragment(AnchorUri[anchorName]) == oldFilename)
            {
                var fragment = AnchorUri[anchorName].Split('#').ElementAtOrDefault(1);
                AnchorUri[anchorName] = newFilename + (fragment is not null ? "#" + fragment : "");
            }
        }

        foreach (var guide in Guide)
        {
            if (guide.Target is not null && PathUtil.RemoveUrlFragment(guide.Target) == oldFilename)
            {
                var fragment = guide.Target.Split('#').ElementAtOrDefault(1);
                guide.Target = newFilename + (fragment is not null ? "#" + fragment : "");
            }
        }

        foreach (var page in Pagemap)
        {
            if (page.Target is not null && PathUtil.RemoveUrlFragment(page.Target) == oldFilename)
            {
                var fragment = page.Target.Split('#').ElementAtOrDefault(1);
                page.Target = newFilename + (fragment is not null ? "#" + fragment : "");
            }
        }

        void UpdateToc(List<TocEntry> toc)
        {
            foreach (var entry in toc)
            {
                if (entry.Target is not null && PathUtil.RemoveUrlFragment(entry.Target) == oldFilename)
                {
                    var fragment = entry.Target.Split('#').ElementAtOrDefault(1);
                    entry.Target = newFilename + (fragment is not null ? "#" + fragment : "");
                }

                if (entry.Children.Count > 0) UpdateToc(entry.Children);
            }
        }

        UpdateToc(NcxToc);

        foreach (var bookPart in BookParts)
        {
            foreach (var elem in bookPart.Html.DescendantsAndSelf())
            {
                foreach (var attr in new[] { "href", "src", XlinkHrefAttr })
                {
                    var value = elem.Get(attr);
                    if (value is not null) elem.Set(attr, ReplaceUrl(value, bookPart.Filename, oldFilename, newFilename));
                }
            }
        }
    }

    public string ReplaceUrl(string url, string refFrom, string oldFilename, string newFilename)
    {
        var abs = PathUtil.UrlAbsolutePath(url, refFrom);
        var filename = PathUtil.GetUrlFilename(abs);
        if (PathUtil.RemoveUrlFragment(filename ?? "") != oldFilename) return url;
        var fragment = abs.Split('#').ElementAtOrDefault(1);
        return PathUtil.UrlRelativePath(newFilename + (fragment is not null ? "#" + fragment : ""), refFrom);
    }

    public void LinkCssFile(BookPart bookPart, string cssFile, string cssType = "text/css")
    {
        var head = bookPart.Head();
        var href = PathUtil.UrlRelativePath(cssFile, bookPart.Filename);
        if (cssFile == StylesCssFilepath)
        {
            var fixedHref = PathUtil.UrlRelativePath(FixedLayoutCssFilepath, bookPart.Filename);
            if (head.Elements("link").Any(l => l.Get("rel") == "stylesheet" && l.Get("href") == fixedHref)) return;
        }
        else if (cssFile == FixedLayoutCssFilepath)
        {
            var stylesHref = PathUtil.UrlRelativePath(StylesCssFilepath, bookPart.Filename);
            foreach (var link in head.Elements("link").ToList())
            {
                if (link.Get("rel") == "stylesheet" && link.Get("href") == stylesHref) head.RemoveChild(link);
            }
        }

        if (head.Elements("link").Any(l => l.Get("rel") == "stylesheet" && l.Get("href") == href)) return;

        CssFiles.Add(cssFile);
        var newLink = XhtmlUtil.SubElement(head, "link");
        newLink.Set("rel", "stylesheet");
        newLink.Set("type", cssType);
        newLink.Set("href", href);
    }

    // 部分樣式檔（例如整頁圖片頁的 fixed-layout-jp.css）是在 SaveBookParts 期間才掛上；
    // CreateCssFiles 已於建構子執行完畢，這裡補登記到 manifest。
    public void EnsureLinkedCssFiles()
    {
        foreach (var bookPart in BookParts)
        {
            foreach (var link in bookPart.Head().Elements("link"))
            {
                if (link.Get("rel") != "stylesheet" || !link.Has("href")) continue;
                var cssFile = PathUtil.GetUrlFilename(PathUtil.UrlAbsolutePath(link.Get("href"), bookPart.Filename));
                if (cssFile == FixedLayoutCssFilepath && !ManifestFiles.ContainsKey(FixedLayoutCssFilepath))
                {
                    ManifestResource(FixedLayoutCssFilepath, data: ReadBundledStyle("fixed-layout-jp.css"),
                        mimetype: "text/css");
                }
            }
        }
    }

    public void IdentifyCover()
    {
        if (BookParts.Count == 0) return;
        string coverPage = null;
        var guide = Guide.OrderBy(g => g.GuideType).ThenBy(g => g.Title).FirstOrDefault(g => g.GuideType == "cover");
        if (guide is not null) coverPage = PathUtil.RemoveUrlFragment(guide.Target);
        else if (Manifest.Any(m => m.IsCoverImage)) coverPage = BookParts[0].Filename;

        if (coverPage is null) return;
        var part = BookParts.FirstOrDefault(bp => bp.Filename == coverPage);
        if (part is null)
        {
            Log.Warning($"找不到封面頁：{coverPage}");
            return;
        }

        part.IsCoverPage = true;
        if (part.PartIndex != 0) Log.Warning($"封面頁不是第一頁：{coverPage}");
    }

    public void DoRemoveHtmlCover()
    {
        var part = BookParts.FirstOrDefault(bp => bp.IsCoverPage);
        if (part is not null) BookParts.Remove(part);
        Guide.RemoveAll(g => g.GuideType == "cover");
    }

    public void AddGenericCoverPage()
    {
        if (BookParts.Any(bp => bp.IsCoverPage)) return;
        // 未提供繪圖字型時，以簡單純色封面代替。
        var imageData = CreateSimpleCoverImage(Title);
        var imageFilename = ImageFilepath.Replace("%s", "cover.jpg");
        var entry = ManifestResource(imageFilename, data: imageData);
        entry.IsCoverImage = true;
        var bookPart = NewBookPart(CoverFilepath, first: true);
        var div = XhtmlUtil.SubElement(bookPart.Body(), "div");
        XhtmlUtil.SubElement(div, "img").Set("src", PathUtil.UrlRelativePath(imageFilename, bookPart.Filename))
            .Set("style", "width: 100%");
        AddGuideEntry("cover", target: bookPart.Filename);
    }

    private static byte[] CreateSimpleCoverImage(string title)
    {
        var width = 600;
        var height = 800;
        var bitmap = new System.Windows.Media.Imaging.WriteableBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgr24, null);
        var pixels = new byte[width * height * 3];
        Array.Fill(pixels, (byte)220);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 3, 0);
        using var stream = new MemoryStream();
        var encoder = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        encoder.Save(stream);
        return stream.ToArray();
    }

    public void CompareFixedLayoutViewports()
    {
        var counts = new Dictionary<(int, int), int>();
        int? coverWidth = null, coverHeight = null;
        foreach (var bookPart in BookParts)
        {
            var head = bookPart.Html.Element("head");
            if (head is null) continue;
            foreach (var meta in head.Elements("meta"))
            {
                if (meta.Get("name") != "viewport") continue;
                var content = meta.Get("content", "");
                var mw = Regex.Match(content, "width=([0-9]+)");
                var mh = Regex.Match(content, "height=([0-9]+)");
                if (!mw.Success || !mh.Success) continue;
                var width = int.Parse(mw.Groups[1].Value);
                var height = int.Parse(mh.Groups[1].Value);
                counts[(width, height)] = counts.GetValueOrDefault((width, height)) + 1;
                if (bookPart.IsCoverPage)
                {
                    coverWidth = width;
                    coverHeight = height;
                }
            }
        }

        if (counts.Count == 0) return;
        var best = counts.OrderByDescending(p => p.Value).First();
        OriginalWidth = best.Key.Item1;
        OriginalHeight = best.Key.Item2;
    }

    public void SaveBookParts()
    {
        foreach (var bookPart in BookParts)
        {
            // 保留 lxml 風格：根節點一定是 XHTML html。
            bookPart.Html.Name = "html";
            bookPart.Html.Namespace = XmlNamespaces.XHtml;
            var head = bookPart.Head();
            var body = bookPart.Body();
            if (head.Element("title") is null)
            {
                var title = XhtmlUtil.SubElement(head, "title");
                title.Text = Title;
            }

            FinalizeXhtmlStructure(bookPart, head, body);
            ConsolidateHtml(body);
            SplitLongTcySpans(body);
            FlattenRubyBases(body);
            BeautifyHtml(bookPart);
        }

        PrepareDomImageSequenceWidths();
        foreach (var bookPart in BookParts)
        {
            var body = bookPart.Body();
            PrepareDomImageFilenames(bookPart);
            if (body.FindDescendantNs(XmlNamespaces.Svg, "svg") is not null) bookPart.Properties.Add("svg");
            if (body.FindDescendantNs(XmlNamespaces.MathMl, "math") is not null) bookPart.Properties.Add("mathml");
            if (body.Descendants().Any(e => (e.Get("src") ?? "").StartsWith("http://") ||
                                            (e.Get("src") ?? "").StartsWith("https://")))
            {
                bookPart.Properties.Add("remote-resources");
            }

            foreach (var e in body.Descendants())
            {
                if ((e.GetNs(XmlNamespaces.Epub, "type") ?? "").StartsWith("amzn:"))
                {
                    bookPart.Html.SetNs(XmlNamespaces.Epub, "prefix",
                        "amzn: https://kindlegen.s3.amazonaws.com/AmazonKindlePublishingGuidelines.pdf");
                    break;
                }
            }

            if (bookPart.Omit) continue;
            var doctype = GenerateEpub2
                ? "<!DOCTYPE html PUBLIC '-//W3C//DTD XHTML 1.1//EN' 'http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd'>"
                : "<!DOCTYPE html>";
            var htmlStr = XhtmlSerializer.Serialize(bookPart.Html, doctype, xmlDeclaration: true);
            htmlStr = FormatXhtmlDocumentHeader(htmlStr);
            ManifestResource(bookPart.Filename, bookPart.Properties, bookPart.Linear, idref: bookPart.Idref,
                data: Encoding.UTF8.GetBytes(htmlStr), mimetype: "application/xhtml+xml");
        }

        RefreshBookStyleCss();
    }

    public void FinalizeXhtmlStructure(BookPart bookPart, XNode head, XNode body)
    {
        foreach (var meta in head.Elements("meta").Where(m => m.Has("charset")).ToList())
        {
            head.RemoveChild(meta);
        }

        var charset = new XNode("meta");
        charset.Set("charset", "UTF-8");
        head.Insert(0, charset);

        var title = head.Element("title");
        if (title is not null)
        {
            head.RemoveChild(title);
            head.Insert(1, title);
        }

        if (bookPart.Html.GetNs(XmlNamespaces.Xml, "lang") is null)
        {
            bookPart.Html.SetNs(XmlNamespaces.Xml, "lang", string.IsNullOrEmpty(Language) ? "ja" : Language);
        }

        var existingClasses = (bookPart.Html.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(c => c is not ("vrtl" or "hltr" or "vltr" or "hrtl")).ToList();

        if (bookPart.IsNav)
        {
            body.Pop("class");
            body.Pop("style");
        }
        else if (bookPart.IsFxl)
        {
            ConvertToFixedLayoutSvgPage(bookPart, head, body);
        }
        else if (ShouldUseFixedLayoutSvg(bookPart, body))
        {
            if (ConvertToFixedLayoutSvgPage(bookPart, head, body))
            {
                bookPart.IsFxl = true;
            }
            else if (IsImageOnlyBody(body))
            {
                ConvertToFitImagePage(bookPart);
            }
            else
            {
                FinalizeReflowableBody(bookPart, body);
            }
        }
        else if (IsImageOnlyBody(body))
        {
            ConvertToFitImagePage(bookPart);
        }
        else
        {
            FinalizeReflowableBody(bookPart, body);
        }

        // 電書協樣式：可重排頁的 <html> 依「這一頁在 KFX 的書寫方向」加上 vrtl 或 hltr。
        // fixed-layout 頁（含整頁圖片頁）遵循 FXL 樣板，不加方向 class。
        if (!bookPart.IsNav && !bookPart.IsFxl && !string.IsNullOrEmpty(bookPart.WritingModeValue))
        {
            existingClasses.Insert(0, IsVerticalWritingMode(bookPart.WritingModeValue) ? "vrtl" : "hltr");
        }

        if (existingClasses.Count > 0) bookPart.Html.Set("class", string.Join(" ", existingClasses));
        else bookPart.Html.Pop("class");

        // lxml 會把 epub:type 放在 class 之前；字典插入順序需手動調整。
        if (body.HasNs(XmlNamespaces.Epub, "type") && body.Has("class"))
        {
            var bodyClass = body.Pop("class");
            body.Set("class", bodyClass);
        }
    }

    public bool ShouldUseFixedLayoutSvg(BookPart bookPart, XNode body)
    {
        if (bookPart.IsPageImage) return true;
        // KFX 的封面頁本身看不出整本書圖片頁的排版方式，跟隨本書其他圖片頁的判定。
        return bookPart.IsCoverPage && IsImageOnlyBody(body) && BookParts.Any(bp => bp.IsPageImage);
    }

    public void FinalizeReflowableBody(BookPart bookPart, XNode body)
    {
        EnsureMainWrapper(body);
        if (bookPart.Filename == TocFilepath && !body.Has("class")) body.Set("class", "p-toc");
        else if (bookPart.Filename == ColophonFilepath && !body.Has("class")) body.Set("class", "p-colophon");
        else if (!body.Has("class")) body.Set("class", "p-text");
    }

    // 奥付與扉頁的殘餘宣告直接寫進元素的 style，避免為了少數頁面產生 style-kfx.css。
    // 本文保留生成 class，統一寫入 style-kfx.css，之後才能統計並比對電書協 utility class。
    // 有 ::first-line 等選擇器附掛規則的 class 一律例外保留。
    public void InlineRemainingGeneratedStyles()
    {
        EpubDiagnostics.RecordGeneratedRules(CssRules, ExtraCssRules, MediaQueries);
        foreach (var bookPart in BookParts)
        {
            if (bookPart.IsNav || bookPart.IsFxl) continue;
            if (!InlineRemainingOnPage(bookPart)) continue;
            foreach (var elem in bookPart.Body().DescendantsAndSelf())
            {
                var classNames = (elem.Get("class", "") ?? "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(c => CssRules.ContainsKey(ClassSelector(c)) || ExtraCssRules.ContainsKey(ClassSelector(c)))
                    .Where(c => !CssRules.Keys.Any(k => k.StartsWith(ClassSelector(c) + ":", StringComparison.Ordinal)))
                    .ToList();
                if (classNames.Count == 0) continue;

                var beforeStyle = elem.Get("style", "") ?? "";
                var merged = new Style(beforeStyle);
                var mergedDecls = new List<string>();
                foreach (var className in classNames)
                {
                    var selector = ClassSelector(className);
                    if (CssRules.TryGetValue(selector, out var rule))
                    {
                        merged.Update(rule, replace: true);
                        mergedDecls.Add(rule.ToString());
                    }

                    if (ExtraCssRules.TryGetValue(selector, out var extra))
                    {
                        merged.Update(new Style(extra), replace: true);
                        mergedDecls.Add(extra);
                    }

                    RemoveClass(elem, className);
                }

                // 固定屬性順序（其他屬性 → class → style），讓 C# 與 Python 的輸出一致。
                // Dictionary 在移除後重新加入時會重用空位，所以這裡直接重建。
                var classValue = elem.Get("class");
                var styleValue = merged.ToString();
                var others = elem.Attributes
                    .Where(pair => pair.Key is not ("class" or "style"))
                    .ToList();
                elem.Attributes.Clear();
                foreach (var (key, value) in others) elem.Attributes[key] = value;
                if (classValue is not null) elem.Set("class", classValue);
                elem.Set("style", styleValue);
                EpubDiagnostics.InlineEvent(bookPart.Filename, elem, classNames, beforeStyle,
                    string.Join("; ", mergedDecls), styleValue);
            }
        }

        // 同一個 utility class 可能被多個元素共用，逐一內聯後才能移除規則；
        // 仍留在本文／nav／fixed-layout 頁面的 class 要保留給 style-kfx.css。
        var usedClasses = UsedHtmlClasses();
        foreach (var selector in ExtraCssRules.Keys.ToList())
        {
            var match = Regex.Match(selector, @"^\.([A-Za-z0-9_-]+)");
            if (match.Success && usedClasses.Contains(match.Groups[1].Value)) continue;
            ExtraCssRules.Remove(selector);
        }
    }

    private static bool InlineRemainingOnPage(BookPart bookPart)
    {
        var frontmatterPrefix = FrontmatterTextFilepath[..FrontmatterTextFilepath.IndexOf("%s", StringComparison.Ordinal)];
        return bookPart.Filename == ColophonFilepath ||
               bookPart.Filename.StartsWith(frontmatterPrefix, StringComparison.Ordinal);
    }

    // 在產生 CSS 之前先補上頁型 class，讓出版社 CSS（style-kadokawa.css 等）能命中，
    // 並移除 KFX 模擬用的 body 邊距，避免與 .main 的內距疊加。
    public void ApplyPageTypeClasses()
    {
        var publisherCss = UsesPublisherCss();
        var colophon = BookParts.FirstOrDefault(bp => bp.Filename == ColophonFilepath);
        var colophonSpacingClass = colophon is null ? null : FindBodySpacingClass(colophon.Body());

        foreach (var bookPart in BookParts)
        {
            if (bookPart.IsNav) continue;
            var body = bookPart.Body();
            if (bookPart.IsFxl || ShouldUseFixedLayoutSvg(bookPart, body) || IsImageOnlyBody(body)) continue;

            var beforeClass = body.Get("class", "") ?? "";
            var pageClass = bookPart.Filename == TocFilepath ? "p-toc"
                : bookPart.Filename == ColophonFilepath ? "p-colophon"
                : "p-text";
            string removedClass = null;
            var reason = publisherCss ? "publisher-default" : "default";
            if (publisherCss && pageClass != "p-toc")
            {
                var spacingClass = FindBodySpacingClass(body);
                if (spacingClass is not null)
                {
                    if (pageClass == "p-colophon")
                    {
                        RemoveClass(body, spacingClass);
                        removedClass = spacingClass;
                        reason = "publisher-colophon";
                    }
                    else if (HasCenterTextAlign(body))
                    {
                        pageClass = "p-titlepage";
                        RemoveClass(body, spacingClass);
                        removedClass = spacingClass;
                        reason = "publisher-center-text";
                    }
                    else if (spacingClass == colophonSpacingClass)
                    {
                        pageClass = "p-colophon2";
                        RemoveClass(body, spacingClass);
                        removedClass = spacingClass;
                        reason = "publisher-same-spacing-as-colophon";
                    }
                }
            }

            AddClass(body, pageClass);
            EpubDiagnostics.PageTypeEvent(bookPart.Filename, beforeClass, body.Get("class", "") ?? "",
                removedClass, pageClass, publisherCss, reason);
        }
    }

    // body 上的樣式若只有 margin-left/right + padding-top/bottom 且左右為百分比，
    // 代表它是 KFX 模擬 .p-colophon .main 等頁面內距的樣式。
    private static bool IsBodySpacingStyle(Style style)
    {
        if (style.Count == 0) return false;
        if (!style.Properties.Keys.All(k => k is "margin-left" or "margin-right" or "padding-top" or "padding-bottom"))
            return false;
        return style.Get("margin-left", "").EndsWith("%", StringComparison.Ordinal) ||
               style.Get("margin-right", "").EndsWith("%", StringComparison.Ordinal);
    }

    private string FindBodySpacingClass(XNode body)
    {
        foreach (var className in (body.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CssRules.TryGetValue(ClassSelector(className), out var style) && IsBodySpacingStyle(style))
                return className;
        }

        return null;
    }

    private static bool HasCenterTextAlign(XNode body)
    {
        var classes = (body.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (classes.Contains("align-center")) return true;
        return Regex.IsMatch(body.Get("style", "") ?? "", @"(^|;)\s*text-align\s*:\s*center");
    }

    private static void RemoveClass(XNode elem, string className)
    {
        var classes = (elem.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(c => c != className).ToList();
        if (classes.Count > 0) elem.Set("class", string.Join(" ", classes));
        else elem.Pop("class");
    }

    public string FormatXhtmlDocumentHeader(string html)
    {
        // 讓輸出格式盡量接近 lxml 版本。
        html = html.Replace("\n class=", " class=");
        html = ReplaceFirst(html, "<html ", "<html\n ");
        html = ReplaceFirst(html, "\" xmlns:epub=", "\"\n xmlns:epub=");
        html = ReplaceFirst(html, "\" xml:lang=", "\"\n xml:lang=");
        var htmlTagEnd = html.IndexOf(">\n<head>", StringComparison.Ordinal);
        if (htmlTagEnd >= 0)
        {
            var htmlTag = ReplaceFirst(html[..htmlTagEnd], "\" class=", "\"\n class=");
            html = htmlTag + html[htmlTagEnd..];
        }

        html = ReplaceFirst(html, "\">\n<head>", "\"\n>\n<head>");
        html = ReplaceFirst(html, "</head>\n<body", "</head>\n\n<body");
        return html;
    }

    public static bool IsImageOnlyBody(XNode body)
    {
        var textTags = new HashSet<string> { "p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "td", "th", "dt", "dd", "blockquote", "nav" };
        if (body.Descendants().Any(e => e.Namespace.Length == 0 && textTags.Contains(e.Name))) return false;
        return body.FindDescendant("img") is not null || body.FindDescendantNs(XmlNamespaces.Svg, "svg") is not null;
    }

    public void ConvertToFitImagePage(BookPart bookPart)
    {
        var body = bookPart.Body();
        var imageSources = new List<(string Src, string Alt)>();
        foreach (var img in body.FindDescendants("img"))
        {
            var src = img.Get("src");
            if (string.IsNullOrEmpty(src)) continue;
            var filename = PathUtil.GetUrlFilename(PathUtil.UrlAbsolutePath(src, bookPart.Filename));
            if (filename is not null) OriginalDomImageRoles.TryAdd(filename, OriginalImageRole(img));
            imageSources.Add((src, img.Get("alt", "")));
        }

        foreach (var svg in body.FindDescendantsNs(XmlNamespaces.Svg, "svg"))
        {
            foreach (var image in svg.FindDescendantsNs(XmlNamespaces.Svg, "image"))
            {
                var src = image.GetNs(XmlNamespaces.XLink, "href") ?? image.Get("href");
                if (string.IsNullOrEmpty(src)) continue;
                var filename = PathUtil.GetUrlFilename(PathUtil.UrlAbsolutePath(src, bookPart.Filename));
                if (filename is not null) OriginalDomImageRoles.TryAdd(filename, OriginalImageRole(svg));
                imageSources.Add((src, ""));
            }
        }

        if (imageSources.Count == 0) return;

        body.Clear();
        body.Pop("class");
        body.PopNs(XmlNamespaces.Epub, "type");
        if (bookPart.IsCoverPage)
        {
            body.SetNs(XmlNamespaces.Epub, "type", "cover");
            body.Set("class", "p-cover");
        }
        else body.Set("class", "p-image");

        body.Text = "\n";
        var main = XhtmlUtil.SubElement(body, "div");
        main.Set("class", "main");
        main.Text = "\n";
        main.Tail = "\n";
        foreach (var (src, alt) in imageSources)
        {
            var p = XhtmlUtil.SubElement(main, "p");
            p.Tail = "\n";
            var img = XhtmlUtil.SubElement(p, "img");
            img.Set("class", "fit").Set("src", src).Set("alt", alt);
        }
    }

    public bool ConvertToFixedLayoutSvgPage(BookPart bookPart, XNode head, XNode body)
    {
        var (src, imageWidth, imageHeight) = ExtractSingleFixedLayoutImage(bookPart, body);
        if (src is null) return false;
        var (viewportWidth, viewportHeight) = FixedLayoutViewportSize(head);
        var width = viewportWidth ?? imageWidth ?? OriginalWidth;
        var height = viewportHeight ?? imageHeight ?? OriginalHeight;
        if (width is null || height is null)
        {
            Log.Warning($"固定版式頁 {bookPart.Filename} 沒有可用的 viewport 或圖片尺寸");
            return false;
        }

        LinkCssFile(bookPart, FixedLayoutCssFilepath);
        var fixedHref = PathUtil.UrlRelativePath(FixedLayoutCssFilepath, bookPart.Filename);
        var charset = head.Elements("meta").FirstOrDefault(m => m.Has("charset"));
        var title = head.Element("title");
        var link = head.Elements("link").FirstOrDefault(l => l.Get("rel") == "stylesheet" && l.Get("href") == fixedHref);
        var viewport = head.Elements("meta").FirstOrDefault(m => m.Get("name") == "viewport");
        if (viewport is null)
        {
            // 可重排書中的整頁圖片頁原本沒有 viewport，改用圖片尺寸建立。
            viewport = new XNode("meta");
            viewport.Set("name", "viewport");
            viewport.Set("content", $"width={width}, height={height}");
        }

        var ordered = new[] { charset, title, link, viewport }.Where(e => e is not null).ToList();
        foreach (var e in ordered) head.RemoveChild(e);
        for (var i = 0; i < ordered.Count; i++) head.Insert(i, ordered[i]);

        body.Clear();
        if (bookPart.IsCoverPage) body.SetNs(XmlNamespaces.Epub, "type", "cover");
        body.Text = "\n";
        var main = XhtmlUtil.SubElement(body, "div");
        main.Set("class", "main");
        main.Text = "\n\n";
        main.Tail = "\n";
        var svg = XhtmlUtil.SubElementNs(main, XmlNamespaces.Svg, "svg");
        svg.NsMap[""] = XmlNamespaces.Svg;
        svg.NsMap["xlink"] = XmlNamespaces.XLink;
        svg.Set("version", "1.1").Set("width", "100%").Set("height", "100%")
            .Set("viewBox", $"0 0 {width} {height}");
        svg.Text = "\n";
        svg.Tail = "\n\n";
        var image = XhtmlUtil.SubElementNs(svg, XmlNamespaces.Svg, "image");
        image.Set("width", width.Value.ToString()).Set("height", height.Value.ToString());
        image.SetNs(XmlNamespaces.XLink, "href", src);
        image.Tail = "\n";
        return true;
    }

    public (string Src, int? Width, int? Height) ExtractSingleFixedLayoutImage(BookPart bookPart, XNode body)
    {
        var sources = new List<(string Src, int? Width, int? Height)>();
        foreach (var img in body.FindDescendants("img"))
        {
            var src = img.Get("src");
            if (string.IsNullOrEmpty(src)) continue;
            var width = FixedLayoutLength(img.Get("width"));
            var height = FixedLayoutLength(img.Get("height"));
            if (width is null || height is null) (width, height) = FixedLayoutImageFileSize(src, bookPart.Filename);
            sources.Add((src, width, height));
        }

        foreach (var svg in body.FindDescendantsNs(XmlNamespaces.Svg, "svg"))
        {
            var (vw, vh) = FixedLayoutSvgViewboxSize(svg);
            foreach (var image in svg.FindDescendantsNs(XmlNamespaces.Svg, "image"))
            {
                var src = image.GetNs(XmlNamespaces.XLink, "href") ?? image.Get("href");
                if (string.IsNullOrEmpty(src)) continue;
                var width = FixedLayoutLength(image.Get("width")) ?? vw;
                var height = FixedLayoutLength(image.Get("height")) ?? vh;
                if (width is null || height is null) (width, height) = FixedLayoutImageFileSize(src, bookPart.Filename);
                sources.Add((src, width, height));
            }
        }

        if (sources.Count != 1)
        {
            Log.Warning($"固定版式頁 {bookPart.Filename} 有 {sources.Count} 個圖片來源；保留原 XHTML");
            return (null, null, null);
        }

        var source = sources[0];
        var filename = PathUtil.GetUrlFilename(PathUtil.UrlAbsolutePath(source.Src, bookPart.Filename));
        if (filename is not null) OriginalDomImageRoles[PathUtil.RemoveUrlFragment(filename)] = "illustration";
        return source;
    }

    public static (int? Width, int? Height) FixedLayoutViewportSize(XNode head)
    {
        foreach (var meta in head.Elements("meta"))
        {
            if (meta.Get("name") != "viewport") continue;
            var content = meta.Get("content", "");
            var mw = Regex.Match(content, "width=([0-9]+)");
            var mh = Regex.Match(content, "height=([0-9]+)");
            if (mw.Success && mh.Success) return (int.Parse(mw.Groups[1].Value), int.Parse(mh.Groups[1].Value));
        }

        return (null, null);
    }

    public (int? Width, int? Height) FixedLayoutImageFileSize(string src, string refFrom)
    {
        var filename = PathUtil.GetUrlFilename(PathUtil.UrlAbsolutePath(src, refFrom));
        filename = PathUtil.RemoveUrlFragment(filename ?? "");
        if (!OebpsFiles.TryGetValue(filename, out var file)) return (null, null);
        return (file.Width is > 0 ? file.Width : null, file.Height is > 0 ? file.Height : null);
    }

    public static int? FixedLayoutLength(string value)
    {
        if (value is null) return null;
        var match = Regex.Match(value.Trim(), @"^([0-9]+)(?:px)?$");
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    public static (int? Width, int? Height) FixedLayoutSvgViewboxSize(XNode svg)
    {
        var match = Regex.Match(svg.Get("viewBox", ""), @"^\s*0\s+0\s+([0-9]+)\s+([0-9]+)\s*$");
        return match.Success ? (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)) : (null, null);
    }

    public static void EnsureMainWrapper(XNode body)
    {
        if (body.Children.Count == 1 && body.Children[0].IsLocal("div") &&
            (body.Children[0].Get("class", "").Split(' ').Contains("main"))) return;

        var main = new XNode("div");
        main.Set("class", "main");
        main.Text = (body.Text ?? "") + "\n";
        body.Text = null;
        foreach (var child in body.Children.ToList())
        {
            body.RemoveChild(child);
            main.Append(child);
            if (string.IsNullOrEmpty(child.Tail)) child.Tail = "\n";
        }

        main.Tail = "\n";
        body.Append(main);
    }

    public void ConsolidateHtml(XNode body)
    {
        foreach (var topTag in body.Elements().ToList())
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var e in topTag.DescendantsAndSelf().ToList())
                {
                    if (!e.IsLocal("a") && !e.IsLocal("b") && !e.IsLocal("em") && !e.IsLocal("i") &&
                        !e.IsLocal("span") && !e.IsLocal("strong") && !e.IsLocal("sub") && !e.IsLocal("sup") &&
                        !e.IsLocal("u")) continue;

                    var next = e.NextSibling();
                    while (string.IsNullOrEmpty(e.Tail) && next is not null && next.Name == e.Name &&
                           next.Namespace == e.Namespace && AttributesEqual(e, next) &&
                           !IsTextCombineElement(e) && !IsTextCombineElement(next))
                    {
                        if (!string.IsNullOrEmpty(next.Text))
                        {
                            if (e.Children.Count > 0)
                            {
                                var last = e.Children[^1];
                                last.Tail = (last.Tail ?? "") + next.Text;
                            }
                            else e.Text = (e.Text ?? "") + next.Text;
                            next.Text = "";
                        }

                        foreach (var child in next.Children.ToList())
                        {
                            next.RemoveChild(child);
                            e.Append(child);
                        }

                        if (!string.IsNullOrEmpty(next.Tail)) e.Tail = next.Tail;
                        next.Remove();
                        changed = true;
                        next = e.NextSibling();
                    }

                    if (changed) break;
                }
            }
        }

        foreach (var e in body.Descendants().Where(n => n.IsLocal("span") && n.Attributes.Count == 0).ToList())
        {
            e.Name = "temporary-tag";
            e.Namespace = "";
        }

        XhtmlUtil.StripTags(body, "temporary-tag");

        while (true)
        {
            XNode target = null;
            foreach (var e in body.Descendants().Where(n => n.IsLocal("div")).ToList())
            {
                if (e.Attributes.Count != 0) continue;
                var parent = e.Parent;
                if (parent is null) continue;
                if (parent.Children.Count == 1 && string.IsNullOrEmpty(parent.Text))
                {
                    if (parent.IsLocal("aside") || parent.IsLocal("caption") || parent.IsLocal("div") ||
                        parent.IsLocal("figure") || Regex.IsMatch(parent.Name, "^h[1-6]$") || parent.IsLocal("li") ||
                        parent.IsLocal("p") || parent.IsLocal("td") || parent.IsNs(XmlNamespaces.Idx, "entry"))
                    {
                        e.Name = "temporary-tag";
                        e.Namespace = "";
                    }
                    else if (parent.IsLocal("body") && string.IsNullOrEmpty(e.Text))
                    {
                        var allowed = new HashSet<string>
                        {
                            "aside", "div", "figure", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "iframe", "ol",
                            "p", "table", "ul"
                        };
                        var ok = e.Children.All(c =>
                            (c.Namespace.Length == 0 && allowed.Contains(c.Name)) || c.IsNs(XmlNamespaces.Idx, "entry")) &&
                            e.Children.All(c => string.IsNullOrEmpty(c.Tail));
                        if (ok) e.Name = "temporary-tag";
                        if (e.Name == "temporary-tag") e.Namespace = "";
                    }

                    if (e.Name == "temporary-tag")
                    {
                        target = parent;
                        break;
                    }
                }
            }

            if (target is null) break;
            XhtmlUtil.StripTags(target, "temporary-tag");
        }

        // 清掉巢狀處理後仍殘留的 temporary-tag。
        StripTagRepeatedly(body, "temporary-tag");
    }

    // StripTags 一次走訪在少數巢狀 ruby 結構會漏掉節點，重複執行直到沒有為止。
    private static void StripTagRepeatedly(XNode parent, string tag)
    {
        for (var guard = 0; guard < 20; guard++)
        {
            if (!parent.Descendants().Any(n => n.Name == tag)) return;
            XhtmlUtil.StripTags(parent, tag);
        }
    }

    private static bool AttributesEqual(XNode a, XNode b)
    {
        if (a.Attributes.Count != b.Attributes.Count) return false;
        foreach (var pair in a.Attributes)
        {
            if (b.Get(pair.Key) != pair.Value) return false;
        }

        return true;
    }

    private bool IsTextCombineElement(XNode e)
    {
        if ((e.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tcy")) return true;
        return GeneratedClassStyle(e)?.Get("text-combine-upright") == "all";
    }

    private void SplitLongTcySpans(XNode body)
    {
        foreach (var span in body.FindDescendants("span").ToList())
        {
            if (!(span.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("tcy")) continue;
            SplitTcySpan(span);
        }
    }

    // bw 的 ruby 不使用 <rb>：基準文字直接放在 <ruby> 內。
    // 只有無屬性的純文字 rb 才拆掉，避免遺失樣式或巢狀結構。
    private void FlattenRubyBases(XNode body)
    {
        foreach (var rb in body.FindDescendants("rb").ToList())
        {
            if (rb.Attributes.Count > 0 || rb.Children.Count > 0) continue;
            var parent = rb.Parent;
            if (parent is null) continue;

            var index = parent.Children.IndexOf(rb);
            var text = (rb.Text ?? "") + (rb.Tail ?? "");
            parent.RemoveChild(rb);
            if (index > 0)
            {
                var previous = parent.Children[index - 1];
                previous.Tail = (previous.Tail ?? "") + text;
            }
            else
            {
                parent.Text = (parent.Text ?? "") + text;
            }
        }
    }

    public void BeautifyHtml(BookPart bookPart)
    {
        var html = bookPart.Html;
        var head = bookPart.Head();
        var body = bookPart.Body();
        var blockTags = new HashSet<string>
        {
            "aside", "body", "div", "figure", "h1", "h2", "h3", "h4", "h5", "h6", "head", "hr",
            "link", "meta", "nav", "ol", "p", "style", "table", "title", "ul"
        };

        foreach (var e in new[] { html }.Concat(html.Elements()).Concat(head.Elements()).Concat(body.Elements()).ToList())
        {
            if (e.Name is "html" or "head" or "body") e.Text = (e.Text ?? "") + "\n";
            if (blockTags.Contains(e.Name) || e.IsNs(XmlNamespaces.Idx, "entry")) e.Tail = (e.Tail ?? "") + "\n";
            if (e.IsLocal("div") && e.Get("id", "").StartsWith("amzn_master_range_"))
            {
                foreach (var ee in e.Descendants())
                {
                    if (blockTags.Contains(ee.Name) && string.IsNullOrEmpty(ee.Tail)) ee.Tail = "\n";
                }
            }
        }

        foreach (var nav in body.FindDescendants("nav"))
        {
            nav.Text = (nav.Text ?? "") + "\n";
            foreach (var e in nav.Descendants())
            {
                if (e.IsLocal("ol") && string.IsNullOrEmpty(e.Text)) e.Text = "\n";
                if (e.IsLocal("h1") || e.IsLocal("h2") || e.IsLocal("h3") || e.IsLocal("h4") ||
                    e.IsLocal("h5") || e.IsLocal("h6") || e.IsLocal("li") || e.IsLocal("ol"))
                {
                    if (string.IsNullOrEmpty(e.Tail)) e.Tail = "\n";
                }
            }
        }
    }

    public void CreateEpub3Nav()
    {
        var filename = NavFilepath.Replace("%s", "");
        var count = 0;
        while (IsBookPartFilename(filename)) filename = NavFilepath.Replace("%s", count++.ToString());
        var bookPart = NewBookPart(filename, linear: null);
        bookPart.IsNav = true;
        bookPart.Html.SetNs(XmlNamespaces.Xml, "lang", string.IsNullOrEmpty(Language) ? "ja" : Language);
        var body = XhtmlUtil.SubElement(bookPart.Html, "body");
        body.Text = "\n";

        var nav = XhtmlUtil.SubElement(body, "nav");
        nav.SetNs(XmlNamespaces.Epub, "type", "toc");
        nav.Set("id", "toc");
        nav.Tail = "\n";
        if (!IsMagazine) HideElement(nav);
        XhtmlUtil.SubElement(nav, "h1").Text = "Navigation";
        CreateNavList(nav, NcxToc, bookPart);

        if (Guide.Count > 0)
        {
            nav = XhtmlUtil.SubElement(body, "nav");
            nav.SetNs(XmlNamespaces.Epub, "type", "landmarks");
            nav.Tail = "\n";
            if (!IsMagazine) HideElement(nav);
            XhtmlUtil.SubElement(nav, "h2").Text = "Guide";
            var ol = XhtmlUtil.SubElement(nav, "ol");
            foreach (var guide in Guide)
            {
                var li = XhtmlUtil.SubElement(ol, "li");
                var a = XhtmlUtil.SubElement(li, "a");
                if (Epub3VocabularyOfGuideType.TryGetValue(guide.GuideType, out var type))
                {
                    a.SetNs(XmlNamespaces.Epub, "type", type);
                }

                a.Set("href", PathUtil.UrlRelativePath(guide.Target, bookPart.Filename));
                a.Text = guide.Title;
            }
        }

        if (Pagemap.Count > 0)
        {
            nav = XhtmlUtil.SubElement(body, "nav");
            nav.SetNs(XmlNamespaces.Epub, "type", "page-list");
            nav.Tail = "\n";
            if (!IsMagazine) HideElement(nav);
            var ol = XhtmlUtil.SubElement(nav, "ol");
            foreach (var page in Pagemap)
            {
                var li = XhtmlUtil.SubElement(ol, "li");
                var a = XhtmlUtil.SubElement(li, "a");
                a.Set("href", PathUtil.UrlRelativePath(page.Target, bookPart.Filename));
                a.Text = page.Label;
            }
        }
    }

    public static void HideElement(XNode elem) => elem.Set("hidden", "");

    // EPUB 3 的 toc nav 必須和 spine 的閱讀順序一致（epubcheck NAV-011）。
    // KFX 的導覽順序有時把目次排在扉頁前，只在輸出 nav 時依 spine 位置穩定排序；
    // NcxToc 本身不改動，避免影響其他用途。
    private List<TocEntry> ReadingOrderToc(List<TocEntry> toc)
    {
        if (toc.Count < 2) return toc;
        var items = new List<(TocEntry Entry, int Original, int Key)>();
        var lastKey = -1;
        for (var i = 0; i < toc.Count; i++)
        {
            var key = SpineIndexOf(toc[i].Target);
            if (key is null) key = lastKey < 0 ? 0 : lastKey;
            lastKey = key.Value;
            items.Add((toc[i], i, key.Value));
        }

        items.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Original.CompareTo(b.Original));
        return items.Select(p => p.Entry).ToList();
    }

    private int? SpineIndexOf(string target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        var filename = PathUtil.RemoveUrlFragment(target);
        var index = BookParts.FindIndex(bp => bp.Filename == filename);
        return index < 0 ? null : index;
    }

    public void CreateNavList(XNode parent, List<TocEntry> toc, BookPart bookPart)
    {
        var ol = XhtmlUtil.SubElement(parent, "ol");
        foreach (var entry in ReadingOrderToc(toc))
        {
            var li = XhtmlUtil.SubElement(ol, "li");
            var a = XhtmlUtil.SubElement(li, "a");
            a.Text = string.IsNullOrWhiteSpace(entry.Title) ? "." : entry.Title;
            if (entry.Target is not null) a.Set("href", PathUtil.UrlRelativePath(entry.Target, bookPart.Filename));
            if (entry.Children.Count > 0) CreateNavList(li, entry.Children, bookPart);
        }
    }

    public byte[] ZipEpub()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var mimetype = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mimetype.Open(), new UTF8Encoding(false)))
            {
                writer.Write("application/epub+zip");
            }

            var container = archive.CreateEntry("META-INF/container.xml", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(container.Open(), new UTF8Encoding(false)))
            {
                writer.Write(ContainerXml());
            }

            foreach (var (filename, file) in OebpsFiles.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(OebpsDir + filename, CompressionLevel.Optimal);
                using var output = entry.Open();
                output.Write(file.BinaryData, 0, file.BinaryData.Length);
            }
        }

        return stream.ToArray();
    }

    public string ContainerXml()
        => "<?xml version=\"1.0\"?>\n<container\n version=\"1.0\"\n xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"\n>\n<rootfiles>\n<rootfile\n full-path=\"" +
           OebpsDir + OpfFilepath + "\"\n media-type=\"application/oebps-package+xml\"\n/>\n</rootfiles>\n</container>\n";

    public string MimetypeOfFilename(string filename, string defaultValue = "application/octet-stream")
    {
        var ext = Path.GetExtension("x" + filename).ToLowerInvariant();
        return ImageUtil.MimetypeOfExt.TryGetValue(ext, out var mime) ? mime : defaultValue;
    }

    public static readonly Dictionary<string, string> DefaultLabelOfGuideType = new()
    {
        ["cover"] = "Cover", ["text"] = "Beginning", ["toc"] = "Table of Contents"
    };

    public static readonly Dictionary<string, int> TocPriorityOfGuideType = new() { ["toc"] = 1, ["text"] = 2, ["cover"] = 3 };
    public static readonly Dictionary<string, string> Epub3VocabularyOfGuideType = new()
    {
        ["cover"] = "cover", ["text"] = "bodymatter", ["toc"] = "toc", ["colophon"] = "colophon"
    };
}
