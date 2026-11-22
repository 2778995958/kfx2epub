using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

public partial class KfxEpub
{
    public Dictionary<string, string> AnchorUri { get; } = new();
    public Dictionary<string, XNode> AnchorElem { get; } = new();
    public Dictionary<string, string> AnchorId { get; } = new();
    public HashSet<string> AnchorIds { get; } = new();
    public Dictionary<string, Dictionary<int, List<string>>> PositionAnchors { get; } = new();
    public Dictionary<string, HashSet<(string, int)>> AnchorPositions { get; } = new();
    public HashSet<string> UsedAnchors { get; } = new();
    public HashSet<string> ImmovableAnchors { get; } = new();
    public Dictionary<string, string> PageAnchorIdLabel { get; } = new();
    public Dictionary<string, int> AnchorHeadingLevel { get; } = new();
    public Dictionary<string, string> NavContainerSection { get; } = new();
    public Dictionary<(string, double), string> NavtoAnchor { get; } = new();
    public int TocEntryCount;
    public Dictionary<string, string> PageLabelAnchorId { get; } = new();
    public bool ApproximatePagesRemoved;

    public void ProcessAnchors()
    {
        AnchorUri.Clear();
        AnchorElem.Clear();
        AnchorId.Clear();
        AnchorIds.Clear();
        PositionAnchors.Clear();
        AnchorPositions.Clear();
        UsedAnchors.Clear();
        ImmovableAnchors.Clear();
        PageAnchorIdLabel.Clear();
        AnchorHeadingLevel.Clear();

        if (BookData.TryGetValue("$266", out var anchorsValue) && anchorsValue is FragmentMap anchors)
        {
            foreach (var (anchorName, anchorValue) in anchors.ToList())
            {
                if (anchorValue is not IonStruct anchor) continue;
                CheckFragmentName(anchor, "$266", anchorName);
                if (anchor.TryGetValue("$186", out var uriValue))
                {
                    var uri = uriValue.ToStr() ?? "";
                    if (uri is "http://" or "https://") uri = "";
                    AnchorUri[anchorName] = uri;
                }
                else if (anchor.ContainsKey("$183"))
                {
                    RegisterAnchor(anchorName, GetPosition(anchor.Pop("$183")));
                }

                anchor.Pop("$597");
                CheckEmpty(anchor, $"anchor {anchorName}");
            }

            BookData.Remove("$266");
        }
    }

    public void ProcessNavigation()
    {
        NavContainerSection.Clear();
        NavtoAnchor.Clear();
        TocEntryCount = 0;
        PageLabelAnchorId.Clear();

        if (BookData.TryGetValue("$390", out var sectionNavValue) && sectionNavValue is List<object> sectionNavList)
        {
            foreach (var sectionNavObj in sectionNavList.OfType<IonStruct>())
            {
                var sectionName = sectionNavObj.Pop("$174").ToStr();
                foreach (var navContainer in (List<object>)sectionNavObj.Pop("$392"))
                {
                    NavContainerSection[navContainer.ToStr() ?? navContainer.ToString()] = sectionName;
                }

                CheckEmpty(sectionNavObj, "section_navigation");
            }
        }

        var bookNavigations = BookData.TryGetValue("$389", out var bn) && bn is List<object> list
            ? list.ToList()
            : new List<object>();

        foreach (var readingOrder in ReadingOrders)
        {
            var readingOrderName = readingOrder.GetValueOrDefault("$178", "").ToStr() ?? "";
            for (var i = 0; i < bookNavigations.Count; i++)
            {
                if (bookNavigations[i] is not IonStruct bookNavigation) continue;
                if ((bookNavigation.GetValueOrDefault("$178", "").ToStr() ?? "") != readingOrderName) continue;
                bookNavigations.RemoveAt(i);
                bookNavigation.Pop("$178");

                var navContainers = new List<(IonStruct Data, string Name)>();
                var hasNavHeadings = false;
                foreach (var navContainerObj in (List<object>)bookNavigation.Pop("$392"))
                {
                    var data = navContainerObj is IonStruct direct ? direct : (IonStruct)GetFragment("$391", navContainerObj.ToStr());
                    if (data is null) throw new InvalidOperationException("找不到 nav container " + IonUtil.Repr(navContainerObj));
                    navContainers.Add((data, navContainerObj is IonStruct ? "" : navContainerObj.ToStr() ?? ""));
                    if (data.GetValueOrDefault("$235").ToStr() == "$798") hasNavHeadings = true;
                }

                foreach (var (data, name) in navContainers)
                {
                    ProcessNavContainer(data, name, readingOrderName, hasNavHeadings);
                    CheckEmpty(bookNavigation, "book_navigation");
                }

                break;
            }
        }

        if (BookData.TryGetValue("$391", out var remaining) && remaining is FragmentMap map)
        {
            CheckEmpty(map, "nav_container");
            BookData.Remove("$391");
        }

        if (BookData.TryGetValue("$394", out var conditional))
        {
            CheckEmpty(conditional, "conditional_nav_group_unit");
            BookData.Remove("$394");
        }
    }

    public void ProcessNavContainer(IonStruct navContainer, string navContainerName, string readingOrderName,
        bool hasNavHeadings)
    {
        navContainer.Pop("mkfx_id");
        var name = navContainer.Pop("$239", navContainerName).ToStr() ?? navContainerName;
        var sectionName = NavContainerSection.GetValueOrDefault(name);
        var navType = navContainer.Pop("$235").ToStr();

        if (navContainer.ContainsKey("imports"))
        {
            foreach (var importName in (List<object>)navContainer.Pop("imports"))
            {
                var imported = (IonStruct)((FragmentMap)BookData["$391"]).Pop(importName.ToStr());
                ProcessNavContainer(imported, navContainerName, readingOrderName, hasNavHeadings);
            }
        }
        else
        {
            foreach (var navUnitObj in (List<object>)navContainer.Pop("$247"))
            {
                var navUnit = navUnitObj is IonStruct directUnit ? directUnit : (IonStruct)GetFragment("$393", navUnitObj.ToStr());
                navUnit.Pop("mkfx_id");

                if (navType is "$212" or "$214" or "$213" or "$798")
                {
                    ProcessNavUnit(navType, navUnit, NcxToc, navContainerName, sectionName,
                        navType == "$212" && !hasNavHeadings ? 1 : null);
                }
                else if (navType == "$236")
                {
                    var label = GetRepresentation(navUnit).Label;
                    var unitName = navUnit.Pop("$240", label).ToStr() ?? label;
                    var position = GetPosition(navUnit.Pop("$246"));
                    var landmarkType = navUnit.Pop("$238").ToStr();
                    if (landmarkType is not null)
                    {
                        var guideType = GuideTypeOfLandmarkType.GetValueOrDefault(landmarkType, landmarkType);
                        if (label == "cover-nav-unit") label = "";
                        var anchorName = UniqueAnchorName(unitName.Length > 0 ? unitName : guideType);
                        RegisterAnchor(anchorName, position);
                        AddGuideEntry(guideType, label, anchor: anchorName);
                    }
                }
                else if (navType == "$237")
                {
                    var label = GetRepresentation(navUnit).Label;
                    var unitName = navUnit.Pop("$240", "page_list_entry").ToStr() ?? "page_list_entry";
                    var position = GetPosition(navUnit.Pop("$246"));
                    if (!string.IsNullOrEmpty(label))
                    {
                        if (name == "APPROXIMATE_PAGE_LIST")
                        {
                            // 移除舊版產生的近似頁碼。
                            ApproximatePagesRemoved = true;
                        }
                        else
                        {
                            var anchorName = "page_" + label;
                            if (ReadingOrders.Count > 1) anchorName = readingOrderName + "_" + anchorName;
                            anchorName = UniqueAnchorName(anchorName);
                            var anchorId = RegisterAnchor(anchorName, position);
                            if (PageLabelAnchorId.GetValueOrDefault(label) == anchorId) { }
                            else
                            {
                                PageLabelAnchorId[label] = anchorId;
                                AddPagemapEntry(label, anchor: anchorName);
                            }
                        }
                    }
                }

                CheckEmpty(navUnit, $"nav_container {navContainerName} nav_unit");
            }
        }

        CheckEmpty(navContainer, $"nav_container {navContainerName}");
    }

    public void ProcessNavUnit(string navType, IonStruct navUnit, List<TocEntry> ncxToc, string navContainerName,
        string sectionName, int? headingLevel)
    {
        var (label, icon, description) = GetRepresentation(navUnit);
        var desc = navUnit.Pop("$154").ToStr();
        if (!string.IsNullOrEmpty(desc)) description = desc.Trim();

        int? nextHeadingLevel;
        if (navType == "$798")
        {
            if (label == "heading-nav-unit") label = "";
            var landmarkType = navUnit.Pop("$238").ToStr();
            if (landmarkType is not null)
            {
                var levels = new[] { "$799", "$800", "$801", "$802", "$803", "$804" };
                var index = Array.IndexOf(levels, landmarkType);
                headingLevel = index >= 0 ? index + 1 : null;
            }

            nextHeadingLevel = headingLevel;
        }
        else
        {
            nextHeadingLevel = headingLevel is null || headingLevel == 6 ? null : headingLevel + 1;
        }

        var unitName = navUnit.Pop("$240", label).ToStr() ?? label;
        navUnit.Pop("mkfx_id");
        var nestedToc = new List<TocEntry>();

        if (navUnit.Pop("$247", new List<object>()) is List<object> nested)
        {
            foreach (var entry in nested)
            {
                var unit = entry is IonStruct direct ? direct : (IonStruct)GetFragment("$393", entry.ToStr());
                ProcessNavUnit(navType, unit, nestedToc, navContainerName, sectionName, nextHeadingLevel);
            }
        }

        if (navUnit.Pop("$248", new List<object>()) is List<object> entrySets)
        {
            foreach (var entrySetObj in entrySets.OfType<IonStruct>())
            {
                foreach (var entry in (List<object>)entrySetObj.Pop("$247"))
                {
                    var unit = entry is IonStruct direct ? direct : (IonStruct)GetFragment("$393", entry.ToStr());
                    ProcessNavUnit(navType, unit, nestedToc, navContainerName, sectionName, nextHeadingLevel);
                }

                var orientation = entrySetObj.Pop("$215").ToStr();
                if (orientation == "$386")
                {
                    if (OrientationLock != "landscape") nestedToc.Clear();
                }
                else if (orientation == "$385")
                {
                    if (OrientationLock == "landscape") nestedToc.Clear();
                }

                if (!string.IsNullOrEmpty(sectionName) && navType == "$214")
                {
                    for (var i = 0; i < nestedToc.Count; i++)
                    {
                        NavtoAnchor[(sectionName, i)] = nestedToc[i].Anchor;
                    }
                }

                CheckEmpty(entrySetObj, $"nav_container {navContainerName} {navType} entry_set");
            }
        }

        string anchorName = null;
        if (navUnit.ContainsKey("$246"))
        {
            anchorName = $"{navType}_{TocEntryCount}_{unitName}";
            TocEntryCount++;
            var position = GetPosition(navUnit.Pop("$246"));
            RegisterAnchor(anchorName, position, headingLevel);
        }

        if (navType != "$798")
        {
            if (string.IsNullOrEmpty(label) && anchorName is null) ncxToc.AddRange(nestedToc);
            else
            {
                ncxToc.Add(new TocEntry(label, anchor: anchorName, children: nestedToc, description: description,
                    icon: !string.IsNullOrEmpty(icon) ? ProcessExternalResource(icon).Filename : null));
            }
        }

        CheckEmpty(navUnit, $"nav_container {navContainerName} {navType} nav_unit");
    }

    public (string Label, string Icon, string Description) GetRepresentation(IonStruct entry)
    {
        var label = "";
        string icon = null;
        string description = null;
        if (entry.Pop("$241") is IonStruct representation)
        {
            if (representation.Pop("$245") is string iconName)
            {
                icon = iconName;
                ProcessExternalResource(iconName);
                label = iconName;
            }

            if (representation.Pop("$146") is List<object> contentList)
            {
                var div = new XNode("div");
                ProcessContentList(contentList, div, null, WritingMode);
                description = div.IterText().Trim();
            }

            if (representation.Pop("$244") is string text) label = text;
            CheckEmpty(representation, "nav_container representation");
        }

        return (label, icon, description);
    }

    public string UniqueAnchorName(string anchorName)
    {
        if (!string.IsNullOrEmpty(anchorName) && !AnchorPositions.ContainsKey(anchorName)) return anchorName;
        var count = 0;
        while (true)
        {
            var candidate = anchorName + ":" + count;
            if (!AnchorPositions.ContainsKey(candidate)) return candidate;
            count++;
        }
    }

    public string PositionStr((string Id, int Offset) position) => $"{position.Id}.{position.Offset}";

    public string RegisterAnchor(string anchorName, (string, int) position, int? headingLevel = null)
    {
        if (string.IsNullOrEmpty(anchorName)) throw new InvalidOperationException("register_anchor: anchor 名稱為空");
        if (!AnchorPositions.TryGetValue(anchorName, out var positions))
        {
            positions = new HashSet<(string, int)>();
            AnchorPositions[anchorName] = positions;
        }

        positions.Add(position);

        var (eid, offset) = position;
        if (!PositionAnchors.TryGetValue(eid, out var offsets))
        {
            offsets = new Dictionary<int, List<string>>();
            PositionAnchors[eid] = offsets;
        }

        if (!offsets.TryGetValue(offset, out var names))
        {
            names = new List<string>();
            offsets[offset] = names;
        }

        if (!names.Contains(anchorName)) names.Add(anchorName);
        if (headingLevel is not null) AnchorHeadingLevel[anchorName] = headingLevel.Value;
        return GetAnchorId(names[0]);
    }

    public void ReportMissingPositions()
    {
        if (PositionAnchors.Count == 0) return;
        var positions = PositionAnchors.SelectMany(id => id.Value.Keys.Select(offset => $"{id.Key}.{offset}")).ToList();
        Log.Error($"無法定位 {positions.Count} 個被引用的位置：{string.Join(", ", positions.OrderBy(p => p, StringComparer.Ordinal).Take(10))}");
    }

    public string RegisterLinkId(string eid, string kind) => RegisterAnchor($"{kind}_{eid}", (eid, 0));

    public string GetAnchorId(string anchorName)
    {
        if (!AnchorId.TryGetValue(anchorName, out var id))
        {
            id = NameUtil.MakeUniqueName(NameUtil.FixHtmlId(anchorName, IllustratedLayout), AnchorIds);
            AnchorId[anchorName] = id;
            AnchorIds.Add(id);
        }

        return id;
    }

    public List<string> ProcessPosition(string eid, int offset, XNode elem)
    {
        if (eid is null || !PositionAnchors.TryGetValue(eid, out var offsets)) return new List<string>();
        if (!offsets.TryGetValue(offset, out var anchorNames)) return new List<string>();

        if (string.IsNullOrEmpty(elem.Get("id", "")))
        {
            elem.Set("id", GetAnchorId(anchorNames[0]));
        }

        offsets.Remove(offset);
        foreach (var anchorName in anchorNames)
        {
            AnchorElem[anchorName] = elem;
            if (AnchorHeadingLevel.TryGetValue(anchorName, out var level))
            {
                AddStyle(elem, new Style(new Dictionary<string, string> { ["-kfx-heading-level"] = level.ToString() }), replace: false);
            }
        }

        if (offsets.Count == 0) PositionAnchors.Remove(eid);
        return anchorNames;
    }

    public void MoveAnchor(XNode oldElem, XNode newElem)
    {
        foreach (var anchorName in AnchorElem.Keys.ToList())
        {
            if (ReferenceEquals(AnchorElem[anchorName], oldElem)) AnchorElem[anchorName] = newElem;
        }

        if (oldElem.Has("id")) newElem.Set("id", oldElem.Pop("id"));
    }

    public void MoveAnchors(XNode oldRoot, XNode targetElem)
    {
        foreach (var anchorName in AnchorElem.Keys.ToList())
        {
            if (ReferenceEquals(XhtmlUtil.RootElement(AnchorElem[anchorName]), oldRoot)) AnchorElem[anchorName] = targetElem;
        }

        if (oldRoot.Has("id") && !targetElem.Has("id")) targetElem.Set("id", oldRoot.Get("id"));
    }

    public string GetAnchorUri(string anchorName)
    {
        UsedAnchors.Add(anchorName);
        if (AnchorUri.TryGetValue(anchorName, out var uri)) return uri;
        Log.Error($"無法為 anchor 找到 URI：{anchorName}");
        return "/MISSING_ANCHOR_" + NameUtil.FixHtmlId(anchorName, IllustratedLayout);
    }

    public void ReportDuplicateAnchors()
    {
        foreach (var (anchorName, positions) in AnchorPositions)
        {
            if (UsedAnchors.Contains(anchorName) && positions.Count > 1)
            {
                Log.Error($"Anchor {anchorName} 有多個位置");
            }
        }
    }

    public string AnchorAsUri(string anchor) => "anchor:" + anchor;

    public string AnchorFromUri(string uri) => uri[7..];

    public void FixupAnchorsAndHrefs()
    {
        foreach (var (anchorName, elem) in AnchorElem)
        {
            var root = XhtmlUtil.RootElement(elem);
            foreach (var bookPart in BookParts)
            {
                if (ReferenceEquals(bookPart.Html, root))
                {
                    var elemId = elem.Get("id", "");
                    if (string.IsNullOrEmpty(elemId))
                    {
                        elemId = GetAnchorId(anchorName);
                        elem.Set("id", elemId);
                    }

                    AnchorUri[anchorName] = bookPart.Filename + "#" + elemId;
                    break;
                }
            }
        }

        AnchorElem.Clear();

        foreach (var bookPart in BookParts)
        {
            foreach (var e in bookPart.Body().DescendantsAndSelf())
            {
                if (!e.Has("id") || VisibleElementsBefore(e)) continue;
                var uri = bookPart.Filename + "#" + e.Get("id");
                var moved = false;
                var immovable = false;
                foreach (var (anchor, anchorUri) in AnchorUri.ToList())
                {
                    if (anchorUri != uri) continue;
                    if (!ImmovableAnchors.Contains(anchor))
                    {
                        AnchorUri[anchor] = bookPart.Filename;
                        moved = true;
                    }
                    else immovable = true;
                }

                if (moved && !immovable) e.Pop("id");
            }
        }

        foreach (var bookPart in BookParts)
        {
            foreach (var e in bookPart.Body().DescendantsAndSelf())
            {
                if (!e.IsLocal("a")) continue;
                var href = e.Get("href", "");
                if (!href.StartsWith("anchor:")) continue;
                e.Set("href", PathUtil.UrlRelativePath(GetAnchorUri(AnchorFromUri(e.Pop("href"))), bookPart.Filename));
            }
        }

        foreach (var guide in Guide)
        {
            if (guide.Anchor is not null) guide.Target = GetAnchorUri(guide.Anchor);
        }

        foreach (var page in Pagemap)
        {
            if (page.Anchor is not null) page.Target = GetAnchorUri(page.Anchor);
        }

        void ResolveToc(List<TocEntry> toc)
        {
            foreach (var entry in toc)
            {
                if (entry.Anchor is not null) entry.Target = GetAnchorUri(entry.Anchor);
                if (entry.Children.Count > 0) ResolveToc(entry.Children);
            }
        }

        ResolveToc(NcxToc);
    }

    public static bool VisibleElementsBefore(XNode elem, XNode root = null)
    {
        root ??= elem;
        while (root is not null && !root.IsLocal("body")) root = root.Parent;
        if (root is null || ReferenceEquals(elem, root)) return false;

        foreach (var e in root.Descendants())
        {
            if (ReferenceEquals(e, elem)) break;
            if (e.IsLocal("img") || e.IsLocal("br") || e.IsLocal("hr") || e.IsLocal("li") ||
                e.IsLocal("ol") || e.IsLocal("ul") || !string.IsNullOrEmpty(e.Text) ||
                !string.IsNullOrEmpty(e.Tail)) return true;
        }

        return false;
    }

    public static readonly Dictionary<string, string> GuideTypeOfLandmarkType = new()
    {
        ["$233"] = "cover", ["$396"] = "text", ["$269"] = "text", ["$212"] = "toc"
    };
}
