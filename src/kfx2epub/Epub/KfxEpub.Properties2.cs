using System.Globalization;
using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

public partial class KfxEpub
{
    public bool TextCombineInUse { get; set; }
    public HashSet<string> UsedKfxStyles { get; } = new();
    public HashSet<string> MissingKfxStyles { get; } = new();
    public HashSet<string> UsedDictionaryRules { get; } = new();
    public Dictionary<string, string> DictionaryRules { get; set; } = new();
    public HashSet<string> MissingSpecialClasses { get; } = new();
    public HashSet<string> VerticalRlClasses { get; set; } = new();

    // KFX 以區塊方向的 lh margin 表示 bw 的空段落。這裡先標記，
    // 等 SimplifyStyles 決定元素的最終標籤後，再插入 <p><br/></p>。
    private const string BlankBeforeProperty = "-kfx-blank-before";
    private const string BlankAfterProperty = "-kfx-blank-after";
    private const string BlankOrigBeforeProperty = "-kfx-blank-orig-before";
    private const string BlankOrigAfterProperty = "-kfx-blank-orig-after";
    private const string BlankPropBeforeProperty = "-kfx-blank-prop-before";
    private const string BlankPropAfterProperty = "-kfx-blank-prop-after";
    private const decimal BlankLineRem = 1.05m;

    private static readonly HashSet<string> BoxPaddingProperties = new()
    {
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left"
    };

    private string _pageWritingMode = "horizontal-tb";

    public void FixupStylesAndClasses()
    {
        CssRules = new Dictionary<string, Style>();
        NonHeritableDefaultProperties = new Style(PropertyData.NonHeritableDefaultProperties
            .Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));

        foreach (var bookPart in BookParts)
        {
            LastKfxHeadingLevel = "1";
            var heritable = new Style(PropertyData.HeritableDefaultProperties
                .Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));
            var lang = bookPart.Html.GetNs(XmlNamespaces.Xml, "lang");
            if (!string.IsNullOrEmpty(lang))
            {
                heritable.Update(new Style(new Dictionary<string, string> { ["-kfx-attrib-xml-lang"] = lang }), true);
            }

            _pageWritingMode = GetStyle(bookPart.Body()).Get("writing-mode", WritingMode) ?? WritingMode;
            SimplifyStyles(bookPart.Body(), bookPart, heritable);
            ExpandBlockMargins(bookPart);
            AddCompositeAndEquivalentStyles(bookPart.Body(), bookPart);
        }

        CreateConditionalPageTemplates();

        var styleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bookPart in BookParts)
        {
            foreach (var e in bookPart.Body().DescendantsAndSelf())
            {
                if (e.Has("style"))
                {
                    var style = GetStyle(e);
                    var styleAttribs = style.Partition(namePrefix: "-kfx-attrib-", removePrefix: true);
                    var modified = styleAttribs.Count > 0;
                    foreach (var (name, value) in styleAttribs.Properties)
                    {
                        string attrName = name;
                        if (name.StartsWith("epub-"))
                        {
                            if (GenerateEpub2) continue;
                            attrName = XNode.AttrKey(XmlNamespaces.Epub, name[5..]);
                        }
                        else if (name.StartsWith("xml-")) attrName = XNode.AttrKey(XmlNamespaces.Xml, name[4..]);

                        e.Attributes[attrName] = value;
                    }

                    if (modified) SetStyle(e, style);

                    if (style.Get("writing-mode") == "vertical-rl") bookPart.HasVerticalRlClass = true;

                    if (style.Get("direction") is not null || style.Get("unicode-bidi") is not null)
                    {
                        var unicodeBidi = style.Get("unicode-bidi", "normal");
                        var hasBlock = false;
                        var hasContent = !string.IsNullOrEmpty(e.Text);
                        foreach (var ex in e.Descendants())
                        {
                            if (ex.IsLocal("aside") || ex.IsLocal("div") || ex.IsLocal("figure") ||
                                Regex.IsMatch(ex.Name, "^h[1-6]$") || ex.IsLocal("iframe") || ex.IsLocal("li") ||
                                ex.IsLocal("ol") || ex.IsLocal("p") || ex.IsLocal("table") || ex.IsLocal("td") ||
                                ex.IsLocal("ul")) hasBlock = true;
                            if (!string.IsNullOrEmpty(ex.Text) || !string.IsNullOrEmpty(ex.Tail) ||
                                ex.IsLocal("audio") || ex.IsLocal("img") || ex.IsLocal("li") ||
                                ex.IsLocal("object") || ex.IsLocal("video") || ex.IsNs(XmlNamespaces.Svg, "svg"))
                                hasContent = true;
                            if (hasBlock && hasContent) break;
                        }

                        if (!hasContent)
                        {
                            style.Pop("direction");
                            style.Pop("unicode-bidi");
                            SetStyle(e, style);
                        }
                        else if (unicodeBidi is "embed" or "normal" || hasBlock)
                        {
                            if (style.Get("direction") is not null) e.Set("dir", style.Pop("direction"));
                            style.Pop("unicode-bidi");
                            SetStyle(e, style);
                        }
                        else if (unicodeBidi is "isolate" or "bidi-override" or "isolate-override")
                        {
                            var wrapper = new XNode(unicodeBidi.Contains("override") ? "bdo" : "bdi");
                            if (style.Get("direction") is not null) wrapper.Set("dir", style.Pop("direction"));
                            if (!e.IsLocal("img"))
                            {
                                wrapper.Text = e.Text;
                                e.Text = null;
                                foreach (var child in e.Children.ToList())
                                {
                                    e.RemoveChild(child);
                                    wrapper.Append(child);
                                }

                                e.Insert(0, wrapper);
                            }

                            style.Pop("unicode-bidi");
                            SetStyle(e, style);
                        }
                    }

                    if (style.Count > 0 && style.Partition(new[] { "-kfx-style-name", "-kfx-layout-hints" }, keepAll: true, modify: false).Count == 0)
                    {
                        style.Pop("-kfx-style-name");
                        style.Pop("-kfx-layout-hints");
                        SetStyle(e, style);
                    }
                }

                if (e.Has("style")) StripResetDefaultProperties(e);
                if (e.Has("style")) styleCounts[e.Get("style")] = styleCounts.GetValueOrDefault(e.Get("style")) + 1;
            }
        }

        var knownClassCount = new Dictionary<string, int>(StringComparer.Ordinal);
        knownClassCount["app-amzn-magnify"] = 2;
        var sortedStyles = new List<(string StyleStr, Style Style, string ClassName)>();
        foreach (var (styleStr, count) in styleCounts.OrderByDescending(p => p.Value))
        {
            var style = new Style(styleStr);
            var className = Regex.Replace(style.Pop("-kfx-style-name", ""), @"[^A-Za-z0-9_-]", "_");
            var prefix = style.Get("-kfx-layout-hints") is not null
                ? string.Join("-", style.Pop("-kfx-layout-hints").Split(' ').OrderBy(v => v, StringComparer.Ordinal))
                : "class";
            knownClassCount[prefix] = knownClassCount.GetValueOrDefault(prefix) + 1;
            className = PrefixUniquePartOfSymbol(className, prefix);
            knownClassCount[className] = knownClassCount.GetValueOrDefault(className) + 1;
            sortedStyles.Add((styleStr, style, className));
        }

        var classes = new Dictionary<string, Style>();
        var styleClassNames = new Dictionary<string, string>();
        var usedClassNameCount = new Dictionary<string, int>();
        var referencedClasses = new HashSet<string>();
        var selectorClasses = new HashSet<string>();

        foreach (var (styleStr, style, originalClassName) in sortedStyles)
        {
            var className = originalClassName;
            if (knownClassCount.GetValueOrDefault(className) > 1 || classes.ContainsKey(className))
            {
                while (true)
                {
                    var unique = usedClassNameCount.GetValueOrDefault(className);
                    usedClassNameCount[className] = unique + 1;
                    className = className + "-" + unique;
                    if (knownClassCount.GetValueOrDefault(className) == 0 && !classes.ContainsKey(className)) break;
                }
            }

            foreach (var (prefix, suffix) in new[]
                     {
                         ("-kfx-firstline-", "::first-line"), ("-kfx-link-", ":link"), ("-kfx-visited-", ":visited")
                     })
            {
                var selectorStyle = style.Partition(namePrefix: prefix, removePrefix: true);
                if (selectorStyle.Count > 0)
                {
                    CssRules[ClassSelector(className) + suffix] = selectorStyle;
                    selectorClasses.Add(className);
                }
            }

            classes[className] = style;
            styleClassNames[styleStr] = className;
        }

        PruneRedundantClassProperties(sortedStyles, styleClassNames);

        // 記錄轉換前的樣式，供 fixed-layout 頁面直接內嵌（那些頁面不套用樣式表）。
        var originalStyles = classes.ToDictionary(p => p.Key, p => new Style(p.Value), StringComparer.Ordinal);
        var classUtilities = MapClassStylesToUtility(classes);

        foreach (var bookPart in BookParts)
        {
            foreach (var e in bookPart.Body().DescendantsAndSelf())
            {
                var styleStr = e.Get("style", "");
                if (!styleClassNames.TryGetValue(styleStr, out var className)) continue;
                var classStyle = classes[className];
                if (bookPart.IsFxl && !selectorClasses.Contains(className) && !classStyle.ContainsKey("-kfx-media-query"))
                {
                    e.Set("style", originalStyles[className].ToString());
                    continue;
                }

                if (classUtilities.TryGetValue(className, out var utilities))
                {
                    foreach (var utility in utilities) AddClass(e, utility);
                }

                if (classStyle.Count == 0 && !selectorClasses.Contains(className)) e.Pop("style");
                else
                {
                    AddClass(e, className, before: true);
                    referencedClasses.Add(className);
                    e.Pop("style");
                }
            }
        }

        foreach (var (className, classStyle) in classes)
        {
            if (!referencedClasses.Contains(className)) continue;
            var mediaQuery = classStyle.Pop("-kfx-media-query");
            if (classStyle.Count == 0) continue;
            var target = mediaQuery is not null
                ? MediaQueries.GetValueOrDefault(mediaQuery) ?? (MediaQueries[mediaQuery] = new Dictionary<string, Style>())
                : CssRules;
            target[ClassSelector(className)] = classStyle;
        }

        UpdateVerticalRlClassUsage();
    }

    // 電書協樣式表中實際存在的 utility class（字級百分比、寬高尺寸）。
    private static readonly HashSet<string> UtilityFontPercents = new(StringComparer.Ordinal)
    {
        "050", "060", "070", "075", "080", "085", "090", "100", "110", "115", "120",
        "130", "140", "150", "160", "170", "180", "190", "200", "250", "300"
    };

    private static readonly HashSet<string> UtilityFontEmSizes = new(StringComparer.Ordinal)
    {
        "0em50", "0em60", "0em70", "0em75", "0em80", "0em85", "0em90", "1em", "1em10",
        "1em15", "1em20", "1em30", "1em40", "1em50", "1em60", "1em70", "1em80", "1em90",
        "2em", "2em50", "3em"
    };

    private static readonly HashSet<string> UtilityBoxPercents = new(StringComparer.Ordinal)
    {
        "010", "020", "025", "030", "033", "040", "050", "060", "067", "070", "075",
        "080", "090", "100"
    };

    private static readonly HashSet<string> UtilityBoxEmSizes = new(StringComparer.Ordinal)
    {
        "0em25", "0em50", "0em75", "1em", "1em25", "1em50", "1em75", "2em", "2em50",
        "3em", "4em", "5em", "5em25", "6em", "7em", "8em", "8em75", "9em", "10em",
        "11em", "12em", "13em", "14em", "15em", "20em", "30em", "40em"
    };

    private sealed record UtilityMapping(string ClassName, string ExtraDeclaration = null);

    // 把可以完整表達的宣告換成電書協 utility class，剩下的宣告留在原規則。
    // 與 kfxlib 的 yj_to_epub_properties.py 保持相同行為。
    private Dictionary<string, List<string>> MapClassStylesToUtility(Dictionary<string, Style> classes)
    {
        var classUtilities = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (className, style) in classes)
        {
            List<string> utilities = null;
            foreach (var name in style.Properties.Keys.ToList())
            {
                if (!TryMapUtilityProperty(name, style.Get(name), out var mapping)) continue;
                (utilities ??= new List<string>()).Add(mapping.ClassName);
                style.Pop(name);
                if (mapping.ExtraDeclaration is not null && !ExtraCssRules.ContainsKey(ClassSelector(mapping.ClassName)))
                {
                    ExtraCssRules[ClassSelector(mapping.ClassName)] = mapping.ExtraDeclaration;
                }
            }

            if (utilities is not null) classUtilities[className] = utilities;
        }

        return classUtilities;
    }

    private static bool TryMapUtilityProperty(string name, string value, out UtilityMapping mapping)
    {
        mapping = null;
        var lower = (value ?? "").ToLowerInvariant();

        switch (name)
        {
            case "text-align":
                var align = lower switch
                {
                    "right" => "align-end", "left" => "align-left",
                    "center" => "align-center", "justify" => "align-justify", _ => null
                };
                if (align is null) return false;
                mapping = new UtilityMapping(align);
                return true;

            case "font-weight":
                var weight = lower switch { "bold" => "bold", "normal" => "font-weight-normal", _ => null };
                if (weight is null) return false;
                mapping = new UtilityMapping(weight);
                return true;

            case "font-family":
                if (lower.Contains("sans-serif")) mapping = new UtilityMapping("gfont");
                else if (lower.Contains("serif")) mapping = new UtilityMapping("mfont");
                return mapping is not null;

            case "font-size":
                return TryMapFontSize(lower, out mapping);

            case "height":
            case "width":
            case "max-height":
            case "max-width":
                return TryMapBoxSize(name, lower, out mapping);

            case "word-break":
                var wordBreak = lower switch
                {
                    "break-all" => "word-break-break-all", "keep-all" => "word-break-keep-all",
                    "normal" => "word-break-normal", _ => null
                };
                if (wordBreak is null) return false;
                mapping = new UtilityMapping(wordBreak);
                return true;

            case "line-break":
                if (lower is not ("auto" or "loose" or "normal" or "strict")) return false;
                mapping = new UtilityMapping("line-break-" + lower);
                return true;

            case "text-orientation":
                if (lower is not ("sideways" or "upright")) return false;
                mapping = new UtilityMapping(lower);
                return true;

            case "text-combine-upright":
                // 保留給 NormalizeTcySpans 判斷（會改寫成 tcy class），不在這裡轉成 upright-1。
                return false;

            case "text-emphasis-style":
                var emphasis = lower switch { "filled sesame" => "em-sesame", "open sesame" => "em-sesame-open", _ => null };
                if (emphasis is null) return false;
                mapping = new UtilityMapping(emphasis);
                return true;
        }

        return false;
    }

    private static bool TryMapFontSize(string value, out UtilityMapping mapping)
    {
        mapping = null;
        var match = Regex.Match(value, @"^(\d+(?:\.\d+)?)(rem|%|em)$");
        if (!match.Success) return false;
        var magnitude = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        if (match.Groups[2].Value == "em")
        {
            var emName = EmSizeName(magnitude);
            if (!UtilityFontEmSizes.Contains(emName)) return false;
            mapping = new UtilityMapping("font-" + emName);
            return true;
        }

        // 根字級 16px、body 100%：rem 與百分比等價（已確認沒有巢狀字級）。
        var percent = match.Groups[2].Value == "rem" ? magnitude * 100 : magnitude;
        if (percent != decimal.Truncate(percent)) return false;
        var digits = ((int)percent).ToString("000");
        var className = "font-" + digits + "per";
        mapping = new UtilityMapping(className,
            UtilityFontPercents.Contains(digits) ? null : "font-size: " + (int)percent + "%");
        return true;
    }

    private static bool TryMapBoxSize(string name, string value, out UtilityMapping mapping)
    {
        mapping = null;
        if (value == "auto" && name is "height" or "width")
        {
            mapping = new UtilityMapping(name + "-auto");
            return true;
        }

        if (value == "none" && name is "max-height" or "max-width")
        {
            mapping = new UtilityMapping(name + "-none");
            return true;
        }

        var match = Regex.Match(value, @"^(\d+(?:\.\d+)?)(em|%)$");
        if (!match.Success) return false;
        var magnitude = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);

        if (match.Groups[2].Value == "%")
        {
            if (magnitude != decimal.Truncate(magnitude)) return false;
            var digits = ((int)magnitude).ToString("000");
            var className = name + "-" + digits + "per";
            mapping = new UtilityMapping(className,
                UtilityBoxPercents.Contains(digits) ? null : name + ": " + (int)magnitude + "%");
            return true;
        }

        var emName = EmSizeName(magnitude);
        if (!UtilityBoxEmSizes.Contains(emName)) return false;
        mapping = new UtilityMapping(name + "-" + emName);
        return true;
    }

    private static string EmSizeName(decimal magnitude)
    {
        if (magnitude == decimal.Truncate(magnitude)) return (int)magnitude + "em";
        return magnitude.ToString("0.00", CultureInfo.InvariantCulture).Replace(".", "em");
    }

    // 移除與電書協樣式表重複、或原版 EPUB 沒有的宣告，避免 style-kfx.css 多餘規則。
    private void PruneRedundantClassProperties(List<(string StyleStr, Style Style, string ClassName)> sortedStyles,
        Dictionary<string, string> styleClassNames)
    {
        // 記錄每個樣式被哪些元素使用，用元素結構判斷宣告是否可以省略。
        var textOnlyInLinks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var onlyInHeadings = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var bookPart in BookParts)
        {
            foreach (var e in bookPart.Body().DescendantsAndSelf())
            {
                var styleStr = e.Get("style", "");
                if (styleStr is null || !styleClassNames.ContainsKey(styleStr)) continue;

                var inLinks = TextOnlyInsideLinks(e);
                textOnlyInLinks[styleStr] = textOnlyInLinks.TryGetValue(styleStr, out var previousLink)
                    ? previousLink && inLinks
                    : inLinks;

                var isHeading = e.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
                onlyInHeadings[styleStr] = onlyInHeadings.TryGetValue(styleStr, out var previousHeading)
                    ? previousHeading && isHeading
                    : isHeading;
            }
        }

        foreach (var (styleStr, style, _) in sortedStyles)
        {
            // page-break-inside 仍用於圖片外框的辨識（NormalizeFitImageWrappers），只在標題上省略。
            if (onlyInHeadings.GetValueOrDefault(styleStr)) style.Pop("page-break-inside");

            if (!textOnlyInLinks.GetValueOrDefault(styleStr)) continue;

            // style-standard.css 的 a:link 已是 #0000ff。
            if (style.Get("color") is "blue" or "#0000ff") style.Pop("color");
            // .vrtl a / .hltr a 已提供方向正確的 overline / underline。
            if (style.Get("text-decoration") == "overline") style.Pop("text-decoration");
        }
    }

    // 判斷元素的可見文字是否全部位於 <a> 之內（連結的顏色與裝飾由樣式表控制）。
    private static bool TextOnlyInsideLinks(XNode elem)
    {
        var hasText = false;

        bool Walk(XNode node, bool inLink)
        {
            if (!string.IsNullOrWhiteSpace(node.Text))
            {
                hasText = true;
                if (!inLink) return false;
            }

            foreach (var child in node.Children)
            {
                if (!Walk(child, inLink || child.IsLocal("a"))) return false;
                if (!string.IsNullOrWhiteSpace(child.Tail))
                {
                    hasText = true;
                    if (!inLink) return false;
                }
            }

            return true;
        }

        return Walk(elem, elem.IsLocal("a")) && hasText;
    }

    public void UpdateVerticalRlClassUsage()
    {
        VerticalRlClasses = new HashSet<string>();
        foreach (var (selector, style) in CssRules)
        {
            if (selector.StartsWith('.') && !selector.Any(c => " ,:#[]>".Contains(c)) &&
                style.Get("writing-mode") == "vertical-rl") VerticalRlClasses.Add(selector[1..]);
        }

        foreach (var bookPart in BookParts)
        {
            if (VerticalRlClasses.Count == 0 || bookPart.HasVerticalRlClass) continue;
            foreach (var elem in new[] { bookPart.Body() }.Concat(bookPart.Body().Descendants()))
            {
                if (VerticalRlClasses.Overlaps((elem.Get("class", "") ?? "").Split(' ')))
                {
                    bookPart.HasVerticalRlClass = true;
                    break;
                }
            }
        }
    }

    public void AddCompositeAndEquivalentStyles(XNode elem, BookPart bookPart)
    {
        var style = GetStyle(elem);
        foreach (var (combined, sides) in PropertyData.CompositeSideStyles)
        {
            var value = style.Get(sides[0]);
            if (value is null) continue;
            if (sides.Skip(1).All(side => style.Get(side) == value))
            {
                style.Set(combined, value);
                foreach (var side in sides) style.Pop(side);
            }
        }

        SetStyle(elem, style);
        foreach (var child in elem.Elements()) AddCompositeAndEquivalentStyles(child, bookPart);
    }

    public void CreateConditionalPageTemplates()
    {
        if (!HasConditionalContent) return;
        Log.Warning("此書含 illustrated layout 條件頁面，目前僅輸出一般內容。");
    }

    public void FixupIllustratedLayoutAnchors()
    {
        if (!HasConditionalContent) return;
    }

    public (bool ContainsBlock, bool ContainsText, bool ContainsImage) SimplifyStyles(XNode elem, BookPart bookPart,
        Style inheritedProperties, int? defaultOrderedListValue = null, bool knownWidth = true)
    {
        inheritedProperties = new Style(inheritedProperties);
        if (TextUtil.SplitValue(inheritedProperties.Get("font-size", "")).Unit == "em")
        {
            inheritedProperties.Set("font-size", "1em");
        }

        var style = new Style(inheritedProperties).Update(GetStyle(elem), replace: true);
        var origStyle = new Style(style);
        style.Pop("-kfx-render");

        if (bookPart.IsFxl && (elem.IsLocal("a") || elem.IsLocal("span")) &&
            style.Get("display", "inline") == "inline" && (style.ContainsKey("height") || style.ContainsKey("width")))
        {
            style.Set("display", "inline-block");
        }

        if (style.Get("position", "static") == "static")
        {
            foreach (var pos in new[] { "top", "bottom", "left", "right" })
            {
                if (style.Get(pos) == "0") style.Pop(pos);
            }
        }

        style.Set("border-spacing", $"{style.Get("-webkit-border-horizontal-spacing", "2px")} {style.Get("-webkit-border-vertical-spacing", "2px")}");

        var sides = new List<string>();
        foreach (var side in new[] { "top", "bottom", "left", "right" })
        {
            if (style.Pop($"-kfx-user-margin-{side}-percentage", "100") == "-100") sides.Add(side);
        }

        var pageAlign = sides.Count > 0 ? (sides.Count < 4 ? string.Join(",", sides) : "all") : "none";
        style.Set("-amzn-page-align", pageAlign);

        foreach (var name in style.Properties.Keys.ToList())
        {
            var val = style.Get(name);
            if (name.StartsWith("padding") && val.StartsWith('-'))
            {
                style.Pop(name);
                continue;
            }

            if (name is "font-family" or "src" or "background-image" or "list-style-image" or
                "-kfx-attrib-xml-lang" or "-kfx-layout-hints" or "-kfx-style-name" or "-amzn-shape-outside")
                continue;

            var (quantity, unit) = TextUtil.SplitValue(val);
            if (quantity is null) continue;

            if (unit == "lh")
            {
                if (name == "line-height")
                {
                    // 與 bw 一致：不輸出 KFX 的 line-height，讓元素繼承 body 的 1.75。
                    style.Pop(name);
                    continue;
                }

                var writingMode = style.Get("writing-mode", _pageWritingMode);
                if (BlockMarginSide(name, writingMode) is string side)
                {
                    var lines = CountBlankLines(elem, name, quantity.Value, style, writingMode);
                    if (lines > 0)
                    {
                        style.Pop(name);
                        AddBlankMarker(style, name, side, lines, quantity.Value * 1.2m);
                        continue;
                    }
                }

                style.Set(name, TextUtil.ValueStr(quantity.Value * 1.2m, "em"));
            }
            else if (unit == "em" && HasBoxBorderOrPadding(style))
            {
                // 框線方塊本身的 em margin 也可能帶一個空行（例如盜賊少女的特典框）。
                var writingMode = style.Get("writing-mode", _pageWritingMode);
                if (IsVerticalWritingMode(writingMode) && BlockMarginSide(name, writingMode) is string side)
                {
                    var fontSizeRem = ResolveFontSizeRem(style.Get("font-size"));
                    var exactLines = quantity.Value * fontSizeRem / BlankLineRem;
                    var rounded = (int)Math.Floor(exactLines + 0.5m);
                    var lines = Math.Max(0, rounded - 1);
                    if (lines > 0)
                    {
                        style.Pop(name);
                        AddBlankMarker(style, name, side, lines, quantity.Value);
                        continue;
                    }
                }
            }

            if (unit is "vh" or "vw")
            {
                if (pageAlign != "none" && name is "height" or "width")
                {
                    style.Set(name, TextUtil.ValueStr(quantity.Value, "%"));
                }
            }
        }

        if (style.ContainsKey("width")) knownWidth = true;
        else if (style.Get("display", "") == "inline-block") knownWidth = false;

        if (!knownWidth)
        {
            var margins = style.Get("writing-mode", "horizontal-tb") == "horizontal-tb"
                ? new[] { "margin-left", "margin-right" }
                : new[] { "margin-bottom", "margin-top" };
            foreach (var name in margins)
            {
                var (quantity, unit) = TextUtil.SplitValue(style.Get(name, ""));
                if (unit == "%" && quantity is not null)
                {
                    style.Set(name, TextUtil.ValueStr(Math.Round(quantity.Value * 8.534m), "px"));
                }
            }
        }

        if (style.ContainsKey("outline-width") && style.Get("outline-style", "none") == "none") style.Pop("outline-width");

        if (elem.IsLocal("ol"))
        {
            if (elem.Has("start"))
            {
                defaultOrderedListValue = int.Parse(elem.Get("start"));
                if (defaultOrderedListValue == 1) elem.Pop("start");
            }
            else defaultOrderedListValue = 1;
        }
        else if (elem.IsLocal("ul"))
        {
            elem.Pop("start");
            defaultOrderedListValue = null;
        }
        else if (elem.IsLocal("li"))
        {
            if (elem.Has("value"))
            {
                var value = int.Parse(elem.Get("value"));
                if (defaultOrderedListValue is null || value == defaultOrderedListValue) elem.Pop("value");
            }

            defaultOrderedListValue = null;
        }

        if (elem.IsLocal("div"))
        {
            style.Update(NonHeritableDefaultProperties, replace: false);
        }

        if (style.Get("background-image", "none") != "none" && style.Get("-amzn-max-crop-percentage") == "0 0 0 0")
        {
            style.Pop("-amzn-max-crop-percentage");
            style.Set("background-size", "contain");
        }
        else if (style.Get("background-image", "none") != "none" && style.Get("-amzn-max-crop-percentage") == "0 100 100 0")
        {
            style.Pop("-amzn-max-crop-percentage");
            style.Set("background-size", "cover");
        }

        if (elem.IsLocal("a") && !style.ContainsKey("color") && style.Get("-kfx-link-color") == style.Get("-kfx-visited-color") &&
            style.Get("-kfx-link-color") is not null)
        {
            style.Set("color", style.Pop("-kfx-link-color"));
            style.Pop("-kfx-visited-color");
        }

        var parentStyle = new Style(style);
        foreach (var name in new[]
                 {
                     "font-size", "-kfx-user-margin-bottom-percentage", "-kfx-user-margin-left-percentage",
                     "-kfx-user-margin-right-percentage", "-kfx-user-margin-top-percentage"
                 })
        {
            if (origStyle.ContainsKey(name)) parentStyle.Set(name, origStyle.Get(name));
        }

        foreach (var name in parentStyle.Properties.Keys.ToList())
        {
            if (!PropertyData.HeritableProperties.Contains(name) ||
                (name != "line-height" && TextUtil.SplitValue(parentStyle.Get(name)).Unit == "%"))
            {
                parentStyle.Pop(name);
            }
        }

        var kfxLayoutHints = (style.Get("-kfx-layout-hints", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        LastKfxHeadingLevel = style.Pop("-kfx-heading-level", LastKfxHeadingLevel);
        var headingLevel = LastKfxHeadingLevel;

        var containsBlock = false;
        var containsText = false;
        var containsImage = false;
        foreach (var child in elem.Elements().ToList())
        {
            var (cb, ct, ci) = SimplifyStyles(child, bookPart, parentStyle, defaultOrderedListValue, knownWidth);
            containsBlock |= cb;
            containsText |= ct;
            containsImage |= ci;
            if (child.IsLocal("li") && defaultOrderedListValue is not null) defaultOrderedListValue++;
        }

        var childCount = elem.Children.Count;
        if (childCount > 0 && (!BookHasIllustratedLayoutConditionalPageTemplate || FixedLayout))
        {
            if (string.IsNullOrEmpty(elem.Text) && string.IsNullOrEmpty(elem.Tail))
            {
                var childProps = new Dictionary<string, Dictionary<string, int>>();
                foreach (var child in elem.Elements())
                {
                    foreach (var (name, val) in GetStyle(child).Properties)
                    {
                        if (!PropertyData.ReverseHeritableProperties.Contains(name)) continue;
                        if (!childProps.TryGetValue(name, out var counts)) childProps[name] = counts = new Dictionary<string, int>();
                        counts[val] = counts.GetValueOrDefault(val) + 1;
                    }
                }

                var newHeritable = new Dictionary<string, string>();
                foreach (var (name, values) in childProps)
                {
                    var total = values.Values.Sum();
                    var mostCommon = values.OrderByDescending(p => p.Value).First().Key;
                    if (total < childCount && style.Get(name) is null) continue;
                    if (values[mostCommon] >= childCount * 0.8) newHeritable[name] = mostCommon;
                }

                if (newHeritable.Count > 0)
                {
                    foreach (var child in elem.Elements())
                    {
                        var childStyle = GetStyle(child);
                        foreach (var (name, newValue) in newHeritable)
                        {
                            var val = childStyle.Get(name);
                            if (val is not null)
                            {
                                if (val == newValue) childStyle.Pop(name);
                            }
                            else if (style.Get(name) != newValue) childStyle.Set(name, style.Get(name));
                        }

                        SetStyle(child, childStyle);
                    }

                    style.Update(new Style(newHeritable), replace: true);
                }
            }
        }

        inheritedProperties.Update(NonHeritableDefaultProperties);

        if (elem.IsLocal("div") && !(FixedLayout || IllustratedLayout || HasConditionalContent))
        {
            if (kfxLayoutHints.Contains("heading") && !containsBlock)
            {
                elem.Name = "h" + headingLevel;
                inheritedProperties.Pop("font-size");
                inheritedProperties.Pop("font-weight");
                if (style.Get("writing-mode", "horizontal-tb") == "horizontal-tb")
                {
                    inheritedProperties.Pop("margin-top");
                    inheritedProperties.Pop("margin-bottom");
                }
                else
                {
                    inheritedProperties.Pop("margin-left");
                    inheritedProperties.Pop("margin-right");
                }
            }
            else if (kfxLayoutHints.Contains("figure") && containsImage && !Epub2Desired)
            {
                elem.Name = "figure";
                if (style.Get("writing-mode", "horizontal-tb") == "horizontal-tb")
                {
                    inheritedProperties.Update(new Style(new Dictionary<string, string>
                    {
                        ["margin-top"] = "1em", ["margin-bottom"] = "1em"
                    }), replace: true);
                }
                else
                {
                    inheritedProperties.Update(new Style(new Dictionary<string, string>
                    {
                        ["margin-left"] = "1em", ["margin-right"] = "1em"
                    }), replace: true);
                }
            }
            else if (containsText && !containsBlock)
            {
                elem.Name = "p";
                if (style.Get("writing-mode", "horizontal-tb") == "horizontal-tb")
                {
                    inheritedProperties.Update(new Style(new Dictionary<string, string>
                    {
                        ["margin-top"] = "1em", ["margin-bottom"] = "1em"
                    }), replace: true);
                }
                else
                {
                    inheritedProperties.Update(new Style(new Dictionary<string, string>
                    {
                        ["margin-left"] = "1em", ["margin-right"] = "1em"
                    }), replace: true);
                }
            }
        }

        if (TextUtil.SplitValue(style.Get("font-size", "")).Unit == "em")
        {
            inheritedProperties.Set("font-size", "1em");
        }

        if (style.Get("background-color", "transparent") == "transparent" && style.Get("background-image", "none") == "none")
        {
            foreach (var name in new[] { "background-clip", "background-origin", "background-position", "background-repeat", "background-size" })
                style.Pop(name);
        }

        var removeFrom = elem.IsLocal("a") ? inheritedProperties : style;
        removeFrom.Pop("-kfx-link-color");
        removeFrom.Pop("-kfx-visited-color");

        foreach (var name in style.Properties.Keys.ToList())
        {
            if (style.Get(name) == inheritedProperties.Get(name, "")) style.Pop(name);
        }

        // 與 bw 一致：不輸出 KFX 的 line-height，讓元素繼承 reset 的 body 1.75。
        style.Pop("line-height");

        SetStyle(elem, style);

        containsBlock = containsBlock || elem.IsLocal("aside") || elem.IsLocal("caption") || elem.IsLocal("div") ||
            elem.IsLocal("figure") || Regex.IsMatch(elem.Name, "^h[1-6]$") || elem.IsLocal("li") ||
            elem.IsLocal("p") || elem.IsLocal("td") || elem.IsNs(XmlNamespaces.Idx, "entry");
        containsText = containsText || !string.IsNullOrEmpty(elem.Text);
        containsImage = containsImage || elem.IsLocal("img") || elem.IsNs(XmlNamespaces.Svg, "svg");
        return (containsBlock, containsText, containsImage);
    }

    public void StripResetDefaultProperties(XNode elem)
    {
        var style = GetStyle(elem);
        var modified = false;
        if (PropertyData.ResetDefaultMarginTags.Contains(elem.Name))
        {
            foreach (var name in new[] { "margin-left", "margin-right", "margin-top", "margin-bottom" })
            {
                if (style.Get(name) == "0")
                {
                    style.Pop(name);
                    modified = true;
                }
            }
        }

        if (style.Get("text-align") == "justify" && !AncestorOverridesTextAlign(elem))
        {
            style.Pop("text-align");
            modified = true;
        }

        if (PropertyData.ResetDefaultFontFamilies.Contains(style.Get("font-family", "")) && !AncestorOverridesFontFamily(elem))
        {
            style.Pop("font-family");
            modified = true;
        }

        if (style.Get("hyphens") == "auto" && !AncestorOverridesProperty(elem, "hyphens", "auto"))
        {
            style.Pop("hyphens");
            modified = true;
        }

        if (style.Get("line-break") == "normal" && !AncestorOverridesProperty(elem, "line-break", "normal"))
        {
            style.Pop("line-break");
            modified = true;
        }

        if (elem.IsLocal("body") && style.Get("writing-mode") == "vertical-rl")
        {
            style.Pop("writing-mode");
            modified = true;
        }

        if (modified) SetStyle(elem, style);
    }

    // 依「區塊方向」判斷 margin 是否是空行。
    // 直排的區塊方向是水平（右→左），橫排是垂直（上→下）。
    private static string BlockMarginSide(string name, string writingMode)
    {
        var mode = writingMode ?? "horizontal-tb";
        if (mode.StartsWith("vertical", StringComparison.Ordinal))
        {
            var rtl = mode.EndsWith("-rl", StringComparison.Ordinal);
            if (name == (rtl ? "margin-right" : "margin-left")) return "before";
            if (name == (rtl ? "margin-left" : "margin-right")) return "after";
            return null;
        }

        if (name == "margin-top") return "before";
        if (name == "margin-bottom") return "after";
        return null;
    }

    private static bool IsVerticalWritingMode(string writingMode)
        => writingMode is not null && writingMode.StartsWith("vertical", StringComparison.Ordinal);

    private static decimal ResolveFontSizeRem(string value)
    {
        var (quantity, unit) = TextUtil.SplitValue(value ?? "");
        if (quantity is null) return 1.0m;
        return unit switch
        {
            "rem" or "em" => quantity.Value,
            "%" => quantity.Value / 100m,
            "px" => quantity.Value / 16m,
            _ => 1.0m
        };
    }

    private static bool HasBoxBorderOrPadding(Style style)
    {
        var hasBorder = false;
        var hasPadding = false;
        foreach (var (name, value) in style.Properties)
        {
            if (value is null) continue;
            if (name is "border" or "border-top" or "border-right" or "border-bottom" or "border-left")
            {
                if (value is not ("none" or "0" or "0px")) hasBorder = true;
            }
            else if (name is "border-style" or "border-top-style" or "border-right-style" or
                     "border-bottom-style" or "border-left-style")
            {
                if (value is not ("none" or "hidden")) hasBorder = true;
            }
            else if (BoxPaddingProperties.Contains(name))
            {
                if (value is not ("0" or "0px" or "0em" or "0%")) hasPadding = true;
            }
        }

        if (hasBorder) return true;
        if (!hasPadding) return false;

        // 只有 padding 沒有框線時，通常只是版面間距（例如口絵頁的字下げ）；
        // 有背景色/背景圖才視為真正的方塊。
        var backgroundColor = style.Get("background-color", "transparent");
        return backgroundColor is not (null or "transparent" or "#0" or "rgba(0,0,0,0)") ||
               style.Get("background-image", "none") is not (null or "none");
    }

    private static bool IsFirstBlockAfterHr(XNode elem)
    {
        var previous = elem.PreviousSibling();
        return previous is not null && previous.IsLocal("hr");
    }

    private int CountBlankLines(XNode elem, string name, decimal rawLh, Style style, string writingMode)
    {
        var isBox = !elem.IsLocal("hr") && HasBoxBorderOrPadding(style);
        var fontSizeRem = ResolveFontSizeRem(style.Get("font-size"));
        var exactLines = rawLh * 1.2m * fontSizeRem / BlankLineRem;
        var rounded = (int)Math.Floor(exactLines + 0.5m);

        if (isBox)
        {
            // 橫排頁面的框線方塊 margin 是版面間距，不是空行。
            if (!IsVerticalWritingMode(writingMode)) return 0;
            // 直排頁面的框線方塊 margin 會含一份方塊本身的間距；扣掉一行再判斷。
            return Math.Max(0, rounded - 1);
        }

        if (rounded < 1) return 0;

        if (IsVerticalWritingMode(writingMode)) return rounded;

        // 橫排頁面的 margin-top/bottom 也常用於 colophon 的細部間距；
        // 只接受接近整行的值，以及 hr 與緊接 hr 的區塊。
        if (elem.IsLocal("hr") || IsFirstBlockAfterHr(elem)) return rounded;
        return Math.Abs(exactLines - rounded) <= 0.05m * Math.Max(1, rounded) ? rounded : 0;
    }

    private static void AddBlankMarker(Style style, string propName, string side, int lines, decimal emValue)
    {
        var before = side == "before";
        var key = before ? BlankBeforeProperty : BlankAfterProperty;
        var origKey = before ? BlankOrigBeforeProperty : BlankOrigAfterProperty;
        var propKey = before ? BlankPropBeforeProperty : BlankPropAfterProperty;
        var existing = int.TryParse(style.Get(key), out var count) ? count : 0;
        style.Set(key, (existing + lines).ToString(CultureInfo.InvariantCulture));
        style.Set(origKey, TextUtil.ValueStr(emValue, "em"));
        style.Set(propKey, propName);
    }

    public void ExpandBlockMargins(BookPart bookPart)
    {
        foreach (var elem in bookPart.Body().DescendantsAndSelf().ToList())
        {
            if (!elem.Has("style")) continue;
            var style = GetStyle(elem);
            if (!style.ContainsKey(BlankBeforeProperty) && !style.ContainsKey(BlankAfterProperty)) continue;

            var before = style.Pop(BlankBeforeProperty);
            var after = style.Pop(BlankAfterProperty);
            var origBefore = style.Pop(BlankOrigBeforeProperty);
            var origAfter = style.Pop(BlankOrigAfterProperty);
            var propBefore = style.Pop(BlankPropBeforeProperty);
            var propAfter = style.Pop(BlankPropAfterProperty);

            if (!CanHostBlankParagraphs(elem))
            {
                if (origBefore is not null && propBefore is not null) style.Set(propBefore, origBefore);
                if (origAfter is not null && propAfter is not null) style.Set(propAfter, origAfter);
                SetStyle(elem, style);
                continue;
            }

            SetStyle(elem, style);
            var parent = elem.Parent;
            if (parent is null) continue;
            var index = elem.IndexInParent();
            if (before is not null && int.TryParse(before, out var beforeCount) && beforeCount > 0)
            {
                for (var i = 0; i < beforeCount; i++) parent.Insert(index + i, CreateBlankParagraph());
                index += beforeCount;
            }

            if (after is not null && int.TryParse(after, out var afterCount) && afterCount > 0)
            {
                for (var i = 0; i < afterCount; i++) parent.Insert(index + 1 + i, CreateBlankParagraph());
            }
        }
    }

    private static bool CanHostBlankParagraphs(XNode elem)
    {
        var parent = elem.Parent;
        if (parent is null) return false;
        if (parent.Name is not ("body" or "div" or "section" or "article" or "aside" or "blockquote" or "figure"))
            return false;
        if (elem.Name is not ("p" or "div" or "hr" or "figure" or "blockquote" or "aside" or "section" or "article" or "pre") &&
            !Regex.IsMatch(elem.Name, "^h[1-6]$"))
            return false;

        foreach (var ancestor in elem.Ancestors())
        {
            if (ancestor.Name is "table" or "tr" or "td" or "th" or "p" or "span" or "a" or "li") return false;
            if (ancestor.IsLocal("body")) break;
        }

        return true;
    }

    private static XNode CreateBlankParagraph()
    {
        var p = new XNode("p");
        p.Append(new XNode("br"));
        return p;
    }

    public bool AncestorOverridesTextAlign(XNode elem)
    {
        foreach (var parent in elem.Ancestors())
        {
            var value = new Style(parent.Get("style", "")).Get("text-align");
            if (value is not null) return value != "justify";
            if (parent.IsLocal("body")) break;
        }

        return false;
    }

    public bool AncestorOverridesProperty(XNode elem, string name, string resetValue)
    {
        foreach (var parent in elem.Ancestors())
        {
            var value = new Style(parent.Get("style", "")).Get(name);
            if (value is not null) return value != resetValue;
            if (parent.IsLocal("body")) break;
        }

        return false;
    }

    public bool AncestorOverridesFontFamily(XNode elem)
    {
        foreach (var parent in elem.Ancestors())
        {
            var value = new Style(parent.Get("style", "")).Get("font-family");
            if (value is not null) return !PropertyData.ResetDefaultFontFamilies.Contains(value);
            if (parent.IsLocal("body")) break;
        }

        return false;
    }

    public void UpdateDefaultFontAndLanguage()
    {
        var fontFamilies = new Dictionary<string, int>();
        var languages = new Dictionary<string, int>();

        int Scan(XNode elem, string fontFamily, string lang)
        {
            var style = elem.Get("style", "");
            if (style.Contains("font-family") || style.Contains("-kfx-attrib-xml-lang"))
            {
                var parsed = new Style(style);
                fontFamily = parsed.Get("font-family", fontFamily);
                lang = parsed.Get("-kfx-attrib-xml-lang", lang);
            }

            var textLen = elem.Text?.Length ?? 0;
            foreach (var child in elem.Elements())
            {
                if (child.IsLocal("head")) continue;
                textLen += Scan(child, fontFamily, lang);
            }

            if (textLen > 0)
            {
                fontFamilies[fontFamily ?? ""] = fontFamilies.GetValueOrDefault(fontFamily ?? "") + textLen;
                languages[lang ?? ""] = languages.GetValueOrDefault(lang ?? "") + textLen;
            }

            return elem.Tail?.Length ?? 0;
        }

        foreach (var bookPart in BookParts) Scan(bookPart.Html, null, null);

        var bestFont = DefaultFontFamily;
        var bestMerit = 0;
        foreach (var (font, merit) in fontFamilies)
        {
            if (merit > bestMerit && !string.IsNullOrEmpty(font) &&
                Regex.IsMatch(font, "^" + Regex.Escape(DefaultFontFamily) + "(,.+)?$"))
            {
                bestFont = font;
                bestMerit = merit;
            }
        }

        DefaultFontFamily = bestFont;
    }

    public void SetHtmlDefaults()
    {
        foreach (var bookPart in BookParts)
        {
            var body = bookPart.Body();
            var style = GetStyle(body);
            // 封面頁也要記錄書寫方向，輸出 <html class="vrtl|hltr"> 時才有依據。
            bookPart.WritingModeValue = style.Get("writing-mode", WritingMode) ?? WritingMode;
            if (bookPart.IsCoverPage) continue;
            if (!style.ContainsKey("font-family") && !string.IsNullOrEmpty(DefaultFontFamily)) style.Set("font-family", DefaultFontFamily);
            if (!style.ContainsKey("font-size")) style.Set("font-size", DefaultFontSize);
            if (!style.ContainsKey("line-height")) style.Set("line-height", DefaultLineHeight);
            if (!style.ContainsKey("writing-mode")) style.Set("writing-mode", WritingMode);
            SetStyle(body, style);
            if (!string.IsNullOrEmpty(Language)) bookPart.Html.SetNs(XmlNamespaces.Xml, "lang", Language);
        }
    }
}
