using System.Globalization;
using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

public partial class KfxEpub
{
    public List<string> ContextStack { get; } = new();
    public HashSet<string> ReportedCharacters { get; } = new();

    // 版面內容中的區塊圖片容器（$270 + 區塊圖片版面），供整頁圖片判定使用。
    private readonly HashSet<XNode> _blockImageContainers = new();

    public static readonly Dictionary<string, string> ListStyleTypes = new()
    {
        ["$346"] = "ol", ["$347"] = "ol", ["$342"] = "ul", ["$340"] = "ul", ["$271"] = "ul",
        ["$349"] = "ul", ["$343"] = "ol", ["$344"] = "ol", ["$345"] = "ol", ["$341"] = "ul"
    };

    public static readonly Dictionary<string, string> ClassificationEpubType = new()
    {
        ["$618"] = "footnote", ["$619"] = "endnote", ["$281"] = "footnote"
    };

    public void ProcessReadingOrder()
    {
        var usedSections = new HashSet<string>();
        foreach (var readingOrder in ReadingOrders)
        {
            var sections = readingOrder.GetValueOrDefault("$170") as List<object> ?? new List<object>();
            foreach (var sectionName in sections.Select(s => s.ToStr()).Where(s => s is not null))
            {
                if (usedSections.Add(sectionName))
                {
                    ProcessSection((IonStruct)GetFragment("$260", sectionName), 0);
                }
                else Log.Error($"閱讀順序有重複章節 {sectionName}");
            }
        }
    }

    public void ProcessSection(IonStruct section, int seq)
    {
        var sectionName = section.Pop("$174").ToStr();
        PushContext("section " + sectionName);
        section.Pop("$702");
        foreach (var key in new[]
                 {
                     "yj.conversion.html_name", "yj.semantics.book_anatomy_type", "yj.semantics.page_type",
                     "yj.authoring.auto_panel_settings_auto_mask_color_flag",
                     "yj.authoring.auto_panel_settings_mask_color", "yj.authoring.auto_panel_settings_opacity",
                     "yj.authoring.auto_panel_settings_padding_bottom", "yj.authoring.auto_panel_settings_padding_left",
                     "yj.authoring.auto_panel_settings_padding_right", "yj.authoring.auto_panel_settings_padding_top"
                 })
            section.Pop(key);

        var pageTemplates = section.Pop("$141") as List<object> ?? new List<object>();
        var hasConditional = pageTemplates.OfType<IonStruct>().Any(pt => pt.ContainsKey("$171"));

        if (section.ContainsKey("nmdl.canvas_width") || section.ContainsKey("nmdl.template_type"))
        {
            Log.Warning("Scribe notebook 章節尚未支援，略過。");
        }
        else if (IsComic || IsChildren)
        {
            if (pageTemplates.Count != 1) Log.Error($"漫畫章節 {ContextText} 有 {pageTemplates.Count} 個 page template");
            ProcessPageSpreadPageTemplate((IonStruct)GetFragment("$608", pageTemplates[0]), sectionName);
        }
        else if ((IsMagazine || IsPrintReplica) && hasConditional)
        {
            foreach (var ptObj in pageTemplates.OfType<IonStruct>())
            {
                if (ptObj.ContainsKey("$171") && !EvaluateBinaryCondition(ptObj.Pop("$171"))) continue;
                var layout = ptObj.GetValueOrDefault("$156").ToStr();
                if (layout is "$325" or "$323")
                {
                    ptObj.Pop("$159");
                    ptObj.Pop("$156");
                    var bp = NewBookPart();
                    LinkCssFile(bp, StylesCssFilepath);
                    AddContent(ptObj, bp.Html, bp, WritingMode, layout);
                    ProcessPosition(GetLocationId(ptObj), 0, bp.Body());
                }
                else if (layout == "$437") ProcessPageSpreadPageTemplate(ptObj, sectionName);
            }
        }
        else
        {
            var bookPart = NewBookPart();
            var mainTemplate = (IonStruct)pageTemplates[^1];
            ProcessContent(mainTemplate, bookPart.Html, bookPart, WritingMode, isSection: true);
            LinkCssFile(bookPart, StylesCssFilepath);
            if (pageTemplates.Count > 1)
            {
                var body = bookPart.Body();
                for (var i = 0; i < pageTemplates.Count - 1; i++)
                {
                    var pt = (IonStruct)pageTemplates[i];
                    ProcessContent(pt, body, bookPart, WritingMode, isSection: true);
                }
            }
        }

        CheckEmpty(section, ContextText);
        PopContext();
    }

    public void ProcessPageSpreadPageTemplate(IonStruct pageTemplate, string sectionName, string pageSpread = "",
        string parentTemplateId = null, bool isSection = true)
    {
        if (pageTemplate.GetValueOrDefault("$159").ToStr() == "$270" &&
            pageTemplate.GetValueOrDefault("$156").ToStr() is "$437" or "$438")
        {
            pageTemplate.Pop("$159");
            var layout = pageTemplate.Pop("$156").ToStr();
            HandleVirtualPanel(pageTemplate);
            pageTemplate.Pop("$192");
            pageTemplate.Pop("$67");
            pageTemplate.Pop("$66");
            pageTemplate.Pop("$140");
            pageTemplate.Pop("$560");
            parentTemplateId = GetLocationId(pageTemplate);
            var story = (IonStruct)GetNamedFragment(pageTemplate, "$259");
            var storyName = story.Pop("$176").ToStr();
            PushContext("story " + storyName);

            var baseProperty = layout == "$437" ? "page-spread" : "facing-page";
            var left = baseProperty + "-left";
            var right = baseProperty + "-right";
            var pageProperty = PageProgressionDirection == "ltr" ? left : right;

            foreach (var item in (story.Pop("$146") as List<object> ?? new List<object>()).OfType<IonStruct>().ToList())
            {
                if (item.GetValueOrDefault("$159").ToStr() == "$269")
                {
                    var bp = BookParts[^1];
                    ProcessContent(item, bp.Body(), bp, WritingMode, isSection: true);
                }
                else
                {
                    ProcessPageSpreadPageTemplate(item, sectionName, pageProperty, parentTemplateId, false);
                    pageProperty = pageProperty == right ? left : right;
                    parentTemplateId = null;
                }
            }

            PopContext();
            CheckEmpty(story, "story " + storyName);
        }
        else
        {
            var spreadType = pageSpread.Replace("rendition:", "").Replace("page-spread-", "");
            var uniqueName = spreadType.Length > 0 ? sectionName + "-" + spreadType : sectionName;
            var bookPart = NewBookPart(opfProperties: pageSpread.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            ProcessContent(pageTemplate, bookPart.Html, bookPart, WritingMode, isSection: isSection);
            LinkCssFile(bookPart, StylesCssFilepath);
            if (parentTemplateId is not null) ProcessPosition(parentTemplateId, 0, bookPart.Body());
        }

        CheckEmpty(pageTemplate, $"Section {sectionName} page_template");
    }

    private void HandleVirtualPanel(IonStruct pageTemplate)
    {
        var virtualPanel = pageTemplate.Pop("$434").ToStr();
        if (virtualPanel == "$441" && VirtualPanelsAllowed) VirtualPanels = true;
        else if (virtualPanel is not null) Log.Warning($"非預期 virtual_panel：{virtualPanel}");
    }

    public void ProcessStory(IonStruct story, XNode parent, BookPart bookPart, string writingMode, string imageRole = "page")
    {
        var storyName = story.Pop("$176").ToStr();
        PushContext("story " + storyName);
        var locationId = GetLocationId(story);
        if (locationId is not null) ProcessPosition(locationId, 0, parent);
        ProcessContentList(story.Pop("$146") as List<object> ?? new List<object>(), parent, bookPart, writingMode, imageRole: imageRole);
        PopContext();
        CheckEmpty(story, ContextText);
    }

    public void AddContent(IonStruct content, XNode parent, BookPart bookPart, string writingMode,
        string contentLayout = null, int? fixedHeight = null, int? fixedWidth = null, string imageRole = "page")
    {
        if (content.ContainsKey("$145"))
        {
            var textElem = XhtmlUtil.SubElement(parent, "span");
            var text = ContentText(content.Pop("$145"));
            textElem.Text = CleanTextForLxml(text);
        }
        else if (content.Pop("$146") is List<object> contentList)
        {
            ProcessContentList(contentList, parent, bookPart, writingMode, contentLayout, fixedHeight, fixedWidth, imageRole);
        }
        else if (content.ContainsKey("$176"))
        {
            var story = (IonStruct)GetNamedFragment(content, "$259");
            ProcessStory(story, parent, bookPart, writingMode, imageRole);
        }
    }

    public void ProcessContentList(List<object> contentList, XNode parent, BookPart bookPart, string writingMode,
        string contentLayout = null, int? fixedHeight = null, int? fixedWidth = null, string imageRole = "page")
    {
        foreach (var content in contentList)
        {
            ProcessContent(content, parent, bookPart, writingMode, contentLayout, fixedHeight, fixedWidth, imageRole: imageRole);
        }
    }

    public void ProcessContent(object contentObj, XNode parent, BookPart bookPart, string writingMode,
        string contentLayout = null, int? fixedHeight = null, int? fixedWidth = null, bool isSection = false,
        string imageRole = "page")
    {
        if (contentObj is string textContent)
        {
            var span = XhtmlUtil.SubElement(parent, "span");
            span.Text = textContent;
            return;
        }

        if (contentObj is IonSymbol symbolContent)
        {
            ProcessContent(GetFragment("$608", symbolContent.Value), parent, bookPart, writingMode, contentLayout,
                fixedHeight, fixedWidth, imageRole: imageRole);
            return;
        }

        if (contentObj is not IonStruct content)
        {
            Log.Error($"未知的內容型別：{TextUtil.TypeName(contentObj)}");
            return;
        }

        var contentType = content.Pop("$159").ToStr();
        var locationId = GetLocationId(content);
        PushContext($"{contentType} {locationId}");
        var contentElem = new XNode("unknown");
        var discard = false;
        var pageImageCandidate = false;
        var isTopLevel = parent.IsLocal("html") || parent.IsNs(XmlNamespaces.XHtml, "html");

        var kfxStyleName = content.Pop("$157").ToStr();
        AddKfxStyle(content, kfxStyleName);

        if (content.ContainsKey("$560")) writingMode = PropertyValue("$560", content["$560"]);
        else if (writingMode is null) writingMode = WritingMode;

        content.Pop("yj.semantics.page_entity");

        if (contentType == "$269")
        {
            contentElem.Name = "div";
            content.Pop("$597");
            content.Pop("yj.semantics.type");
            content.Pop("yj.semantics.toc_creator");
            content.Pop("yj.semantics.toc_info_type");
            content.Pop("yj.authoring.metrics_detection_type");
            content.Pop("yj.authoring.metrics_detection_mode");
            if (content.ContainsKey("$605"))
            {
                content.Pop("$605");
                AddStyle(contentElem, new Style(new Dictionary<string, string> { ["white-space"] = "nowrap" }));
            }

            AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole);
        }
        else if (contentType == "$271")
        {
            contentElem.Name = "img";
            var imgResource = ProcessExternalResource(GetFragmentName(content, "$164"));
            contentElem.Set("src", PathUtil.UrlRelativePath(imgResource.Filename, bookPart.Filename));
            contentElem.Set("alt", content.Pop("$584", "").ToStr() ?? "");
            content.Pop("yj.semantics.image_type");
        }
        else if (contentType == "$274")
        {
            var resourceName = content.Pop("$175").ToStr();
            var altText = content.Pop("$584", "").ToStr() ?? "";
            content.Pop("$597");
            AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole);
            ProcessPlugin(resourceName, altText, contentElem, bookPart);
        }
        else if (contentType == "$439")
        {
            contentElem.Name = "div";
            content.Pop("$156");
            AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole);
            AddStyle(contentElem, new Style(new Dictionary<string, string> { ["display"] = "none" }));
        }
        else if (contentType == "$270")
        {
            contentElem.Name = "div";
            var layout = content.Pop("$156").ToStr();
            content.Pop("$171");
            content.Pop("yj.dictionary.term");
            content.Pop("yj.dictionary.unnormalized_term");
            content.Pop("yj.dictionary.rules");
            var isScaleFit = layout == "$326";

            if (isTopLevel && ((isScaleFit && FixedLayout) || layout == "$325")) bookPart.IsFxl = true;

            if (isScaleFit)
            {
                fixedHeight = (int?)PixelValue(content.Pop("$67"));
                fixedWidth = (int?)PixelValue(content.Pop("$66"));
            }
            else
            {
                fixedHeight = fixedWidth = null;
            }

            var childImageRole = "page";
            if (layout == (writingMode != "vertical-rl" ? "$322" : "$323") && !bookPart.IsFxl) childImageRole = "illustration";
            else if (isScaleFit && !(bookPart.IsFxl && IsPdfBacked)) childImageRole = "illustration";

            AddContent(content, contentElem, bookPart, writingMode, layout, fixedHeight, fixedWidth, childImageRole);

            // KFX 的區塊圖片容器若只包一張圖，代表「整頁一張圖」。
            var blockImageLayout = writingMode != "vertical-rl" ? "$323" : "$322";
            pageImageCandidate = layout == blockImageLayout;

            if (layout == blockImageLayout)
            {
                // 一般區塊圖片：留給後續 SVG wrapper 處理。
            }
            else if (isScaleFit)
            {
                content.Pop("$434");
                content.Pop("$140");
                content.Pop("$432");
                if (bookPart.IsFxl && isTopLevel)
                {
                    if (fixedWidth is not null && fixedHeight is not null)
                    {
                        AddViewport(bookPart, fixedWidth.Value, fixedHeight.Value);
                        LinkCssFile(bookPart, FixedLayoutCssFilepath);
                    }
                }
                else if (bookPart.IsFxl && IsPdfBacked)
                {
                    if (fixedWidth is not null && fixedHeight is not null)
                    {
                        AddStyle(contentElem, new Style(new Dictionary<string, string>
                        {
                            ["height"] = TextUtil.ValueStr(fixedHeight, "px"),
                            ["width"] = TextUtil.ValueStr(fixedWidth, "px")
                        }));
                    }
                }
                else
                {
                    AddSvgWrapperToBlockImage(contentElem, bookPart, fixedHeight ?? 0, fixedWidth ?? 0);
                }
            }
            else if (layout == "$325")
            {
                if (!IsMagazine) Log.Error("非雜誌書籍使用 $325 container");
                var width = GetPxValue(content, "$66");
                var height = GetPxValue(content, "$67");
                AddViewport(bookPart, width, height);
                LinkCssFile(bookPart, FixedLayoutCssFilepath);
            }

            content.Pop("$475");
            content.Pop("$684");
            content.Pop("$426");
            content.Pop("$429");
        }
        else if (contentType == "$276")
        {
            var listType = content.GetValueOrDefault("$100").ToStr();
            contentElem.Name = ListStyleTypes.GetValueOrDefault(listType, "ul");
            if (content.ContainsKey("$104")) contentElem.Set("start", content.Pop("$104").ToString());
            content.Pop("$102");
            AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole);
        }
        else if (contentType == "$277")
        {
            contentElem.Name = "li";
            if (content.ContainsKey("$104")) contentElem.Set("value", content.Pop("$104").ToString());
            content.Pop("$102");
            AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole);
        }
        else if (contentType == "$278")
        {
            contentElem.Name = "table";
            if (content.Pop("$152") is List<object> columns)
            {
                var colgroup = XhtmlUtil.SubElement(contentElem, "colgroup");
                foreach (var col in columns.OfType<IonStruct>())
                {
                    var colElem = XhtmlUtil.SubElement(colgroup, "col");
                    if (col.ContainsKey("$118")) colElem.Set("span", col.Pop("$118").ToString());
                    col.Pop("$698");
                    col.Pop("yj.conversion.source_attr_width");
                    col.Pop("yj.conversion.source_style_width");
                    col.Pop("box_align");
                    col.Pop("fit_width");
                    var colStyle = ConvertYjProperties(col);
                    SetStyle(colElem, colStyle);
                }
            }

            content.Pop("$700");
            content.Pop("$630");
            content.Pop("$629");
            content.Pop("$821");
            content.Pop("$755");
            AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole);
        }
        else if (contentType == "$454") { contentElem.Name = "tbody"; AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole); }
        else if (contentType == "$151") { contentElem.Name = "thead"; AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole); }
        else if (contentType == "$455") { contentElem.Name = "tfoot"; AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole); }
        else if (contentType == "$279")
        {
            contentElem.Name = "tr";
            AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole);
            for (var i = 0; i < contentElem.Children.Count; i++)
            {
                var child = contentElem.Children[i];
                if (child.IsLocal("div")) child.Name = "td";
                else if (!child.IsLocal("td") && !child.IsLocal("table") && !child.IsLocal("ol") &&
                         !child.IsLocal("ul") && !child.IsLocal("hr"))
                {
                    var td = new XNode("td");
                    contentElem.RemoveChild(child);
                    td.Append(child);
                    contentElem.Insert(i, td);
                }
            }
        }
        else if (contentType == "$596") contentElem.Name = "hr";
        else if (contentType == "$272")
        {
            contentElem.Name = "svg";
            contentElem.Namespace = XmlNamespaces.Svg;
            contentElem.NsMap[""] = XmlNamespaces.Svg;
            contentElem.Set("version", "1.1").Set("preserveAspectRatio", "xMidYMid meet");
            if (content.ContainsKey("$66"))
            {
                fixedHeight = (int?)PixelValue(content.Pop("$67"));
                fixedWidth = (int?)PixelValue(content.Pop("$66"));
            }

            if (fixedHeight is not null && fixedWidth is not null)
                contentElem.Set("viewBox", $"0 0 {fixedWidth} {fixedHeight}");
            content.Pop("$686");
            var contentList = content.Pop("$146") as List<object> ?? new List<object>();
            foreach (var shape in content.Pop("$250") as List<object> ?? new List<object>())
                ProcessKvgShape(contentElem, (IonStruct)shape, contentList, bookPart, writingMode);
        }
        else
        {
            contentElem.Name = "div";
            AddContent(content, contentElem, bookPart, writingMode, imageRole: imageRole);
        }

        if (content.Pop("$683") is List<object> annotations)
        {
            foreach (var annotationObj in annotations.OfType<IonStruct>())
            {
                var annotationType = annotationObj.Pop("$687").ToStr();
                if (annotationType == "$584" && contentType == "$270")
                {
                    var annotationText = ContentText(annotationObj.Pop("$145"));
                    if (!string.IsNullOrEmpty(annotationText) && annotationText != "no accessible name found.")
                        contentElem.Set("aria-label", annotationText);
                }
            }
        }

        content.Pop("$696");
        content.Pop("$591");
        content.Pop("$592");
        content.Pop("$663");
        content.Pop("$436");
        content.Pop("$622");
        content.Pop("$615");
        content.Pop("$475");

        if (locationId is not null)
        {
            ProcessPosition(locationId, 0, contentElem);
            if (PositionAnchors.TryGetValue(locationId, out var offsets))
            {
                foreach (var anchorOffset in offsets.Keys.OrderBy(k => k).ToList())
                {
                    var anchorElem = LocateOffset(contentElem, anchorOffset, splitAfter: false, zeroLen: true);
                    if (anchorElem is not null) ProcessPosition(locationId, anchorOffset, anchorElem);
                }
            }
        }

        content.Pop("yj.conversion.offset_map");
        content.Pop("yj.conversion.modified_content_info");
        content.Pop("yj.conversion.html_name_index");
        content.Pop("yj.conversion.source_attr_width");
        content.Pop("yj.conversion.source_attr_height");
        content.Pop("yj.conversion.source_style_height");
        content.Pop("yj.conversion.source_style_width");

        var styleEvents = content.Pop("$142") as List<object> ?? new List<object>();
        var dropcapChars = content.Pop("$126", 0) is object dc ? Convert.ToInt32(dc) : 0;
        var dropcapLines = content.Pop("$125", 0) is object dl ? Convert.ToInt32(dl) : 0;
        if (dropcapChars > 0 && dropcapLines > 0)
        {
            var dropcap = new IonStruct
            {
                ["$143"] = 0, ["$144"] = dropcapChars, ["$125"] = dropcapLines
            };
            if (content.TryGetValue("$173", out var styleName)) dropcap["$173"] = styleName;
            styleEvents.Insert(0, dropcap);
        }

        foreach (var eventObj in styleEvents.OfType<IonStruct>())
        {
            var offset = Convert.ToInt32(eventObj.Pop("$143"));
            var length = Convert.ToInt32(eventObj.Pop("$144"));
            var isDropcap = eventObj.ContainsKey("$125");
            var eventElem = FindOrCreateStyleEventElement(contentElem, offset, length, isDropcap);
            if (eventElem is null) break;

            AddKfxStyle(eventObj, eventObj.Pop("$157").ToStr());

            var rubyName = eventObj.Pop("$757").ToStr();
            if (rubyName is not null)
            {
                eventElem = XhtmlUtil.ReplaceElementWithContainer(eventElem, "ruby");
                var nextRubyOffset = 0;
                var rubyList = eventObj.ContainsKey("$758")
                    ? new List<object> { new IonStruct { ["$758"] = eventObj.Pop("$758"), ["$143"] = 0, ["$144"] = length } }
                    : eventObj.Pop("$759") as List<object>;
                foreach (var rubyEntryObj in rubyList ?? new List<object>())
                {
                    if (rubyEntryObj is not IonStruct rubyEntry) continue;
                    var rubyOffset = Convert.ToInt32(rubyEntry.Pop("$143"));
                    var rubyLength = Convert.ToInt32(rubyEntry.Pop("$144"));
                    var rbElem = FindOrCreateStyleEventElement(eventElem, rubyOffset, rubyLength, false);
                    if (rbElem is null) break;
                    var rbParent = rbElem.Parent;
                    while (rbParent is not null && !ReferenceEquals(rbParent, eventElem) && rbParent.Children.Count == 1)
                    {
                        rbElem = rbParent;
                        rbParent = rbElem.Parent;
                    }

                    rbElem = XhtmlUtil.ReplaceElementWithContainer(rbElem, "rb");
                    var rubyContent = GetRubyContent(rubyName, Convert.ToInt32(rubyEntry.Pop("$758")));
                    rubyContent.Pop("$758");
                    var tempRt = new XNode("div");
                    ProcessContent(rubyContent, tempRt, bookPart, writingMode, imageRole: imageRole);
                    foreach (var rtChild in tempRt.DescendantsAndSelf().ToList())
                    {
                        var rtStyle = GetStyle(rtChild);
                        if (rtStyle.Get("font-size") == "0.5em") rtStyle.Pop("font-size");
                        SetStyle(rtChild, rtStyle);
                    }

                    XNode rtElem;
                    if (tempRt.Children.Count == 1 && tempRt.Children[0].IsLocal("div"))
                    {
                        rtElem = tempRt.Children[0];
                        tempRt.RemoveChild(rtElem);
                    }
                    else rtElem = tempRt;

                    rtElem.Name = "rt";
                    var parentRb = rbElem.Parent;
                    parentRb.Insert(parentRb.Children.IndexOf(rbElem) + 1, rtElem);
                    nextRubyOffset += rubyLength;
                }
            }

            if (eventObj.ContainsKey("$616") || eventElem.Has("class") || eventElem.Has("style"))
            {
                eventElem = XhtmlUtil.ReplaceElementWithContainer(eventElem, eventElem.IsLocal("div") ? "div" : "span");
            }

            eventObj.Pop("$604");
            if (isDropcap)
            {
                AddStyle(eventElem, new Style(new Dictionary<string, string>
                {
                    ["float"] = "left",
                    ["font-size"] = TextUtil.ValueStr(Convert.ToInt32(eventObj.Pop("$125")), "em"),
                    ["line-height"] = "100%",
                    ["margin-top"] = "0",
                    ["margin-right"] = "0.1em",
                    ["margin-bottom"] = "0"
                }));
            }

            var anchor = eventObj.Pop("$179").ToStr();
            if (anchor is not null)
            {
                if (eventElem.IsLocal("span") && string.IsNullOrEmpty(eventElem.Text)) eventElem.Name = "a";
                else eventElem = XhtmlUtil.ReplaceElementWithContainer(eventElem, "a");
                eventElem.Set("href", AnchorAsUri(anchor));
            }

            var eventStyle = ProcessContentProperties(eventObj);
            AddStyle(eventElem, eventStyle, replace: true);
            var mergedEventStyle = GetStyle(eventElem);
            FixVerticalAlignProperties(eventElem, mergedEventStyle, setStyleIfChanged: true);
        }

        content.Pop("$647");
        content.Pop("$648");
        var fitTight = content.Pop("$784", false) is true;
        var fitWidth = content.Pop("$478", false) is true;
        var linkTo = content.Pop("$179").ToStr();
        var render = content.Pop("$601").ToStr();
        content.Pop("$597");

        var contentStyle = GetStyle(contentElem, remove: true);
        contentStyle.Update(ProcessContentProperties(content), replace: true);
        CheckEmpty(content, $"{ContextText} content type {contentType}");

        if (fitTight && contentStyle.Get("width") == "100%") contentStyle.Pop("width");

        if (bookPart.IsFxl && contentStyle.Get("position", "static") == "static")
        {
            foreach (var pos in new[] { "top", "bottom", "left", "right" })
            {
                if (contentStyle.ContainsKey(pos))
                {
                    contentStyle.Set("position", "absolute");
                    break;
                }
            }
        }

        if (linkTo is not null)
        {
            var (container, containerStyle) = CreateContainer(contentElem, contentStyle, "a",
                PropertyData.HeritableProperties.Concat(new[] { "-kfx-attrib-colspan", "-kfx-attrib-rowspan",
                    "-kfx-table-vertical-align", "-kfx-box-align", "-kfx-heading-level", "-kfx-layout-hints",
                    "-kfx-link-color", "-kfx-visited-color" }).ToHashSet());
            container.Set("href", AnchorAsUri(linkTo));
            SetStyle(contentElem, contentStyle);
            contentElem = container;
            contentStyle = containerStyle;
        }

        if (render == "$283")
        {
            contentStyle.Set("-kfx-render", "inline");

            if (contentElem.IsLocal("a") || contentElem.IsLocal("audio") || contentElem.IsLocal("img") ||
                contentElem.IsNs(XmlNamespaces.Svg, "svg") || contentElem.IsLocal("video"))
            {
                if (contentStyle.Get("text-indent") == "0") contentStyle.Pop("text-indent");
            }
            else if (contentElem.IsLocal("div") || contentElem.IsLocal("table"))
            {
                if (contentElem.IsLocal("div"))
                {
                    contentElem.Name = "span";
                    if (IsInlineOnly(contentElem))
                    {
                        // 只有一個無屬性空 span 子節點時，把它的文字併回父節點。
                        if (contentElem.Children.Count == 1 && contentElem.Children[0].IsLocal("span") &&
                            contentElem.Children[0].Children.Count == 0 && contentElem.Children[0].Attributes.Count == 0)
                        {
                            var inner = contentElem.Children[0];
                            contentElem.Text = (contentElem.Text ?? "") + (inner.Text ?? "") + (inner.Tail ?? "");
                            contentElem.RemoveChild(inner);
                        }
                    }
                    else
                    {
                        contentElem.Name = "div";
                        fitWidth = true;
                    }
                }
                else
                {
                    fitWidth = true;
                }
            }
            else
            {
                Log.Error($"{ContextText} render:inline 用於非預期元素：{contentElem.Name}");
            }
        }
        else
        {
            if (render is not null)
            {
                Log.Error($"{ContextText} 未知的 render 值：{render}");
            }

            if ((contentElem.IsLocal("a") || contentElem.IsLocal("audio") || contentElem.IsLocal("img") ||
                 contentElem.IsNs(XmlNamespaces.Svg, "svg") || contentElem.IsLocal("video")) &&
                contentStyle.Get("position") != "fixed")
            {
                // Python 版 SKIP_FIT_WIDTH_FOR_IMAGES：此類容器不套用 fit-width。
                fitWidth = false;

                FixVerticalAlignProperties(contentElem, contentStyle);
                var blockProperties = PropertyData.HeritableProperties.Concat(new[]
                {
                    "-kfx-attrib-colspan", "-kfx-attrib-rowspan", "-kfx-box-align", "-kfx-heading-level",
                    "-kfx-layout-hints", "-kfx-table-vertical-align", "box-sizing", "float", "margin-left",
                    "margin-right", "margin-top", "margin-bottom", "overflow", "page-break-after",
                    "page-break-before", "page-break-inside", "text-indent", "transform", "transform-origin",
                    "display"
                }).ToHashSet();
                var (container, containerStyle) = CreateContainer(contentElem, contentStyle, "div", blockProperties);
                if (!fitWidth && containerStyle.ContainsKey("-kfx-box-align"))
                {
                    containerStyle.Set("text-align", containerStyle.Pop("-kfx-box-align"));
                }

                if (containerStyle.Get("float") is not null)
                {
                    var widthOrHeight = writingMode != "vertical-rl" ? "width" : "height";
                    if (contentStyle.Get(widthOrHeight)?.EndsWith("%") == true)
                    {
                        containerStyle.Set(widthOrHeight, contentStyle.Get(widthOrHeight));
                        contentStyle.Set(widthOrHeight, "100%");
                    }
                }

                SetStyle(contentElem, contentStyle);
                contentElem = container;
                contentStyle = containerStyle;
            }
        }

        if (!fitTight && contentElem.IsLocal("img"))
        {
            // 圖片外層由後續的程序建立。
        }

        var imageContainer = contentElem.Children.Count == 1 &&
                             (contentElem.Children[0].IsLocal("img") ||
                              contentElem.Children[0].IsNs(XmlNamespaces.Svg, "svg"));
        // Python 版 SKIP_FIT_WIDTH_FOR_IMAGES：圖片本身不套用 fit-width 的 inline-block。
        if (imageContainer || contentElem.IsLocal("img") || contentElem.IsNs(XmlNamespaces.Svg, "svg") ||
            contentElem.IsLocal("audio") || contentElem.IsLocal("video"))
        {
            fitWidth = false;
        }

        if (fitWidth && contentElem.IsLocal("div")) contentStyle.Set("display", "inline-block");
        if (contentStyle.Get("-kfx-box-align") is string boxAlign)
        {
            contentStyle.Pop("-kfx-box-align");
            // 對齊 Python 版：只有具備明確 width 或 table 才用 margin 置中。
            if (boxAlign is "left" or "right" or "center" &&
                (contentStyle.ContainsKey("width") || contentElem.IsLocal("table")))
            {
                if (boxAlign != "left") contentStyle.Set("margin-left", "auto");
                if (boxAlign != "right") contentStyle.Set("margin-right", "auto");
            }
        }

        // COMBINE_NESTED_DIVS：合併僅有一層 div 的巢狀容器。
        if (contentElem.IsLocal("div") && string.IsNullOrEmpty(contentElem.Text) && contentElem.Children.Count == 1 &&
            contentStyle.Get("display", "block") == "block" && contentStyle.Get("position", "static") == "static" &&
            contentStyle.Get("float", "none") == "none" &&
            contentElem.Attributes.Keys.All(k => k is "id" or "style"))
        {
            var child = contentElem.Children[0];
            if (child.IsLocal("div") && string.IsNullOrEmpty(child.Tail) &&
                child.Attributes.Keys.All(k => k is "id" or "style"))
            {
                var childStyle = GetStyle(child);
                if (childStyle.Get("display", "block") == "block" &&
                    childStyle.Get("position", "static") == "static" &&
                    childStyle.Get("float", "none") == "none" &&
                    !(contentStyle.Properties.Keys.Intersect(childStyle.Properties.Keys)
                        .Any(k => k != "-kfx-style-name")) &&
                    !((contentElem.Has("id") || IllustratedLayout) && child.Has("id")))
                {
                    var doMerge = true;
                    if (isTopLevel)
                    {
                        var allowed = new HashSet<string>
                        {
                            "blockquote", "del", "div", "dl", "h1", "h2", "h3", "h4", "h5", "h6", "hr",
                            "ins", "svg", "ol", "p", "pre", "table", "ul"
                        };
                        if (child.Elements().Any(g => !(g.Namespace.Length == 0 && allowed.Contains(g.Name)) &&
                                                      !g.IsNs(XmlNamespaces.Svg, "svg")))
                        {
                            doMerge = false;
                        }
                    }

                    if (doMerge)
                    {
                        contentStyle.Update(childStyle, replace: false);
                        if (contentElem.Has("id"))
                        {
                            contentElem.RemoveChild(child);
                            contentElem.Text = child.Text;
                            foreach (var grandchild in child.Children.ToList())
                            {
                                child.RemoveChild(grandchild);
                                contentElem.Append(grandchild);
                            }
                        }
                        else
                        {
                            contentElem.RemoveChild(child);
                            contentElem = child;
                        }
                    }
                }
            }
        }

        FixVerticalAlignProperties(contentElem, contentStyle);
        SetStyle(contentElem, contentStyle);

        // 記錄「只包一張圖」的區塊圖片容器，並判斷這一頁是否只有這個容器。
        if (pageImageCandidate && IsSingleImageContainer(contentElem))
        {
            _blockImageContainers.Add(contentElem);
        }

        if (isTopLevel && IsPageImageRoot(contentElem))
        {
            bookPart.IsPageImage = true;
        }

        if (!discard)
        {
            if (isTopLevel)
            {
                if (!contentElem.IsLocal("aside") && !contentElem.IsLocal("div") && !contentElem.IsLocal("figure"))
                {
                    var container = new XNode(contentElem.Name) { Namespace = contentElem.Namespace };
                    container.Append(contentElem);
                    contentElem = container;
                }

                contentElem.Name = "body";
                contentElem.Namespace = "";
            }

            parent.Append(contentElem);
        }

        PopContext();
    }

    public bool IsInlineOnly(XNode elem)
    {
        if (elem.IsNs(XmlNamespaces.Svg, "svg")) return true;
        if (!elem.IsLocal("a") && !elem.IsLocal("audio") && !elem.IsLocal("img") && !elem.IsLocal("rb") &&
            !elem.IsLocal("rt") && !elem.IsLocal("ruby") && !elem.IsLocal("span") && !elem.IsLocal("video"))
        {
            return false;
        }

        foreach (var child in elem.Children)
        {
            if (!IsInlineOnly(child)) return false;
        }

        return true;
    }

    public (XNode Container, Style Style) CreateContainer(XNode contentElem, Style contentStyle, string tag,
        HashSet<string> containerProperties)
    {
        var container = new XNode(tag);
        container.Append(contentElem);
        var containerStyle = contentStyle.Partition(containerProperties);
        if (contentElem.Has("id")) MoveAnchor(contentElem, container);
        if (contentStyle.Get("-kfx-style-name") is string styleName) containerStyle.Set("-kfx-style-name", styleName);
        return (container, containerStyle);
    }

    public string ContentText(object content)
    {
        if (content is string text) return text;
        if (content is IonStruct st)
        {
            var name = st.Pop("name").ToStr();
            var index = Convert.ToInt32(st.Pop("$403"));
            CheckEmpty(st, "content");
            if (BookData.TryGetValue("$145", out var bundleValue) && bundleValue is FragmentMap bundle &&
                bundle.TryGetValue(name, out var contentValue) && contentValue is IonStruct contentStruct)
            {
                var list = contentStruct.GetValueOrDefault("$146") as List<object>;
                return list[index].ToStr() ?? "";
            }

            Log.Error("缺少書籍內容：" + name);
            return "";
        }

        return "";
    }

    public string CombinedText(XNode elem)
    {
        if (elem.IsLocal("img") || elem.IsNs(XmlNamespaces.Svg, "svg") || elem.IsNs(XmlNamespaces.MathMl, "math")) return " ";
        if (TextCombineInUse && GetStyle(elem).Get("text-combine-upright") == "all") return " ";

        var sb = new System.Text.StringBuilder();
        if (elem.Text is not null) sb.Append(elem.Text);
        foreach (var child in elem.Elements()) sb.Append(CombinedText(child));
        if (elem.Tail is not null) sb.Append(elem.Tail);
        return sb.ToString();
    }

    public XNode LocateOffset(XNode root, int offsetQuery, bool splitAfter = false, bool zeroLen = false,
        bool isDropcap = false)
    {
        var result = LocateOffsetIn(root, offsetQuery, splitAfter, zeroLen, isDropcap);
        if (result is XNode node) return node;
        if (result is int failure)
        {
            if (failure == 0 && !splitAfter) return XhtmlUtil.SubElement(root, "span");
            if (isDropcap) return XhtmlUtil.SubElement(root, "span");
            Log.Error($"無法定位 offset {offsetQuery}（剩餘 {failure}）");
        }

        return null;
    }

    private object LocateOffsetIn(XNode elem, int offsetQuery, bool splitAfter, bool zeroLen, bool isDropcap)
    {
        if (offsetQuery < 0) return offsetQuery;
        if (elem.IsLocal("span"))
        {
            var textLen = TextUtil.UnicodeLen(elem.Text);
            // 直排中的 tcy（text-combine-upright: all）在定位時只算一個字元。
            if (textLen > 1 && TextCombineInUse)
            {
                var ancestor = elem;
                while (ancestor is not null)
                {
                    if (GetStyle(ancestor).Get("text-combine-upright") == "all")
                    {
                        textLen = 1;
                        break;
                    }

                    ancestor = ancestor.Parent;
                }
            }

            if (textLen > 0)
            {
                if (!splitAfter)
                {
                    if (offsetQuery == 0) return elem;
                    if (offsetQuery < textLen)
                    {
                        var newSpan = SplitSpan(elem, offsetQuery);
                        if (zeroLen) SplitSpan(newSpan, 0);
                        return newSpan;
                    }
                }
                else
                {
                    if (offsetQuery == textLen - 1) return elem;
                    if (offsetQuery < textLen)
                    {
                        SplitSpan(elem, offsetQuery + 1);
                        return elem;
                    }
                }

                offsetQuery -= textLen;
            }
        }
        else
        {
            if (isDropcap && GetStyle(elem).Get("float") is not null) return offsetQuery;
            if (elem.IsLocal("img") || elem.IsNs(XmlNamespaces.Svg, "svg") ||
                elem.IsNs(XmlNamespaces.MathMl, "math") || GetStyle(elem).Get("-kfx-render") == "inline")
            {
                if (offsetQuery == 0) return elem;
                offsetQuery--;
                return offsetQuery;
            }

            if (!(elem.IsLocal("a") || elem.IsLocal("aside") || elem.IsLocal("div") || elem.IsLocal("figure") ||
                  Regex.IsMatch(elem.Name, "^h[1-6]$") || elem.IsLocal("li") || elem.IsLocal("ruby") || elem.IsLocal("rb")))
            {
                return offsetQuery;
            }
        }

        foreach (var child in elem.Elements())
        {
            var result = LocateOffsetIn(child, offsetQuery, splitAfter, zeroLen, isDropcap);
            if (result is XNode node) return node;
            offsetQuery = (int)result;
        }

        return offsetQuery;
    }

    public XNode SplitSpan(XNode oldSpan, int firstTextLen)
    {
        var text = oldSpan.Text ?? "";
        var newSpan = new XNode("span");
        oldSpan.Text = TextUtil.UnicodeSlice(text, 0, firstTextLen);
        newSpan.Text = TextUtil.UnicodeSlice(text, firstTextLen);
        SetStyle(newSpan, GetStyle(oldSpan));
        var parent = oldSpan.Parent;
        parent.Insert(parent.Children.IndexOf(oldSpan) + 1, newSpan);
        return newSpan;
    }

    public XNode FindOrCreateStyleEventElement(XNode contentElem, int eventOffset, int eventLength, bool isDropcap)
    {
        if (eventLength <= 0) return null;
        var first = LocateOffset(contentElem, eventOffset, isDropcap: isDropcap);
        if (first is null) return null;
        var last = LocateOffset(contentElem, eventOffset + eventLength - 1, splitAfter: true, isDropcap: isDropcap);
        if (last is null || ReferenceEquals(first, last)) return first;

        var firstParent = first.Parent;
        var lastParent = last.Parent;
        if (!ReferenceEquals(firstParent, lastParent))
        {
            var tryFirst = first;
            var firsts = new List<XNode> { tryFirst };
            while (firstParent is not null && string.IsNullOrEmpty(firstParent.Text) &&
                   firstParent.Children.IndexOf(tryFirst) == 0)
            {
                tryFirst = firstParent;
                firstParent = tryFirst.Parent;
                if (firstParent is not null) firsts.Add(tryFirst);
            }

            var tryLast = last;
            var lasts = new List<XNode> { tryLast };
            while (lastParent is not null && string.IsNullOrEmpty(tryLast.Tail) &&
                   lastParent.Children.IndexOf(tryLast) == lastParent.Children.Count - 1)
            {
                tryLast = lastParent;
                lastParent = tryLast.Parent;
                if (lastParent is not null) lasts.Add(tryLast);
            }

            var found = false;
            foreach (var candidateFirst in firsts)
            {
                foreach (var candidateLast in lasts)
                {
                    if (ReferenceEquals(candidateFirst.Parent, candidateLast.Parent))
                    {
                        first = candidateFirst;
                        last = candidateLast;
                        found = true;
                        break;
                    }
                }

                if (found) break;
            }

            if (!found)
            {
                Log.Warning($"{ContextText} style event 的 first 與 last 父節點不同：offset {eventOffset} len {eventLength}");
                return null;
            }
        }

        if (ReferenceEquals(first, contentElem) && first.Parent is null)
        {
            var wrapper = new XNode("span") { Text = first.Text };
            first.Text = null;
            first.Append(wrapper);
            first = wrapper;
            last = wrapper;
        }

        if (first.Parent is null) return null;
        if (last.Parent is null) last = first;

        var eventElem = new XNode("span");
        var parentElem = first.Parent;
        var firstIndex = parentElem.Children.IndexOf(first);
        var lastIndex = parentElem.Children.IndexOf(last);
        for (var i = firstIndex; i <= lastIndex; i++)
        {
            var child = parentElem.Children[firstIndex];
            parentElem.RemoveChild(child);
            eventElem.Append(child);
        }

        parentElem.Insert(firstIndex, eventElem);
        return eventElem;
    }

    public IonStruct GetRubyContent(string rubyName, int rubyId)
    {
        if (!BookData.TryGetValue("$756", out var rubyValue) || rubyValue is not FragmentMap rubyMap ||
            !rubyMap.TryGetValue(rubyName, out var contentValue) || contentValue is not IonStruct rubyContent)
        {
            throw new InvalidOperationException("缺少 ruby_content：" + rubyName);
        }

        foreach (var entryObj in rubyContent.GetValueOrDefault("$146") as List<object> ?? new List<object>())
        {
            var entry = entryObj is IonSymbol sym ? (IonStruct)GetFragment("$608", sym.Value, delete: false) : entryObj as IonStruct;
            if (entry is not null && Convert.ToInt32(entry.GetValueOrDefault("$758")) == rubyId) return entry.Clone();
        }

        throw new InvalidOperationException($"ruby_content 缺少 ruby_id {rubyId}");
    }

    public void AddKfxStyle(IonStruct content, string kfxStyleName)
    {
        if (string.IsNullOrEmpty(kfxStyleName)) return;
        if (BookData.TryGetValue("$157", out var styleValue) && styleValue is FragmentMap styles &&
            styles.TryGetValue(kfxStyleName, out var styleData) && styleData is IonStruct style)
        {
            UsedKfxStyles.Add(kfxStyleName);
            foreach (var (key, value) in style)
            {
                if (!content.ContainsKey(key)) content[key] = IonUtil.DeepCopy(value);
            }
        }
        else if (MissingKfxStyles.Add(kfxStyleName))
        {
            Log.Error($"{ContextText} 找不到 KFX 樣式定義：{kfxStyleName}");
        }
    }

    public string CleanTextForLxml(string text)
    {
        var chars = new HashSet<int>
        {
            0x0000, 0x0001, 0x0002, 0x0003, 0x0004, 0x0005, 0x0006, 0x0007, 0x0008, 0x000b, 0x000c, 0x000e, 0x000f,
            0x0010, 0x0011, 0x0012, 0x0013, 0x0014, 0x0015, 0x0016, 0x0017, 0x0018, 0x0019, 0x001a, 0x001b,
            0x001c, 0x001d, 0x001e, 0x001f, 0x061c, 0x2063, 0xfff9, 0xfffa, 0xfffb, 0xfffe, 0xffff
        };
        var sb = new System.Text.StringBuilder();
        foreach (var c in text) sb.Append(chars.Contains(c) ? '?' : c);
        return sb.ToString();
    }

    public void AddViewport(BookPart bookPart, int width, int height)
    {
        XhtmlUtil.SubElement(bookPart.Head(), "meta").Set("name", "viewport")
            .Set("content", $"width={width}, height={height}");
    }

    // 「整頁只有一張圖片」：頁面根節點本身就是區塊圖片容器，
    // 或根容器內只有一個區塊圖片容器，且沒有文字。
    private bool IsPageImageRoot(XNode elem)
    {
        if (_blockImageContainers.Contains(elem))
        {
            return string.IsNullOrEmpty(elem.Text);
        }

        if (!string.IsNullOrEmpty(elem.Text) || elem.Children.Count != 1) return false;
        var child = elem.Children[0];
        return child.IsLocal("div") && _blockImageContainers.Contains(child) && string.IsNullOrEmpty(child.Tail);
    }

    // 區塊圖片容器內只有一張圖（圖片本身可能先被包在一個 div 內），沒有文字。
    private static bool IsSingleImageContainer(XNode elem)
    {
        if (!string.IsNullOrEmpty(elem.Text) || elem.Children.Count != 1) return false;
        var child = elem.Children[0];
        if (!string.IsNullOrEmpty(child.Tail)) return false;

        if (child.IsLocal("img")) return child.Children.Count == 0;

        if (child.IsLocal("div") && string.IsNullOrEmpty(child.Text) && child.Children.Count == 1)
        {
            var img = child.Children[0];
            return img.IsLocal("img") && img.Children.Count == 0;
        }

        return false;
    }

    public int GetPxValue(IonStruct content, string propName)
    {
        if (!content.ContainsKey(propName)) return 0;
        var value = PropertyValue(propName, content.Pop(propName));
        var match = Regex.Match(value ?? "", "^([0-9]+)(px)?$");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    public string ContextText => string.Join(", ", ContextStack);
    public void PushContext(string context) => ContextStack.Add(context);
    public void PopContext() { if (ContextStack.Count > 0) ContextStack.RemoveAt(ContextStack.Count - 1); }
}
