using System.Globalization;
using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

public partial class KfxEpub
{
    public Dictionary<string, (int? Nargs, object Func)> ConditionOperators { get; } = new();
    public HashSet<string> KnownSupportedFeatures { get; } = new()
    {
        "($826,)", "($827,)", "($660,)", "($751,)", "($664,crop_bleed,1)"
    };

    public void SetConditionOperators()
    {
        ConditionOperators.Clear();
        var screenWidth = OrientationLock == "landscape" ? 1920 : 1200;
        var screenHeight = OrientationLock == "landscape" ? 1200 : 1920;
        ConditionOperators["$305"] = (0, screenHeight);
        ConditionOperators["$304"] = (0, screenWidth);
        ConditionOperators["$300"] = (0, true);
        ConditionOperators["$301"] = (0, true);
        ConditionOperators["$183"] = (0, 0);
        ConditionOperators["$302"] = (0, screenWidth);
        ConditionOperators["$303"] = (0, screenHeight);
        ConditionOperators["$525"] = (0, screenWidth > screenHeight);
        ConditionOperators["$526"] = (0, screenWidth < screenHeight);
        ConditionOperators["$660"] = (0, true);
        ConditionOperators["$293"] = (1, "not");
        ConditionOperators["$266"] = (1, "anchor");
        ConditionOperators["$750"] = (1, "layout");
        ConditionOperators["$659"] = (null, null);
        ConditionOperators["$292"] = (2, "and");
        ConditionOperators["$291"] = (2, "or");
        ConditionOperators["$294"] = (2, "eq");
        ConditionOperators["$295"] = (2, "ne");
        ConditionOperators["$296"] = (2, "gt");
        ConditionOperators["$297"] = (2, "ge");
        ConditionOperators["$298"] = (2, "lt");
        ConditionOperators["$299"] = (2, "le");
        ConditionOperators["$516"] = (2, "add");
        ConditionOperators["$517"] = (2, "sub");
        ConditionOperators["$518"] = (2, "mul");
        ConditionOperators["$519"] = (2, "div");
    }

    public bool EvaluateBinaryCondition(object condition)
    {
        var value = EvaluateCondition(condition);
        if (value is bool boolean) return boolean;
        Log.Error("條件的結果不是布林值");
        return false;
    }

    public object EvaluateCondition(object condition)
    {
        string op;
        List<object> args;
        if (condition is IonSExp sexp)
        {
            op = sexp.Count > 0 ? IonUtil.SymbolValue(sexp[0]) : null;
            args = sexp.Skip(1).ToList();
        }
        else
        {
            op = IonUtil.SymbolValue(condition);
            args = new List<object>();
        }

        if (op is null || !ConditionOperators.TryGetValue(op, out var info))
        {
            Log.Error("未知的條件運算子：" + op);
            return false;
        }

        if (info.Nargs is null)
        {
            if (op == "$659")
            {
                var key = "(" + string.Join(",", args.Select(IonUtil.SymbolValue)) + ",)";
                if (KnownSupportedFeatures.Contains(key)) return true;
                Log.Error("未知的 yj.supports 功能：" + key);
                return false;
            }

            return false;
        }

        if (info.Nargs != args.Count) return false;
        if (info.Nargs == 0) return info.Func;
        if (info.Nargs == 1)
        {
            if (op == "$266") return 0;
            if (op == "$750") return IonUtil.SymbolValue(args[0]) == "$752";
            var val = EvaluateCondition(args[0]);
            return op == "$293" ? !(val is bool b && b) : val;
        }

        var left = EvaluateCondition(args[0]);
        var right = EvaluateCondition(args[1]);
        return op switch
        {
            "$292" => (left is bool lb && lb) && (right is bool rb && rb),
            "$291" => (left is bool lo && lo) || (right is bool ro && ro),
            "$294" => Equals(left, right),
            "$295" => !Equals(left, right),
            "$296" => Compare(left, right) > 0,
            "$297" => Compare(left, right) >= 0,
            "$298" => Compare(left, right) < 0,
            "$299" => Compare(left, right) <= 0,
            "$516" => Convert.ToDecimal(left) + Convert.ToDecimal(right),
            "$517" => Convert.ToDecimal(left) - Convert.ToDecimal(right),
            "$518" => Convert.ToDecimal(left) * Convert.ToDecimal(right),
            "$519" => Convert.ToDecimal(left) / Convert.ToDecimal(right),
            _ => false
        };
    }

    private static int Compare(object left, object right)
    {
        if (left is IComparable l && right is IComparable r)
        {
            try
            {
                return l.CompareTo(Convert.ChangeType(r, l.GetType()));
            }
            catch
            {
                return string.CompareOrdinal(left.ToString(), right.ToString());
            }
        }

        return string.CompareOrdinal(left?.ToString(), right?.ToString());
    }

    public void AddSvgWrapperToBlockImage(XNode contentElem, BookPart bookPart, int fixedHeight = 0, int fixedWidth = 0)
    {
        if (contentElem.Children.Count != 1) return;
        var imageDiv = contentElem.Children[0];
        if (!imageDiv.IsLocal("div") || imageDiv.Children.Count != 1 || !imageDiv.Children[0].IsLocal("img")) return;
        var img = imageDiv.Children[0];
        var imgFile = OebpsFiles.GetValueOrDefault(PathUtil.GetUrlFilename(PathUtil.UrlAbsolutePath(img.Get("src"), bookPart.Filename)));
        if (imgFile is null) return;
        var intHeight = imgFile.Height ?? 0;
        var intWidth = imgFile.Width ?? 0;
        if (intHeight == 0 || intWidth == 0) return;

        imageDiv.RemoveChild(img);
        var svg = XhtmlUtil.SubElementNs(imageDiv, XmlNamespaces.Svg, "svg");
        svg.NsMap[""] = XmlNamespaces.Svg;
        svg.NsMap["xlink"] = XmlNamespaces.XLink;
        svg.Set("version", "1.1").Set("preserveAspectRatio", "xMidYMid meet")
            .Set("viewBox", $"0 0 {intWidth} {intHeight}").Set("height", "100%").Set("width", "100%");
        MoveAnchors(img, svg);
        var image = XhtmlUtil.SubElementNs(svg, XmlNamespaces.Svg, "image");
        image.SetNs(XmlNamespaces.XLink, "href", img.Get("src"));
        image.Set("height", intHeight.ToString()).Set("width", intWidth.ToString());
    }

    public void ProcessKvgShape(XNode parent, IonStruct shape, List<object> contentList, BookPart bookPart, string writingMode)
    {
        var shapeType = shape.Pop("$159").ToStr();
        XNode elem;
        if (shapeType == "$273" || shapeType == "line" || shapeType == "$837")
        {
            elem = XhtmlUtil.SubElementNs(parent, XmlNamespaces.Svg, "path");
            elem.Set("d", ProcessPath(shape.Pop("$249")));
        }
        else if (shapeType == "rectangle" || shapeType == "$836") elem = XhtmlUtil.SubElementNs(parent, XmlNamespaces.Svg, "rect");
        else if (shapeType == "ellipse" || shapeType == "$835") elem = XhtmlUtil.SubElementNs(parent, XmlNamespaces.Svg, "ellipse");
        else if (shapeType == "polygon" || shapeType == "$838") elem = XhtmlUtil.SubElementNs(parent, XmlNamespaces.Svg, "polygon");
        else if (shapeType == "polyline" || shapeType == "$839") elem = XhtmlUtil.SubElementNs(parent, XmlNamespaces.Svg, "polyline");
        else
        {
            Log.Error("不支援的 KVG shape：" + shapeType);
            return;
        }

        var dimensions = shape.Pop("shape_dimensions", shape.Pop("$840", new IonStruct())) as IonStruct;
        var dimensionNames = new Dictionary<string, string>
        {
            ["cx"] = "cx", ["$843"] = "cx", ["cy"] = "cy", ["$844"] = "cy", ["end_x"] = "x2", ["$849"] = "x2",
            ["end_y"] = "y2", ["$850"] = "y2", ["$57"] = "height", ["radius_x"] = "rx", ["$845"] = "rx",
            ["radius_y"] = "ry", ["$846"] = "ry", ["start_x"] = "x1", ["$847"] = "x1", ["start_y"] = "y1",
            ["$848"] = "y1", ["vertex_list"] = "points", ["$851"] = "points", ["$56"] = "width", ["x"] = "x",
            ["$841"] = "x", ["y"] = "y", ["$842"] = "y"
        };
        if (dimensions is not null)
        {
            foreach (var pair in dimensions.ToList())
            {
                if (dimensionNames.TryGetValue(pair.Key, out var name)) elem.Set(name, PropertyValue(pair.Key, pair.Value, svg: true));
            }
        }

        var propertyNames = new Dictionary<string, string>
        {
            ["$498"] = "stroke", ["$70"] = "fill", ["$72"] = "fill-opacity", ["$75"] = "stroke",
            ["$531"] = "stroke-dasharray", ["$532"] = "stroke-dashoffset", ["$77"] = "stroke-linecap",
            ["$529"] = "stroke-linejoin", ["$530"] = "stroke-miterlimit", ["$76"] = "stroke-width", ["$98"] = "transform"
        };
        foreach (var pair in shape.ToList())
        {
            if (propertyNames.TryGetValue(pair.Key, out var name))
                elem.Set(name, PropertyValue(pair.Key, shape.Pop(pair.Key), svg: true, transformMatrixSwap: true));
        }

        if (elem.Has("stroke") && !elem.Has("fill")) elem.Set("fill", "none");
    }

    public string ProcessPath(object path)
    {
        if (path is IonStruct st)
        {
            var bundleName = st.Pop("name").ToStr();
            var index = Convert.ToInt32(st.Pop("$403"));
            if (BookData.TryGetValue("$692", out var bundles) && bundles is FragmentMap map &&
                map.TryGetValue(bundleName, out var bundle) && bundle is IonStruct b && b.GetValueOrDefault("$693") is List<object> paths)
            {
                return ProcessPath(paths[index]);
            }

            return "";
        }

        var list = ((List<object>)path).Select(Convert.ToDecimal).ToList();
        var result = new List<string>();
        var i = 0;
        void Instruction(string code, int count, bool pixels = true)
        {
            result.Add(code);
            for (var j = 0; j < count; j++)
            {
                if (i >= list.Count) return;
                var value = list[i++];
                if (pixels) value = AdjustPixelValue(value);
                result.Add(TextUtil.ValueStr(value));
            }
        }

        while (i < list.Count)
        {
            var inst = (int)list[i++];
            switch (inst)
            {
                case 0: Instruction("M", 2); break;
                case 1: Instruction("L", 2); break;
                case 2: Instruction("Q", 4); break;
                case 3: Instruction("C", 6); break;
                case 4: Instruction("Z", 0); break;
                default: return string.Join(" ", result);
            }
        }

        return string.Join(" ", result);
    }

    public string ProcessPolygon(List<object> path)
    {
        var values = path.Select(Convert.ToDecimal).ToList();
        var points = new List<string>();
        var i = 0;
        while (i < values.Count)
        {
            var inst = (int)values[i];
            if (inst is 0 or 1)
            {
                if (i + 3 > values.Count) break;
                points.Add($"{TextUtil.ValueStr(values[i + 1] * 100, "%", true)} {TextUtil.ValueStr(values[i + 2] * 100, "%", true)}");
                i += 3;
            }
            else if (inst == 4) i++;
            else break;
        }

        return "polygon(" + string.Join(", ", points) + ")";
    }

    public string ProcessVertexList(List<object> vertices)
    {
        var points = new List<string>();
        for (var i = 0; i + 1 < vertices.Count; i += 2)
        {
            points.Add($"{vertices[i]},{vertices[i + 1]}");
        }

        return string.Join(" ", points);
    }

    public string ProcessTransform(List<object> values, bool svg = false, bool transformMatrixSwap = false)
    {
        var vals = values.Select(Convert.ToDecimal).ToList();
        var px = svg ? "" : "px";
        var sep = svg ? " " : ",";
        var deg = svg ? "" : "deg";
        if (vals.Count != 6) return "?";

        if (transformMatrixSwap) (vals[1], vals[2]) = (vals[2], vals[1]);
        vals[4] = AdjustPixelValue(vals[4]);
        vals[5] = AdjustPixelValue(vals[5]);

        var translate = vals[4] == 0 && vals[5] == 0
            ? ""
            : $"translate({TextUtil.ValueStr(vals[4], px)}{sep}{TextUtil.ValueStr(vals[5], px)}) ";
        if (vals.Take(4).SequenceEqual(new[] { 1m, 0m, 0m, 1m }) && translate.Length > 0) return translate.Trim();
        if (vals[1] == 0 && vals[2] == 0)
        {
            if (vals[0] == vals[3]) return translate + $"scale({TextUtil.ValueStr(vals[0])})";
            return translate + $"scale({TextUtil.ValueStr(vals[0])}{sep}{TextUtil.ValueStr(vals[3])})";
        }

        if (vals[0] == vals[3] && vals[1] == -vals[2] && vals[0] is >= -1 and <= 1 && vals[1] is >= -1 and <= 1)
        {
            if (vals.Take(4).SequenceEqual(new[] { 0m, 1m, -1m, 0m })) return translate + $"rotate(-90{deg})";
            if (vals.Take(4).SequenceEqual(new[] { 0m, -1m, 1m, 0m })) return translate + $"rotate(90{deg})";
            if (vals.Take(4).SequenceEqual(new[] { -1m, 0m, 0m, -1m })) return translate + $"rotate(180{deg})";
        }

        return "matrix(" + string.Join(sep, vals.Select(v => TextUtil.ValueStr(v))) + ")";
    }

    public int? PixelValue(object value, bool adjust = true)
    {
        if (value is null) return null;
        if (value is IonStruct st)
        {
            st.Pop("$306");
            value = st.Pop("$307");
        }

        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        var pixel = adjust ? AdjustPixelValue(number) : number;
        return (int)Math.Round(pixel, MidpointRounding.AwayFromZero);
    }

    public void ProcessDictionaryRules()
    {
        DictionaryRules.Clear();
        if (DictionaryAuxDataId is null) return;
        if (!BookData.TryGetValue("$597", out var auxValue) || auxValue is not FragmentMap aux ||
            !aux.TryGetValue(DictionaryAuxDataId, out var dictValue) || dictValue is not IonStruct dictionary)
        {
            Log.Warning("缺少 yj.dictionary auxiliary_data：" + DictionaryAuxDataId);
            return;
        }

        // 字典變化規則以 IonText 儲存；目前未移植 IonText，僅保留空規則。
        Log.Info("字典變化規則解析尚未支援，略過。");
    }

    public string UnapplyDictionaryRule(string word, string rule)
    {
        foreach (var instr in rule.Split(';').AsEnumerable().Reverse())
        {
            if (string.IsNullOrEmpty(instr)) continue;
            var match = Regex.Match(instr, "^([0-9]+)(.)(.+)$");
            if (!match.Success) break;
            var offset = int.Parse(match.Groups[1].Value);
            var cmd = match.Groups[2].Value;
            var text = match.Groups[3].Value;
            var endOffset = word.Length - offset;
            if (cmd == "-") word = word[..endOffset] + text + word[endOffset..];
            else if (cmd == "+")
            {
                var cut = word.Substring(Math.Max(0, endOffset - text.Length), Math.Min(text.Length, endOffset));
                if (cut != text) break;
                word = word[..(endOffset - text.Length)] + word[endOffset..];
            }
            else if (cmd == "/") word = word[..offset] + text + word[offset..];
            else if (cmd == "*")
            {
                if (offset + text.Length > word.Length || word.Substring(offset, text.Length) != text) break;
                word = word[..offset] + word[(offset + text.Length)..];
            }
            else break;
        }

        return word;
    }

    public void ProcessPlugin(string resourceName, string altText, XNode contentElem, BookPart bookPart, bool isHtml = false)
    {
        var resource = ProcessExternalResource(resourceName, save: false, isPlugin: true);
        if (isHtml || resource.Mime == "plugin/kfx-html-article")
        {
            var src = PathUtil.UrlRelativePath(ProcessExternalResource(resourceName, isPlugin: true, saveReferred: true).Filename,
                bookPart.Filename);
            contentElem.Name = "iframe";
            contentElem.Set("src", src);
            AddStyle(contentElem, new Style(new Dictionary<string, string>
            {
                ["height"] = "100%", ["width"] = "100%", ["border-bottom-style"] = "none",
                ["border-left-style"] = "none", ["border-right-style"] = "none", ["border-top-style"] = "none"
            }));
        }
        else
        {
            contentElem.Name = "img";
            contentElem.Set("src", PathUtil.UrlRelativePath(resource.Filename, bookPart.Filename));
            contentElem.Set("alt", altText ?? "");
        }
    }

    public void FixVerticalAlignProperties(XNode contentElem, Style contentStyle, bool setStyleIfChanged = false)
    {
        // 目前保留基本的 vertical-align 處理。
        foreach (var prop in new[] { "-kfx-baseline-shift", "-kfx-baseline-style", "-kfx-table-vertical-align" })
        {
            if (!contentStyle.ContainsKey(prop)) continue;
            var value = contentStyle.Pop(prop);
            if (!contentStyle.ContainsKey("vertical-align")) contentStyle.Set("vertical-align", value);
        }

        if (setStyleIfChanged) SetStyle(contentElem, contentStyle);
    }
}
