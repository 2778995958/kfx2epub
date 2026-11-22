using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

public partial class KfxEpub
{
    public List<IonStruct> ReadingOrders { get; private set; } = new();
    public string NmdlTemplateId { get; set; }
    public string DictionaryAuxDataId { get; set; }
    public string CoverResource { get; set; }
    public bool IsPrintReplica { get; set; }
    public bool IsPdfBacked { get; set; }
    public bool IsPdfBackedFixedLayout { get; set; }
    public string CdeContentType { get; set; }
    public string DefaultFontFamily { get; set; } = "serif";
    public string DefaultFontSize { get; set; } = "1em";
    public string DefaultLineHeight { get; set; } = "normal";
    public bool HasConditionalContent { get; set; }
    public List<string> ConditionOperatorNames { get; } = new();

    public static readonly Dictionary<string, string> MetadataSymbols = new()
    {
        ["ASIN"] = "$224", ["asset_id"] = "$466", ["author"] = "$222", ["cde_content_type"] = "$251",
        ["cover_image"] = "$424", ["description"] = "$154", ["language"] = "$10", ["orientation"] = "$215",
        ["publisher"] = "$232", ["reading_orders"] = "$169", ["support_landscape"] = "$218",
        ["support_portrait"] = "$217", ["title"] = "$153"
    };

    public static readonly Dictionary<string, string> MetadataNames =
        MetadataSymbols.ToDictionary(p => p.Value, p => p.Key);

    public void ProcessDocumentData()
    {
        if (BookData.TryGetValue("$538", out var docValue) && docValue is IonStruct documentData)
        {
            if (documentData.ContainsKey("$433"))
            {
                var orientation = documentData.Pop("$433").ToStr();
                OrientationLock = orientation switch { "$385" => "portrait", "$386" => "landscape", _ => "none" };
            }

            documentData.Pop("$436");
            documentData.Pop("$477");
            documentData.Pop("$581");

            if (documentData.ContainsKey("$665"))
            {
                SetBookType("comic");
                documentData.Pop("$665");
            }

            documentData.Pop("$668");

            if (documentData.Pop("$597") is IonStruct auxData)
            {
                foreach (var pair in auxData)
                {
                    if (pair.Key == "$697") DictionaryAuxDataId = pair.Value.ToStr();
                }
            }

            foreach (var key in new[]
                     {
                         "yj.semantics.book_theme_metadata", "yj.semantics.containers_with_semantics",
                         "yj.semantics.page_number_begin", "yj.print.settings",
                         "yj.authoring.auto_panel_settings_auto_mask_color_flag",
                         "yj.authoring.auto_panel_settings_mask_color", "yj.authoring.auto_panel_settings_opacity",
                         "yj.authoring.auto_panel_settings_padding_bottom", "yj.authoring.auto_panel_settings_padding_left",
                         "yj.authoring.auto_panel_settings_padding_right", "yj.authoring.auto_panel_settings_padding_top",
                         "yj.dictionary.text", "yj.conversion.source_attr_width"
                     })
            {
                documentData.Pop(key);
            }

            ReadingOrders = (documentData.Pop("$169", new List<object>()) as List<object>)
                ?.OfType<IonStruct>().ToList() ?? new List<IonStruct>();
            NmdlTemplateId = documentData.Pop("nmdl.template_id").ToStr();

            if (documentData.ContainsKey("max_id")) documentData.Pop("max_id");

            FontNameReplacements["default"] = "serif";
            var docStyle = ProcessContentProperties(documentData);
            docStyle.Pop("column-count", "auto");
            PageProgressionDirection = docStyle.Pop("direction", "ltr") ?? "ltr";
            DefaultFontFamily = docStyle.Pop("font-family", "serif") ?? "serif";
            foreach (var defaultName in new[] { "default", "$amzn_fixup_default_font$" })
            {
                foreach (var family in DefaultFontFamily.Split(','))
                {
                    FontNameReplacements[defaultName] = StripFontName(family);
                }
            }

            DefaultFontSize = docStyle.Pop("font-size", "1em") ?? "1em";
            DefaultLineHeight = docStyle.Pop("line-height", "normal") ?? "normal";
            WritingMode = docStyle.Pop("writing-mode", "horizontal-tb") ?? "horizontal-tb";
            if (WritingMode.EndsWith("-rl")) PageProgressionDirection = "rtl";
            CheckEmpty(docStyle.Properties, "document data styles");
            CheckEmpty(documentData, "$538");
            BookData.Remove("$538");
        }
        else
        {
            PageProgressionDirection = "ltr";
        }
    }

    public void ProcessContentFeatures()
    {
        if (BookData.TryGetValue("$585", out var cf) && cf is IonStruct contentFeatures)
        {
            BookData.Remove("$585");
            contentFeatures.Pop("$590");
            contentFeatures.Pop("$598");
            contentFeatures.Pop("$155");
            CheckEmpty(contentFeatures, "$585");
        }
    }

    public void ProcessMetadata()
    {
        if (BookData.TryGetValue("$490", out var mdValue) && mdValue is IonStruct bookMetadata)
        {
            BookData.Remove("$490");
            if (bookMetadata.Pop("$491") is List<object> categories)
            {
                foreach (var category in categories.OfType<IonStruct>())
                {
                    var cat = category.Pop("$495").ToStr() ?? "";
                    foreach (var metadata in (List<object>)category.Pop("$258"))
                    {
                        if (metadata is not IonStruct md) continue;
                        var key = md.Pop("$492").ToStr() ?? "";
                        ProcessMetadataItem(cat, key, md.Pop("$307"));
                        CheckEmpty(md, $"categorised_metadata {cat}/{key}");
                    }
                }
            }
        }

        if (BookData.TryGetValue("$258", out var itemValue) && itemValue is IonStruct metadataItems)
        {
            BookData.Remove("$258");
            foreach (var (key, value) in metadataItems.ToList())
            {
                ProcessMetadataItem("", MetadataNames.GetValueOrDefault(key, key), value);
            }
        }

        if (BookType is null && FixedLayout && (VirtualPanelsAllowed || !IsPrintReplica))
        {
            SetBookType("comic");
            if (!RegionMagnification) VirtualPanelsAllowed = true;
        }
    }

    public void ProcessMetadataItem(string category, string key, object value)
    {
        var catKey = string.IsNullOrEmpty(category) ? key : category + "/" + key;
        switch (catKey)
        {
            case "kindle_title_metadata/ASIN":
            case "ASIN":
                if (string.IsNullOrEmpty(Asin)) Asin = value.ToStr() ?? "";
                break;
            case "kindle_title_metadata/author":
                // 依 KFX 原始順序保留：第一位是主要作者，後續為插畫等協力者。
                if (value is string author && author.Length > 0) Authors.Add(author);
                break;
            case "kindle_title_metadata/author_pronunciation":
                if (value is string pron && pron.Length > 0) AuthorPronunciations.Add(pron);
                break;
            case "author":
                if (Authors.Count == 0 && value is string authors)
                {
                    Authors = authors.Split('&').Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
                }
                break;
            case "kindle_title_metadata/book_id":
                BookId = value.ToStr() ?? "";
                break;
            case "kindle_title_metadata/cde_content_type":
            case "cde_content_type":
                CdeContentType = value.ToStr() ?? "";
                if (CdeContentType == "MAGZ") SetBookType("magazine");
                else if (CdeContentType == "EBSP") IsSample = true;
                break;
            case "kindle_title_metadata/description":
            case "description":
                DescriptionText = (value.ToStr() ?? "").Trim();
                break;
            case "kindle_title_metadata/cover_image":
            case "cover_image":
                CoverResource = value.ToStr();
                break;
            case "kindle_title_metadata/dictionary_lookup":
                IsDictionary = true;
                if (value is IonStruct dict)
                {
                    SourceLanguage = dict.Pop("$474").ToStr() ?? "";
                    TargetLanguage = dict.Pop("$163").ToStr() ?? "";
                }
                break;
            case "kindle_title_metadata/issue_date":
                Pubdate = value.ToStr() ?? "";
                break;
            case "kindle_title_metadata/language":
            case "language":
                Language = FixLanguage(value.ToStr() ?? "");
                break;
            case "kindle_title_metadata/periodicals_generation_V2":
                SetBookType("magazine");
                VirtualPanelsAllowed = true;
                break;
            case "kindle_title_metadata/publisher":
            case "publisher":
                Publisher = (value.ToStr() ?? "").Trim();
                break;
            case "kindle_title_metadata/title":
            case "title":
                if (string.IsNullOrEmpty(Title)) Title = (value.ToStr() ?? "").Trim();
                break;
            case "kindle_title_metadata/title_pronunciation":
                if (string.IsNullOrEmpty(TitlePronunciation)) TitlePronunciation = (value.ToStr() ?? "").Trim();
                break;
            case "kindle_ebook_metadata/book_orientation_lock":
                OrientationLock = value.ToStr() ?? OrientationLock;
                break;
            case "kindle_title_metadata/is_dictionary":
                IsDictionary = value is bool b && b;
                break;
            case "kindle_title_metadata/is_sample":
                IsSample = value is bool sb && sb;
                break;
            case "kindle_title_metadata/override_kindle_font":
                OverrideKindleFont = value is bool ob && ob;
                break;
            case "kindle_capability_metadata/continuous_popup_progression":
                VirtualPanelsAllowed = true;
                if (value is int cpp && cpp == 0) SetBookType("comic");
                else if (value is int cpp1 && cpp1 == 1) SetBookType("children");
                break;
            case "kindle_capability_metadata/yj_fixed_layout":
                FixedLayout = true;
                if (value is int fxl)
                {
                    if (fxl == 2)
                    {
                        IsPdfBacked = true;
                        IsPrintReplica = true;
                    }
                    else if (fxl == 3)
                    {
                        IsPdfBacked = true;
                        IsPdfBackedFixedLayout = true;
                        VirtualPanelsAllowed = true;
                    }
                }
                break;
            case "kindle_capability_metadata/yj_forced_continuous_scroll":
                ScrolledContinuous = true;
                break;
            case "kindle_capability_metadata/yj_guided_view_native":
                GuidedViewNative = true;
                break;
            case "kindle_capability_metadata/yj_has_text_popups":
                SetBookType("children");
                RegionMagnification = true;
                break;
            case "kindle_capability_metadata/yj_publisher_panels":
                SetBookType("comic");
                if (value is int pp && pp == 0) VirtualPanelsAllowed = true;
                else RegionMagnification = true;
                break;
            case "kindle_capability_metadata/yj_facing_page":
            case "kindle_capability_metadata/yj_double_page_spread":
                SetBookType("comic");
                break;
            case "kindle_capability_metadata/yj_textbook":
                IsPdfBacked = true;
                IsPrintReplica = true;
                break;
            case "kindle_capability_metadata/yj_illustrated_layout":
                IllustratedLayout = HtmlCover = true;
                break;
            case "reading_orders":
                if (ReadingOrders.Count == 0 && value is List<object> orders)
                {
                    ReadingOrders = orders.OfType<IonStruct>().ToList();
                }
                break;
            case "support_landscape":
                if (value is false && OrientationLock == "none") OrientationLock = "portrait";
                break;
            case "support_portrait":
                if (value is false && OrientationLock == "none") OrientationLock = "landscape";
                break;
        }
    }

    public object GetMetadataValue(string name, string category = "kindle_title_metadata", object defaultValue = null)
    {
        var fragment = Book.Fragments.Get("$490");
        if (fragment?.Value is IonStruct metadata)
        {
            foreach (var cm in metadata.GetValueOrDefault("$491") as List<object> ?? new List<object>())
            {
                if (cm is not IonStruct cat || cat.GetValueOrDefault("$495").ToStr() != category) continue;
                foreach (var kv in cat.GetValueOrDefault("$258") as List<object> ?? new List<object>())
                {
                    if (kv is IonStruct item && item.GetValueOrDefault("$492").ToStr() == name) return item.GetValueOrDefault("$307");
                }
            }
        }

        if (MetadataSymbols.TryGetValue(name, out var symbol))
        {
            var simple = Book.Fragments.Get("$258");
            if (simple?.Value is IonStruct values && values.TryGetValue(symbol, out var value)) return value;
        }

        return defaultValue;
    }

    public object GetFeatureValue(string feature, string ns = "com.amazon.yjconversion", object defaultValue = null)
    {
        if (ns == "format_capabilities")
        {
            var fragment = Book.Fragments.Get("$593", first: true);
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
            var fragment = Book.Fragments.Get("$585", first: true);
            if (fragment?.Value is IonStruct contentFeatures)
            {
                foreach (var cf in contentFeatures.GetValueOrDefault("$590") as List<object> ?? new List<object>())
                {
                    if (cf is not IonStruct featureStruct || featureStruct.GetValueOrDefault("$586").ToStr() != ns ||
                        featureStruct.GetValueOrDefault("$492").ToStr() != feature) continue;
                    var versionInfo = featureStruct.GetValueOrDefault("$589") as IonStruct;
                    var version = versionInfo?.GetValueOrDefault("version") as IonStruct;
                    var major = Convert.ToInt32(version?.GetValueOrDefault("$587", 0) ?? 0);
                    var minor = Convert.ToInt32(version?.GetValueOrDefault("$588", 0) ?? 0);
                    return minor == 0 ? major : (major, minor);
                }
            }
        }

        return defaultValue;
    }

    public bool IsFixedLayout
        => Book.IsScribeNotebook ||
           GetMetadataValue("yj_fixed_layout", "kindle_capability_metadata") is not null;

    public bool IsIllustratedLayout => GetFeatureValue("yj.illustrated_layout") is not null;

    public bool HasIllustratedLayoutConditionalPageTemplate
        => GetFeatureValue("yj.conditional_structure") is not null ||
           (Convert.ToInt32(GetFeatureValue("reflow-style", defaultValue: 0)) == 5 && !IsMagazine) ||
           Book.HasIllustratedLayoutPageTemplateCondition();
}
