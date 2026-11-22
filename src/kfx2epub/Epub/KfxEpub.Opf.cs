using System.Globalization;
using System.Text;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

public partial class KfxEpub
{
    public static readonly HashSet<string> ManifestItemProperties = new()
    {
        "cover-image", "mathml", "nav", "remote-resources", "scripted", "svg", "switch"
    };

    public static readonly HashSet<string> SpineItemrefProperties = new()
    {
        "page-spread-left", "page-spread-right", "rendition:align-x-center",
        "rendition:flow-auto", "rendition:flow-paginated", "rendition:flow-scrolled-continuous",
        "rendition:flow-scrolled-doc", "rendition:layout-pre-paginated", "rendition:layout-reflowable",
        "rendition:orientation-auto", "rendition:orientation-landscape", "rendition:orientation-portrait",
        "rendition:page-spread-center", "rendition:spread-auto", "rendition:spread-both",
        "rendition:spread-landscape", "rendition:spread-none", "rendition:spread-portrait",
        "facing-page-left", "facing-page-right", "layout-blank"
    };

    public static readonly Dictionary<string, string> ReservedOpfValuePrefixes = new()
    {
        ["a11y"] = "http://www.idpf.org/epub/vocab/package/a11y/#",
        ["dcterms"] = "http://purl.org/dc/terms/",
        ["marc"] = "http://id.loc.gov/vocabulary/",
        ["media"] = "http://www.idpf.org/epub/vocab/overlays/#",
        ["onix"] = "http://www.editeur.org/ONIX/book/codelists/current.html#",
        ["rendition"] = "http://www.idpf.org/vocab/rendition/#",
        ["schema"] = "http://schema.org/",
        ["xsd"] = "http://www.w3.org/2001/XMLSchema#"
    };

    public void CreateOpf()
    {
        var uidValue = Uid;
        var authors = Authors.Count > 0 ? Authors : new List<string> { "Unknown" };
        var usedPrefixes = new HashSet<string>();
        var metaLines = new List<string>();

        metaLines.Add($"<dc:title id=\"title\">{Escape(Title)}</dc:title>");
        if (!string.IsNullOrEmpty(TitlePronunciation))
        {
            metaLines.Add($"<meta refines=\"#title\" property=\"file-as\">{Escape(TitlePronunciation)}</meta>");
        }

        var creatorBlocks = new List<List<string>>();
        for (var i = 0; i < authors.Count; i++)
        {
            var block = new List<string>();
            var creatorId = $"creator{i + 1:00}";
            block.Add($"<dc:creator id=\"{creatorId}\">{Escape(authors[i])}</dc:creator>");
            usedPrefixes.Add("marc");
            block.Add($"<meta refines=\"#{creatorId}\" property=\"role\" scheme=\"marc:relators\">aut</meta>");
            if (i < AuthorPronunciations.Count)
            {
                block.Add($"<meta refines=\"#{creatorId}\" property=\"file-as\">{Escape(AuthorPronunciations[i])}</meta>");
            }

            creatorBlocks.Add(block);
        }

        var metaOther = new List<string>();
        if (!string.IsNullOrEmpty(Publisher)) metaOther.Add($"<dc:publisher id=\"publisher\">{Escape(Publisher)}</dc:publisher>");
        metaOther.Add($"<dc:language>{Escape(string.IsNullOrEmpty(Language) ? "en" : Language)}</dc:language>");
        if (!string.IsNullOrEmpty(Pubdate)) metaOther.Add($"<dc:date>{Escape(Pubdate.Length > 10 ? Pubdate[..10] : Pubdate)}</dc:date>");
        if (!string.IsNullOrEmpty(DescriptionText)) metaOther.Add($"<dc:description>{Escape(DescriptionText)}</dc:description>");
        if (!string.IsNullOrEmpty(Subject)) metaOther.Add($"<dc:subject>{Escape(Subject)}</dc:subject>");
        if (!string.IsNullOrEmpty(Rights)) metaOther.Add($"<dc:rights>{Escape(Rights)}</dc:rights>");

        var modifiedTime = Environment.GetEnvironmentVariable("KFX2EPUB_MODIFIED_TIME")
                           ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss") + "Z";
        metaOther.Add($"<meta property=\"dcterms:modified\">{modifiedTime}</meta>");
        metaOther.Add("<meta property=\"ebpaj:guide-version\">1.1.4</meta>");
        metaOther.Add("<meta property=\"dpfj:guide-version\">1.1.4</meta>");

        if (FixedLayout)
        {
            usedPrefixes.Add("rendition");
            metaOther.Add("<meta property=\"rendition:layout\">pre-paginated</meta>");
            metaOther.Add("<meta name=\"fixed-layout\" content=\"true\"/>");
            if (OriginalWidth is not null && OriginalHeight is not null)
            {
                if (OrientationLock == "none") OrientationLock = OriginalWidth > OriginalHeight ? "landscape" : "portrait";
                metaOther.Add($"<meta name=\"original-resolution\" content=\"{OriginalWidth}x{OriginalHeight}\"/>");
            }
        }

        if (ScrolledContinuous)
        {
            usedPrefixes.Add("rendition");
            metaOther.Add("<meta property=\"rendition:flow\">scrolled-continuous</meta>");
        }

        if (BookType is "children" or "comic") metaOther.Add($"<meta name=\"book-type\" content=\"{BookType}\"/>");
        if (OrientationLock != "none")
        {
            usedPrefixes.Add("rendition");
            metaOther.Add($"<meta property=\"rendition:orientation\">{OrientationLock}</meta>");
            metaOther.Add($"<meta name=\"orientation-lock\" content=\"{OrientationLock}\"/>");
        }

        if (OverrideKindleFont) metaOther.Add("<meta name=\"Override-Kindle-Fonts\" content=\"true\"/>");
        if (WritingMode != "horizontal-tb") metaOther.Add($"<meta name=\"primary-writing-mode\" content=\"{WritingMode}\"/>");
        else if (PageProgressionDirection == "rtl") metaOther.Add("<meta name=\"primary-writing-mode\" content=\"horizontal-rl\"/>");
        if (RegionMagnification) metaOther.Add("<meta name=\"RegionMagnification\" content=\"true\"/>");
        if (IllustratedLayout) metaOther.Add("<meta name=\"amzn:kindle-illustrated\" content=\"true\"/>");
        if (HtmlCover) metaOther.Add("<meta name=\"amzn:cover-as-html\" content=\"true\"/>");
        if (GuidedViewNative) metaOther.Add("<meta name=\"amzn:guided-view-native\" content=\"true\"/>");
        if (MinAspectRatio is not null) metaOther.Add($"<meta name=\"amzn:min-aspect-ratio\" content=\"{TextUtil.ValueStr(MinAspectRatio)}\"/>");
        if (MaxAspectRatio is not null) metaOther.Add($"<meta name=\"amzn:max-aspect-ratio\" content=\"{TextUtil.ValueStr(MaxAspectRatio)}\"/>");

        if (IsDictionary)
        {
            metaOther.Add($"<x-metadata><DictionaryInLanguage>{Escape(SourceLanguage)}</DictionaryInLanguage><DictionaryOutLanguage>{Escape(TargetLanguage)}</DictionaryOutLanguage></x-metadata>");
        }

        var manifestItems = new List<(string Category, string Line, string Filename)>();
        var tocIdref = (string)null;
        var hasPageSpread = false;
        foreach (var entry in Manifest.OrderBy(m => m.Filename, StringComparer.Ordinal))
        {
            if (entry.Filename == NcxLocation || entry.Filename.EndsWith(".ncx")) tocIdref = entry.Id;
            var mimetype = entry.External ? MimetypeOfFilename(entry.Filename) : OebpsFiles[entry.Filename].Mimetype;
            var href = entry.External ? entry.Filename : PathUtil.UrlRelativePath(entry.Filename, OpfFilepath);

            if (entry.IsCoverImage && !(HtmlCover || IsComic))
            {
                metaOther.Add($"<meta name=\"cover\" content=\"{entry.Id}\"/>");
            }

            if (FixedLayout)
            {
                if (entry.IsFxl) entry.Properties.Remove("rendition:layout-pre-paginated");
                else entry.Properties.Add("rendition:layout-reflowable");
                if (entry.Properties.Contains("page-spread-left") || entry.Properties.Contains("page-spread-right")) hasPageSpread = true;
            }

            var properties = entry.Properties.Intersect(ManifestItemProperties).OrderBy(p => p, StringComparer.Ordinal).ToList();
            var propertyAttr = properties.Count > 0 ? $" properties=\"{string.Join(" ", properties)}\"" : "";
            manifestItems.Add((OpfManifestCategory(href), $"<item media-type=\"{Escape(mimetype)}\" id=\"{entry.Id}\" href=\"{Escape(href)}\"{propertyAttr}/>", entry.Filename));
        }

        var spineLines = new List<string>();
        foreach (var entry in Manifest)
        {
            if (entry.Linear is null) continue;
            var props = entry.Properties.Intersect(SpineItemrefProperties).ToHashSet();
            if (IsComic && hasPageSpread && props.Count == 0) props.Add("rendition:page-spread-center");
            var propAttr = props.Count > 0 ? $" properties=\"{string.Join(" ", props.OrderBy(p => p, StringComparer.Ordinal))}\"" : "";
            var linearAttr = entry.Linear == false ? " linear=\"no\"" : "";
            spineLines.Add($"<itemref idref=\"{entry.Id}\"{linearAttr}{propAttr}/>");
        }

        var metadata = new StringBuilder();
        metadata.AppendLine("<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">");
        metadata.AppendLine();
        metadata.AppendLine("<!-- 作品名 -->");
        metadata.AppendLine(metaLines[0]);
        if (!string.IsNullOrEmpty(TitlePronunciation) && metaLines.Count > 1) metadata.AppendLine(metaLines[1]);
        metadata.AppendLine();
        metadata.AppendLine("<!-- 著者名 -->");
        for (var i = 0; i < creatorBlocks.Count; i++)
        {
            if (i > 0) metadata.AppendLine();
            foreach (var line in creatorBlocks[i]) metadata.AppendLine(line);
        }

        metadata.AppendLine();
        metadata.AppendLine("<!-- 出版社名 -->");
        var publisherLine = metaOther.FirstOrDefault(l => l.StartsWith("<dc:publisher"));
        if (publisherLine is not null) metadata.AppendLine(publisherLine);
        metadata.AppendLine();
        metadata.AppendLine("<!-- 言語 -->");
        var languageLine = metaOther.FirstOrDefault(l => l.StartsWith("<dc:language"));
        if (languageLine is not null) metadata.AppendLine(languageLine);
        metadata.AppendLine();
        metadata.AppendLine("<!-- ファイルid -->");
        metadata.AppendLine($"<dc:identifier id=\"ASIN\">{Escape(Uid)}</dc:identifier>");
        metadata.AppendLine();
        metadata.AppendLine("<!-- 更新日 -->");
        var modifiedLine = metaOther.FirstOrDefault(l => l.Contains("dcterms:modified"));
        if (modifiedLine is not null) metadata.AppendLine(modifiedLine);
        metadata.AppendLine();
        metadata.AppendLine("<!-- etc. -->");
        foreach (var line in metaOther.Where(l => l != publisherLine && l != languageLine && l != modifiedLine))
            metadata.AppendLine(line);
        metadata.AppendLine();
        metadata.AppendLine("</metadata>");

        var manifest = new StringBuilder();
        manifest.AppendLine("<manifest>");
        manifest.AppendLine();
        foreach (var group in new[] { "navigation", "style", "image", "xhtml", "font", "misc" })
        {
            var items = manifestItems.Where(i => i.Category == group).ToList();
            if (items.Count == 0) continue;
            manifest.AppendLine($"<!-- {group} -->");
            foreach (var item in items) manifest.AppendLine(item.Line);
            manifest.AppendLine();
        }

        manifest.AppendLine("</manifest>");

        var spine = new StringBuilder();
        var spineAttrs = PageProgressionDirection != "ltr" ? $" page-progression-direction=\"{PageProgressionDirection}\"" : "";
        spine.AppendLine($"<spine{spineAttrs}>");
        spine.AppendLine();
        foreach (var line in spineLines) spine.AppendLine(line);
        spine.AppendLine();
        spine.AppendLine("</spine>");

        if (usedPrefixes.Contains("rendition")) { }
        var prefixItems = new List<string>
        {
            "ebpaj: http://www.ebpaj.jp/", "dpfj: https://www.dpfj.or.jp/"
        };
        foreach (var prefix in usedPrefixes.Where(p => p is not ("ebpaj" or "dpfj")).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (ReservedOpfValuePrefixes.TryGetValue(prefix, out var uri)) prefixItems.Add(prefix + ": " + uri);
        }

        var opf = new StringBuilder();
        opf.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        opf.AppendLine("<package");
        opf.AppendLine(" xmlns=\"http://www.idpf.org/2007/opf\"");
        opf.AppendLine(" version=\"3.0\"");
        opf.AppendLine($" xml:lang=\"{Escape(string.IsNullOrEmpty(Language) ? "ja" : Language)}\"");
        opf.AppendLine(" unique-identifier=\"ASIN\"");
        if (prefixItems.Count > 0)
        {
            opf.AppendLine(" prefix=\"" + string.Join("\n         ", prefixItems.Select(Escape)) + "\"");
        }

        opf.AppendLine(">");
        opf.AppendLine();
        opf.Append(metadata);
        opf.AppendLine();
        opf.Append(manifest);
        opf.AppendLine();
        opf.Append(spine);
        opf.AppendLine();
        opf.AppendLine("</package>");

        AddOebpsFile(OpfFilepath, Encoding.UTF8.GetBytes(opf.ToString().Replace("\r\n", "\n")), "application/oebps-package+xml");
    }

    public static string OpfManifestCategory(string href)
    {
        if (href.StartsWith("navigation-documents")) return "navigation";
        if (href.StartsWith("style/")) return "style";
        if (href.StartsWith("image/")) return "image";
        if (href.StartsWith("xhtml/")) return "xhtml";
        if (href.StartsWith("fonts/")) return "font";
        return "misc";
    }

    public static string Escape(string value)
        => (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string XmlEscape(string value) => value?.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
