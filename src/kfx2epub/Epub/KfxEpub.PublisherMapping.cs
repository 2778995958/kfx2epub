using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

// 以出版社樣式表（style-kadokawa.css／style-karc.css）為語彙，把奥付／扉上還沒對應的列
// 換成 author／label 等語意 class。
//
// 判定方式不是比對特定文字或 class 名稱，而是：
//   1. 該元素剩下的宣告，全部都能在某一條出版社規則裡找到；
//   2. 可縮放的長度（em／%）共用同一個比例（容差 0.1%），px 與 font-size 必須 1:1。
// 同一頁同一個 publisher class 只會使用一次，避免兩個元素搶同一條規則。
public partial class KfxEpub
{
    private sealed record PublisherRule(
        string PageType, string ParentClass, string Tag, string ClassName, Style Declarations, int Order);

    // 平行轉換時多個執行緒會同時取用，交給 Lazy<T> 確保只載入一次。
    private static readonly Lazy<List<PublisherRule>> _publisherRules = new(LoadPublisherRules);

    private static List<PublisherRule> PublisherRules => _publisherRules.Value;

    // 版面骨架由頁型 class 負責，不作为比對對象。
    private static readonly HashSet<string> PublisherStructuralClasses = new(StringComparer.Ordinal)
    {
        "main"
    };

    private static List<PublisherRule> LoadPublisherRules()
    {
        var rules = new List<PublisherRule>();
        foreach (var file in new[] { "style-kadokawa.css", "style-karc.css" })
        {
            var css = Encoding.UTF8.GetString(ReadBundledStyle(file));
            css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
            css = Regex.Replace(css, @"@charset[^;]*;", "");
            css = Regex.Replace(css, @"@import[^;]*;", "");
            foreach (Match match in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
            {
                var parts = match.Groups[1].Value
                    .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length is < 2 or > 3) continue;
                if (!parts[0].StartsWith(".p-", StringComparison.Ordinal)) continue;
                if (!parts[1].StartsWith(".", StringComparison.Ordinal)) continue;

                var pageType = parts[0][1..];
                string parent = null;
                string tag = null;
                string className = null;
                if (parts.Length == 2)
                {
                    className = parts[1][1..];
                }
                else if (parts[2].StartsWith(".", StringComparison.Ordinal))
                {
                    parent = parts[1][1..];
                    className = parts[2][1..];
                }
                else
                {
                    parent = parts[1][1..];
                    tag = parts[2];
                }

                var declarations = ParseDeclarations(match.Groups[2].Value);
                if (declarations.Count == 0) continue;
                rules.Add(new PublisherRule(pageType, parent, tag, className,
                    new Style(declarations), rules.Count));
            }
        }

        return rules;
    }

    private static Dictionary<string, string> ParseDeclarations(string body)
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in body.Split(';'))
        {
            var index = part.IndexOf(':');
            if (index < 0) continue;
            var name = part[..index].Trim().ToLowerInvariant();
            var value = Regex.Replace(part[(index + 1)..].Trim(), @"\s+", " ");
            if (name.Length == 0 || value.Length == 0) continue;
            raw[name] = value;
        }

        return ExpandShorthands(raw);
    }

    private static Dictionary<string, string> ExpandShorthands(Dictionary<string, string> raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in raw)
        {
            if (name is "margin" or "padding")
            {
                var values = BoxValues(value);
                if (values is null) continue;
                var sides = new[] { "top", "right", "bottom", "left" };
                for (var i = 0; i < 4; i++) result[$"{name}-{sides[i]}"] = values[i];
                continue;
            }

            if (name is "border-style" or "border-width" or "border-color")
            {
                var values = BoxValues(value);
                if (values is null) continue;
                var suffix = name["border-".Length..];
                var sides = new[] { "top", "right", "bottom", "left" };
                for (var i = 0; i < 4; i++) result[$"border-{sides[i]}-{suffix}"] = values[i];
                continue;
            }

            var borderMatch = Regex.Match(name, @"^border(?:-(top|right|bottom|left))?$");
            if (borderMatch.Success)
            {
                var prefix = borderMatch.Groups[1].Success ? $"border-{borderMatch.Groups[1].Value}-" : "border-";
                foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token is "solid" or "dashed" or "dotted" or "double" or "none" or "hidden")
                        result[prefix + "style"] = token;
                    else if (Regex.IsMatch(token, @"^\d+(?:\.\d+)?px$") || token is "thin" or "medium" or "thick")
                        result[prefix + "width"] = token;
                    else
                        result[prefix + "color"] = token;
                }

                continue;
            }

            result[name] = value;
        }

        return result;
    }

    private static string[] BoxValues(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => new[] { parts[0], parts[0], parts[0], parts[0] },
            2 => new[] { parts[0], parts[1], parts[0], parts[1] },
            3 => new[] { parts[0], parts[1], parts[2], parts[1] },
            4 => parts,
            _ => null
        };
    }

    // 把奥付／扉上剩下的宣告換成 publisher class；必須在頁型 class 套用之後、CSS 產生之前執行。
    public void ApplyPublisherClassMapping()
    {
        if (!UsesPublisherCss()) return;
        var rules = PublisherRules;
        if (rules.Count == 0) return;

        foreach (var bookPart in BookParts)
        {
            if (bookPart.IsNav || bookPart.IsFxl) continue;
            var body = bookPart.Body();
            var pageType = PageTypeClass(body);
            if (pageType is null) continue;

            var used = new HashSet<string>(StringComparer.Ordinal);
            var mappedParents = new List<(XNode Element, string ClassName)>();

            // publisher class 在樣式表裡是固定順序，和頁面上的區塊順序一致。
            // 依序掃描區塊、只在順序往後推進時取用，避免兩個區塊搶同一條規則；
            // 沒有殘餘宣告的區塊也一樣推進游標，讓後面的區塊對到正確的規則。
            var container = body.DescendantsAndSelf().FirstOrDefault(e =>
                (e.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("main")) ?? body;

            var blocks = container.Children;
            var blockFonts = new double?[blocks.Count];
            var blockResiduals = new Style[blocks.Count];
            var blockRules = new PublisherRule[blocks.Count];

            var cursor = -1;
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                var generated = GeneratedClassName(block);
                var residual = (Style)null;
                if (generated is not null) CssRules.TryGetValue(ClassSelector(generated), out residual);
                var fontPercent = ElementFontPercent(block);
                blockFonts[i] = fontPercent;
                blockResiduals[i] = residual;

                PublisherRule chosen = null;
                foreach (var rule in rules)
                {
                    if (rule.PageType != pageType) continue;
                    if (rule.ParentClass is not null || rule.ClassName is null) continue;
                    if (rule.Order <= cursor) continue;
                    if (PublisherStructuralClasses.Contains(rule.ClassName)) continue;
                    if (used.Contains(rule.ClassName)) continue;
                    if (!FontCompatible(rule.Declarations, fontPercent)) continue;

                    if (residual is not null && residual.Count > 0)
                    {
                        if (!CoversResidual(residual, rule.Declarations, fontPercent, out var ratios)) continue;
                        if (ratios.Count < 1 || !RatiosConsistent(ratios)) continue;
                    }

                    chosen = rule;
                    break;
                }

                if (chosen is null) continue;
                cursor = chosen.Order;

                if (residual is null || residual.Count == 0) continue;
                blockRules[i] = chosen;
                AddClass(block, chosen.ClassName);
                RemoveClass(block, generated);
                used.Add(chosen.ClassName);
                mappedParents.Add((block, chosen.ClassName));
            }

            // 出版社樣式把同一組的後續行包在一個 div 裡，讓它們繼承 line-height（多為 1.0）。
            // 我們的結構是平鋪的，所以直接把同樣的行高套在後續同字級的區塊上。
            for (var i = 0; i < blocks.Count; i++)
            {
                var rule = blockRules[i];
                if (rule is null) continue;
                var lineHeight = LineHeightClass(rule.Declarations.Get("line-height"));
                if (lineHeight is null) continue;

                for (var j = i + 1; j < blocks.Count; j++)
                {
                    if (blockRules[j] is not null) break;
                    if (blockFonts[j] != blockFonts[i]) break;
                    AddClass(blocks[j], lineHeight);
                }
            }

            // 已經掛上 publisher class 的區塊，其子元素（例如 .author p）也一併收斂。
            foreach (var (parent, className) in mappedParents)
            {
                foreach (var child in parent.Descendants())
                {
                    var generated = GeneratedClassName(child);
                    if (generated is null) continue;
                    if (!CssRules.TryGetValue(ClassSelector(generated), out var residual)) continue;

                    foreach (var rule in rules)
                    {
                        if (rule.PageType != pageType) continue;
                        if (rule.ParentClass != className) continue;
                        if (rule.Tag is not null && rule.Tag != child.Name) continue;
                        if (!CoversResidual(residual, rule.Declarations, ElementFontPercent(child), out var ratios))
                            continue;
                        if (ratios.Count < 1 || !RatiosConsistent(ratios)) continue;

                        if (rule.ClassName is not null && !used.Contains(rule.ClassName))
                        {
                            AddClass(child, rule.ClassName);
                            used.Add(rule.ClassName);
                        }

                        RemoveClass(child, generated);
                        break;
                    }
                }
            }
        }
    }

    private static string PageTypeClass(XNode body)
    {
        var classes = (body.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return classes.FirstOrDefault(c => c is "p-colophon" or "p-colophon2" or "p-titlepage");
    }

    private string GeneratedClassName(XNode elem)
    {
        foreach (var className in (elem.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CssRules.ContainsKey(ClassSelector(className))) return className;
        }

        return null;
    }

    private static double? ElementFontPercent(XNode elem)
    {
        foreach (var className in (elem.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var per = Regex.Match(className, @"^font-(\d{3})per$");
            if (per.Success) return int.Parse(per.Groups[1].Value, CultureInfo.InvariantCulture);

            var em = Regex.Match(className, @"^font-(\d+)em(\d*)$");
            if (em.Success)
            {
                var whole = em.Groups[1].Value;
                var fraction = em.Groups[2].Value;
                var text = fraction.Length == 0 ? whole : $"{whole}.{fraction}";
                return double.Parse(text, CultureInfo.InvariantCulture) * 100;
            }
        }

        return null;
    }

    private static bool CoversResidual(Style residual, Style candidate, double? fontPercent,
        out List<double> ratios)
    {
        ratios = new List<double>();
        foreach (var (name, value) in residual.Properties)
        {
            var other = candidate.Get(name);
            if (other is null) return false;

            if (name == "font-size")
            {
                if (!TryFontPercent(value, out var a) || !TryFontPercent(other, out var b)) return false;
                if (Math.Abs(a - b) > 0.5) return false;
                continue;
            }

            if (TryLength(value, out var current, out var currentUnit) &&
                TryLength(other, out var reference, out var referenceUnit) && currentUnit == referenceUnit)
            {
                if (referenceUnit == "px")
                {
                    if (Math.Abs(current - reference) > 1e-6) return false;
                }
                else if (reference > 0)
                {
                    ratios.Add(current / reference);
                }

                continue;
            }

            if (!string.Equals(value.Trim(), other.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (fontPercent is not null && candidate.Get("font-size") is { } candidateFont &&
            TryFontPercent(candidateFont, out var candidatePercent) &&
            Math.Abs(fontPercent.Value - candidatePercent) > 0.5)
        {
            return false;
        }

        return true;
    }

    private static string LineHeightClass(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().ToLowerInvariant();
        if (text == "normal") return "line-height-normal";
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        var name = "line-height-" + (Math.Abs(number - Math.Truncate(number)) < 1e-9
            ? ((int)number) + "em"
            : number.ToString("0.00", CultureInfo.InvariantCulture).Replace(".", "em"));
        return UtilityClassNames.Contains(name) ? name : null;
    }

    private static bool FontCompatible(Style declarations, double? fontPercent)
    {
        if (fontPercent is null) return true;
        var value = declarations.Get("font-size");
        if (value is null) return true;
        return TryFontPercent(value, out var percent) && Math.Abs(fontPercent.Value - percent) <= 0.5;
    }

    private static bool RatiosConsistent(List<double> ratios)
        => ratios.Count > 0 && ratios.Max() - ratios.Min() <= 1e-3;

    private static bool TryLength(string value, out double amount, out string unit)
    {
        amount = 0;
        unit = null;
        var match = Regex.Match(value ?? "", @"^(-?\d+(?:\.\d+)?)(em|rem|%|px)$");
        if (!match.Success) return false;
        amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        unit = match.Groups[2].Value;
        if (unit == "rem") unit = "em";
        return true;
    }

    private static bool TryFontPercent(string value, out double percent)
    {
        percent = 0;
        if (!TryLength(value, out var amount, out var unit)) return false;
        percent = unit switch
        {
            "%" => amount,
            "em" => amount * 100,
            "px" => amount / 16 * 100,
            _ => 0
        };
        return true;
    }
}
