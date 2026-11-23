using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

public sealed class Style
{
    public Dictionary<string, string> Properties { get; private set; }
    private string _styleStr;

    public Style(Dictionary<string, string> properties, string styleStr = null)
    {
        Properties = new Dictionary<string, string>(properties, StringComparer.Ordinal);
        _styleStr = styleStr;
    }

    public Style(string styleStr)
    {
        Properties = ParseProperties(styleStr);
        _styleStr = styleStr;
    }

    public Style(Style other)
    {
        Properties = new Dictionary<string, string>(other.Properties, StringComparer.Ordinal);
        _styleStr = other._styleStr;
    }

    public int Count => Properties.Count;

    public bool ContainsKey(string key) => Properties.ContainsKey(key);

    public string Get(string key, string defaultValue = null)
        => Properties.TryGetValue(key, out var value) ? value : defaultValue;

    public string this[string key]
    {
        get => Properties[key];
        set
        {
            Properties[key] = value;
            _styleStr = null;
        }
    }

    public void Set(string key, string value)
    {
        Properties[key] = value;
        _styleStr = null;
    }

    public string Pop(string key, string defaultValue = null)
    {
        if (!Properties.TryGetValue(key, out var value)) return defaultValue;
        Properties.Remove(key);
        _styleStr = null;
        return value;
    }

    public Style Update(Style other, bool? replace = null, bool throwOnConflict = false)
    {
        foreach (var (name, value) in other.Properties)
        {
            if (Properties.TryGetValue(name, out var existing) && existing != value)
            {
                if (throwOnConflict) throw new InvalidOperationException($"樣式屬性衝突：{name} = {existing} >> {value}");
                if (replace == false) continue;
                if (replace is null) Log.Error($"樣式屬性值衝突：{name} = {existing} >> {value}");
            }

            Properties[name] = value;
            _styleStr = null;
        }

        return this;
    }

    public Style Partition(ICollection<string> propertyNames = null, string namePrefix = null, bool removePrefix = false,
        bool addPrefix = false, bool keep = false, bool keepAll = false, bool modify = true)
    {
        var matchProps = new Dictionary<string, string>();
        var otherProps = new Dictionary<string, string>();
        foreach (var (name, value) in Properties)
        {
            var compareName = name;
            bool match;
            if (namePrefix is not null)
            {
                if (addPrefix)
                {
                    compareName = namePrefix + "-" + name;
                    match = true;
                }
                else match = name.StartsWith(namePrefix, StringComparison.Ordinal);

                if (match && removePrefix) compareName = compareName[namePrefix.Length..];
            }
            else
            {
                match = propertyNames is not null && propertyNames.Contains(name);
            }

            if (match) matchProps[compareName] = value;
            else otherProps[name] = value;
        }

        if (keep && modify)
        {
            Properties = matchProps;
            _styleStr = null;
        }

        if (keep || keepAll) return new Style(otherProps);
        if (modify)
        {
            Properties = otherProps;
            _styleStr = null;
        }

        return new Style(matchProps);
    }

    public override string ToString()
    {
        _styleStr ??= string.Join("; ", Properties.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}: {p.Value}"));
        return _styleStr;
    }

    public static Dictionary<string, string> ParseProperties(string styleStr)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(styleStr)) return result;

        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        foreach (var c in styleStr)
        {
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            if (c == ';' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }

        if (current.Length > 0) parts.Add(current.ToString());
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            result[trimmed[..colon].Trim()] = trimmed[(colon + 1)..].Trim();
        }

        return result;
    }
}

public partial class KfxEpub
{
    public Dictionary<string, Style> CssRules { get; set; } = new();
    // 轉換成電書協 utility class 時，若該 class 不存在於樣式表（例如 95% 字級），補在 style-kfx.css。
    public Dictionary<string, string> ExtraCssRules { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, Style>> MediaQueries { get; } = new();
    public List<Style> FontFaces { get; } = new();
    public Dictionary<string, string> FontNameReplacements { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FixedFontNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PresentFontNames { get; } = new();
    public HashSet<string> UsedFontNames { get; } = new();
    public Dictionary<string, string> FileIds { get; } = new();
    public Style NonHeritableDefaultProperties { get; set; }
    public Dictionary<string, string> LastKfxHeadingLevelHolder { get; set; }
    public string LastKfxHeadingLevel = "1";

    static KfxEpub()
    {
        // 預先註冊常見字型名稱，避免第一次使用時才建立。
    }

    public KfxEpub()
    {
        foreach (var name in PropertyData.GenericFontNames) FixFontName(name, add: true, generic: true);
    }

    public Style NewStyle(Dictionary<string, string> properties = null) => new(properties ?? new Dictionary<string, string>());

    public Style ProcessContentProperties(IonStruct content)
    {
        _pendingPropertyValues.Clear();
        foreach (var name in content.Keys.ToList())
        {
            if (PropertyData.Properties.ContainsKey(name))
            {
                _pendingPropertyValues[name] = content.Pop(name);
            }
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in _pendingPropertyValues)
        {
            var converted = PropertyValue(name, value);
            if (converted is null) continue;
            var propertyName = PropertyData.Properties.TryGetValue(name, out var info)
                ? info.Name
                : name.Replace('_', '-');
            dict[propertyName] = converted;
        }

        _pendingPropertyValues.Clear();
        FinalizeDeclarations(dict);
        return new Style(dict);
    }

    private readonly Dictionary<string, object> _pendingPropertyValues = new();

    public Style ConvertYjProperties(IonStruct yjProperties)
    {
        var declarations = new Dictionary<string, string>();
        foreach (var (name, value) in yjProperties.ToList())
        {
            var converted = PropertyValue(name, value);
            var property = PropertyData.Properties.TryGetValue(name, out var info) ? info.Name : name.Replace('_', '-');
            if (property == "position" && converted is "oeb-page-foot" or "oeb-page-head")
            {
                property = GenerateEpub2 ? "display" : null;
            }

            if (property is not null && converted is not null)
            {
                if (declarations.TryGetValue(property, out var existing) && existing != converted)
                {
                    if (property == "-kfx-attrib-epub-type")
                    {
                        var vals = existing.Split(' ').Concat(converted.Split(' ')).ToHashSet();
                        if (vals.Any(v => !v.StartsWith("amzn:")))
                        {
                            Log.Error($"屬性 {property} 有多個不相容值");
                        }
                        else
                        {
                            converted = string.Join(" ", vals.OrderBy(v => v, StringComparer.Ordinal));
                        }
                    }
                    else if (property == "text-decoration")
                    {
                        converted = string.Join(" ", existing.Split(' ').Concat(converted.Split(' '))
                            .ToHashSet().OrderBy(v => v, StringComparer.Ordinal));
                    }
                    else
                    {
                        Log.Error($"屬性 {property} 有多個值：{existing} / {converted}");
                    }
                }

                declarations[property] = converted;
            }
        }

        FinalizeDeclarations(declarations);
        return new Style(declarations);
    }

    private void FinalizeDeclarations(Dictionary<string, string> declarations)
    {
        if (declarations.GetValueOrDefault("letter-spacing") is "0" or "0em") declarations["letter-spacing"] = "normal";
        if (declarations.GetValueOrDefault("word-spacing") is "0" or "0em") declarations["word-spacing"] = "normal";

        if (declarations.GetValueOrDefault("text-combine-upright") == "all")
        {
            TextCombineInUse = true;
            if (declarations.GetValueOrDefault("writing-mode") == "horizontal-tb") declarations.Remove("writing-mode");
        }

        if (declarations.ContainsKey("-kfx-background-positionx") || declarations.ContainsKey("-kfx-background-positiony"))
        {
            declarations["background-position"] =
                $"{declarations.Pop("-kfx-background-positionx", "50%")} {declarations.Pop("-kfx-background-positiony", "50%")}";
        }

        if (declarations.ContainsKey("-kfx-background-sizex") || declarations.ContainsKey("-kfx-background-sizey"))
        {
            declarations["background-size"] =
                $"{declarations.Pop("-kfx-background-sizex", "auto")} {declarations.Pop("-kfx-background-sizey", "auto")}";
        }

        if (declarations.ContainsKey("-kfx-fill-color") || declarations.ContainsKey("-kfx-fill-opacity"))
        {
            declarations["background-color"] = AddColorOpacity(
                declarations.Pop("-kfx-fill-color", "#ffffff"), declarations.Pop("-kfx-fill-opacity"));
        }

        if (declarations.ContainsKey("-kfx-text-emphasis-position-horizontal") ||
            declarations.ContainsKey("-kfx-text-emphasis-position-vertical"))
        {
            declarations["text-emphasis-position"] = string.Join(" ",
                new[]
                {
                    declarations.Pop("-kfx-text-emphasis-position-horizontal"),
                    declarations.Pop("-kfx-text-emphasis-position-vertical")
                }.Where(v => !string.IsNullOrEmpty(v)));
        }

        if (declarations.TryGetValue("-kfx-keep-lines-together", out var keepLines))
        {
            declarations.Remove("-kfx-keep-lines-together");
            var vals = keepLines.Split(' ');
            if (vals.Length > 0 && vals[0] != "inherit") declarations["orphans"] = vals[0];
            if (vals.Length > 1 && vals[1] != "inherit") declarations["widows"] = vals[1];
        }

        if (declarations.GetValueOrDefault("text-decoration-color") == "rgba(255,255,255,0)" &&
            !declarations.ContainsKey("text-decoration"))
        {
            declarations.Remove("text-decoration-color");
            declarations["text-decoration"] = "none !important";
        }

        foreach (var (combined, sides) in PropertyData.CompositeSideStyles)
        {
            if (!declarations.TryGetValue(combined, out var value)) continue;
            foreach (var side in sides)
            {
                if (!declarations.ContainsKey(side)) declarations[side] = value;
            }

            declarations.Remove(combined);
        }
    }

    public string PropertyValue(string yjPropertyName, object yjValue, bool svg = false, bool transformMatrixSwap = false)
    {
        var valueMap = PropertyData.Properties.TryGetValue(yjPropertyName, out var info) ? info.Values : null;
        switch (yjValue)
        {
            case null:
                return null;
            case IonStruct st:
                return PropertyValueFromStruct(yjPropertyName, st);
            case string text:
                if (yjPropertyName == "$11") return FixAndQuoteFontFamilyList(text);
                if (yjPropertyName == "$10") return FixLanguage(text);
                if ((yjPropertyName == "$32" || yjPropertyName == "$33") && (text == "0" || text == "0em")) return "normal";
                return text;
            case IonSymbol symbol:
                return PropertyValueFromSymbol(yjPropertyName, symbol.Value, valueMap);
            case bool boolValue:
                return ValueFromMap(valueMap, boolValue, yjPropertyName);
            case int or long or double or decimal or float:
                return PropertyValueFromNumber(yjPropertyName, yjValue, svg);
            case List<object> list:
                return PropertyValueFromList(yjPropertyName, list, svg, transformMatrixSwap);
            default:
                Log.Error($"屬性 {yjPropertyName} 的型別不支援：{TextUtil.TypeName(yjValue)}");
                return "?";
        }
    }

    private string PropertyValueFromStruct(string name, IonStruct value)
    {
        if (value.ContainsKey("$307"))
        {
            var magnitude = Convert.ToDecimal(value.Pop("$307"), CultureInfo.InvariantCulture);
            var unit = IonUtil.SymbolValue(value.Pop("$306"));
            if (unit == "$318" && magnitude > 0)
            {
                // 保持與 Python 版本相同的 pt→px 修正。
                if ((long)(magnitude * 1000) % 225 == 0)
                {
                    magnitude = magnitude * 1000 / 450m;
                    unit = "$319";
                }
            }

            if (unit == "$319") magnitude = AdjustPixelValue(magnitude);
            var unitText = unit is null ? "" : PropertyData.LengthUnits.GetValueOrDefault(unit, unit);
            var valueText = TextUtil.ValueStr(magnitude, unitText);
            if (name == "$42" && valueText == "1.2em" && DefaultLineHeight == "normal") return "normal";
            return valueText;
        }

        if (value.ContainsKey("$19")) return FixColorValue(value.Pop("$19"));
        if (value.ContainsKey("$499") && value.ContainsKey("$500"))
        {
            var values = new List<string>();
            foreach (var sub in new[] { "$499", "$500", "$501", "$502", "$498" })
            {
                if (value.ContainsKey(sub)) values.Add(PropertyValue(sub, value.Pop(sub)));
            }

            if (value.Pop("$336", false) is true) values.Add("inset");
            return string.Join(" ", values);
        }

        if (value.ContainsKey("$58") && name == "$549")
        {
            var values = new List<string>();
            foreach (var sub in new[] { "$59", "$58" })
            {
                values.Add(value.ContainsKey(sub) ? PropertyValue(sub, value.Pop(sub)) : "50%");
            }

            return string.Join(" ", values);
        }

        if (value.ContainsKey("$58"))
        {
            var values = new List<string>();
            foreach (var sub in new[] { "$58", "$61", "$60", "$59" })
            {
                var val = value.ContainsKey(sub) ? PropertyValue(sub, value.Pop(sub)) : "0";
                values.Add(val.Replace("%", ""));
            }

            return string.Join(" ", values);
        }

        if (value.ContainsKey("$175"))
        {
            value.Pop("$57");
            value.Pop("$56");
            return PropertyValue("$175", value.Pop("$175"));
        }

        if (value.ContainsKey("$131") || value.ContainsKey("$132"))
        {
            return $"{value.Pop("$131", "inherit")} {value.Pop("$132", "inherit")}";
        }

        Log.Error($"屬性 {name} 的 dict 內容未知");
        return "?";
    }

    private string PropertyValueFromSymbol(string name, string symbol, Dictionary<string, string> valueMap)
    {
        if (name == "$173")
        {
            var unique = UniquePartOfLocalSymbol(symbol);
            return unique.Length == 0 ? null : unique;
        }

        if (name is "$479" or "$175" or "$528")
        {
            return CssUrl(PathUtil.UrlRelativePath(ProcessExternalResource(symbol).Filename, StylesCssFilepath));
        }

        if (PropertyData.ColorProperties.Contains(name))
        {
            return symbol == "$349" ? FixColorValue(0) : null;
        }

        if (valueMap is not null && valueMap.TryGetValue(symbol, out var mapped)) return name == "$11" ? FixAndQuoteFontFamilyList(mapped) : mapped;
        Log.Warning($"屬性 {name} 有未知符號值：{symbol}");
        return name == "$11" ? FixAndQuoteFontFamilyList(symbol) : symbol;
    }

    private string PropertyValueFromNumber(string name, object value, bool svg)
    {
        if (PropertyData.ColorProperties.Contains(name))
        {
            var numeric = Convert.ToInt64(value);
            if (name == "$70" && (numeric & unchecked((long)0xff000000)) == 0) numeric |= unchecked((long)0xff000000);
            return FixColorValue(numeric);
        }

        var textValue = TextUtil.ValueStr(value);
        if ((name == "$32" || name == "$33") && (textValue == "0" || textValue == "0em")) return "normal";
        if (textValue != "0" && name is not ("$112" or "$13" or "$148" or "$149" or "$645" or "$647" or
                "$648" or "$790" or "$640" or "$641" or "$642" or "$639" or "$72" or "$126" or "$125" or "$42") && !svg)
        {
            return TextUtil.ValueStr(AdjustPixelValue(Convert.ToDecimal(value))) + "px";
        }

        return textValue;
    }

    private string PropertyValueFromList(string name, List<object> list, bool svg, bool transformMatrixSwap)
    {
        switch (name)
        {
            case "$650":
                return ProcessPolygon(list);
            case "vertex_list":
            case "$851":
                return ProcessVertexList(list);
            case "$646":
                var collisions = list.Select(v => v.ToStr()).Where(v => v is not null)
                    .Select(v => PropertyData.Collisions.GetValueOrDefault(v, "")).Where(v => v.Length > 0).ToList();
                return string.Join(" ", collisions.OrderBy(v => v, StringComparer.Ordinal));
            case "$98":
                return ProcessTransform(list, svg, transformMatrixSwap);
            case "$497":
                return string.Join(", ", list.Select(v => PropertyValue(name, v)));
            case "$761":
                var hints = new Dictionary<string, string> { ["$453"] = "caption", ["$282"] = "figure", ["$760"] = "heading" };
                return string.Join(" ", list.Select(v => hints.GetValueOrDefault(v.ToStr(), "")).Where(v => v.Length > 0));
            case "$531":
                return string.Join(" ", list.Select(v => PropertyValue(name, v)));
            default:
                Log.Error($"屬性 {name} 的 list 值不支援");
                return "?";
        }
    }

    private string ValueFromMap(Dictionary<string, string> map, object key, string name)
    {
        var keyText = key is bool b ? (b ? "true" : "false") : key?.ToString();
        if (map is not null && map.TryGetValue(keyText, out var value)) return value;
        Log.Warning($"屬性 {name} 有未知值：{key}");
        return keyText;
    }

    public decimal AdjustPixelValue(decimal value)
    {
        return IsPdfBacked ? Math.Round(value / 100m, 2) : value;
    }

    public string FixLanguage(string lang)
    {
        if (lang.Length > 2 && lang[2] == '_') lang = lang.Replace('_', '-');
        var dash = lang.IndexOf('-');
        if (dash < 0) return lang;
        var prefix = lang[..dash];
        var suffix = lang[(dash + 1)..];
        suffix = suffix.Length < 4 ? suffix.ToUpperInvariant() : char.ToUpperInvariant(suffix[0]) + suffix[1..];
        return prefix + "-" + suffix;
    }

    public string FixColorValue(object value)
    {
        if (value is string text && !Regex.IsMatch(text, @"^-?[0-9]+$")) return text;
        var color = Convert.ToInt64(value) & 0xffffffff;
        return ColorStr(color, IntToAlpha(color >> 24));
    }

    public string AddColorOpacity(string value, string opacity)
    {
        if (opacity is null or "1") return value;
        var color = ColorInt(value);
        return ColorStr(color, double.Parse(opacity, CultureInfo.InvariantCulture));
    }

    public string ColorStr(long rgbInt, double alpha = 1.0)
    {
        if (Math.Abs(alpha - 1.0) < 0.000001)
        {
            var hex = "#" + (rgbInt & 0x00ffffff).ToString("x6");
            if (PropertyData.ColorName.TryGetValue(hex, out var name)) return name;
            return hex is "#000000" ? "#000" : hex is "#ffffff" ? "#fff" : hex;
        }

        var red = (rgbInt & 0x00ff0000) >> 16;
        var green = (rgbInt & 0x0000ff00) >> 8;
        var blue = rgbInt & 0x000000ff;
        var alphaStr = Math.Abs(alpha) < 0.000001 ? "0" : alpha.ToString("0.000", CultureInfo.InvariantCulture);
        return $"rgba({red},{green},{blue},{alphaStr})";
    }

    public long ColorInt(string color)
    {
        if (PropertyData.ColorHex.TryGetValue(color, out var hex)) color = hex;
        var match = Regex.Match(color, @"^#([0-9a-f]{3,6})$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var rgb = match.Groups[1].Value;
            if (rgb.Length == 3) rgb = $"{rgb[0]}{rgb[0]}{rgb[1]}{rgb[1]}{rgb[2]}{rgb[2]}";
            return unchecked((long)0xff000000) + long.Parse(rgb, NumberStyles.HexNumber);
        }

        match = Regex.Match(color, @"^rgba\(([0-9]+),([0-9]+),([0-9]+),([0-9.]+)\)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var red = long.Parse(match.Groups[1].Value);
            var green = long.Parse(match.Groups[2].Value);
            var blue = long.Parse(match.Groups[3].Value);
            var alpha = AlphaToInt(double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture));
            return (alpha << 24) + (red << 16) + (green << 8) + blue;
        }

        throw new InvalidOperationException("無法解析顏色：" + color);
    }

    public static double IntToAlpha(long alphaInt)
    {
        if (alphaInt < 2) return 0.0;
        if (alphaInt > 253) return 1.0;
        return Math.Max(Math.Min((alphaInt + 1) / 256.0, 1.0), 0.0);
    }

    public static int AlphaToInt(double alpha)
    {
        if (alpha < 0.012) return 0;
        if (alpha > 0.996) return 255;
        return Math.Max(Math.Min((int)(alpha * 256.0 + 0.5) - 1, 255), 0);
    }

    public string FixAndQuoteFontFamilyList(string value)
    {
        var names = value.Split(',').Select(n => FixFontName(n)).Where(n => !string.IsNullOrEmpty(n)).Distinct();
        var quoted = names.Select(n => QuoteFontName(n));
        return string.Join(",", quoted);
    }

    public string StripFontName(string name)
    {
        name = (name ?? "").Trim();
        if (name.Length > 0 && (name[0] == '\'' || name[0] == '"')) name = name[1..];
        if (name.Length > 0 && (name[^1] == '\'' || name[^1] == '"')) name = name[..^1];
        return name.Trim();
    }

    public string FixFontName(string name, bool add = false, bool generic = false)
    {
        name = StripFontName(name);
        if (name.Length == 0) return name;
        var original = name.ToLowerInvariant();
        if (FixedFontNames.TryGetValue(original, out var fixedName)) return fixedName;

        name = name.Replace("\\", "");
        name = Regex.Replace(name, @"-(oblique|italic|bold|regular|roman|medium)$", " $1", RegexOptions.IgnoreCase);
        if (PropertyData.MisspelledFontNames.TryGetValue(name.ToLowerInvariant(), out var misspelled)) name = misspelled;

        var hasPrefix = name.Contains('-') && name != "sans-serif";
        if (hasPrefix)
        {
            name = name.Replace("sans-serif", "sans_serif");
            name = name[(name.LastIndexOf('-') + 1)..];
            name = name.Replace("sans_serif", "sans-serif");
        }

        name = name.Trim();
        if (add)
        {
            var lower = (hasPrefix ? "?-" : "") + name.ToLowerInvariant();
            if (!FontNameReplacements.TryGetValue(lower, out var replacement))
            {
                FontNameReplacements[lower] = name;
            }
            else
            {
                if (replacement != name) name = replacement;
            }

            if (!generic) PresentFontNames.Add(name);
        }
        else
        {
            if (FontNameReplacements.TryGetValue(name.ToLowerInvariant(), out var replacement)) name = replacement;
            else name = TextUtil.CapitalizeFontName(name);
            if (!PropertyData.GenericFontNames.Contains(name)) UsedFontNames.Add(name);
        }

        FixedFontNames[original] = name;
        return name;
    }

    public string QuoteFontName(string value)
    {
        var ok = true;
        foreach (var ident in value.Split(' '))
        {
            if (ident.Length == 0 || Regex.IsMatch(ident, @"^(-?[0-9]|--)") ||
                !Regex.IsMatch(ident, @"^[-_a-zA-Z0-9]*$"))
            {
                ok = false;
                break;
            }
        }

        return ok ? value : QuoteCssStr(value);
    }

    public string CssUrl(string value)
        => "url(" + (Regex.IsMatch(value, @"[ ()'""\\]") ? QuoteCssStr(value) : value) + ")";

    public string QuoteCssStr(string value)
    {
        if (!Regex.IsMatch(value, @"['\\]")) return "'" + value + "'";
        return "\"" + Regex.Replace(value, @"([""\\])", "\\$1") + "\"";
    }

    public Style GetStyle(XNode elem, bool remove = false)
    {
        var value = remove ? elem.Pop("style", "") : elem.Get("style", "");
        return new Style(value ?? "");
    }

    public void SetStyle(XNode elem, Style style)
    {
        var value = style.ToString();
        if (value.Length > 0) elem.Set("style", value);
        else elem.Pop("style");
    }

    public void AddStyle(XNode elem, Style style, bool? replace = null)
    {
        if (style.Count == 0) return;
        var existing = elem.Get("style", "");
        var combined = existing.Length > 0 ? new Style(existing).Update(style, replace) : style;
        SetStyle(elem, combined);
    }

    public void AddClass(XNode elem, string className, bool before = false)
    {
        var classes = (elem.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (string.IsNullOrEmpty(className) || classes.Contains(className)) return;
        if (before) classes.Insert(0, className);
        else classes.Add(className);
        elem.Set("class", string.Join(" ", classes));
    }

    public static string ClassSelector(string className) => "." + className;

    public static IEnumerable<string> MergeCssRules(Dictionary<string, Style> rules)
    {
        var byBody = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var selector in rules.Keys.OrderBy(k => k, Comparer<string>.Create((a, b) =>
                     string.CompareOrdinal(TextUtil.NaturalSortKey(a), TextUtil.NaturalSortKey(b)))))
        {
            var body = rules[selector].ToString();
            if (!byBody.TryGetValue(body, out var selectors))
            {
                selectors = new List<string>();
                byBody[body] = selectors;
            }

            selectors.Add(selector);
        }

        return byBody.Select(p => string.Join(", ", p.Value) + " {" + p.Key + "}");
    }

    public void CreateCssFiles()
    {
        PruneUnusedCssRules();
        var linked = new HashSet<string>();
        foreach (var bookPart in BookParts)
        {
            foreach (var link in bookPart.Head().Elements("link"))
            {
                if (link.Get("rel") == "stylesheet" && link.Has("href"))
                {
                    linked.Add(PathUtil.GetUrlFilename(PathUtil.UrlAbsolutePath(link.Get("href"), bookPart.Filename)));
                }
            }
        }

        foreach (var cssFile in linked.OrderBy(l => l, StringComparer.Ordinal))
        {
            if (cssFile == FixedLayoutCssFilepath)
            {
                ManifestResource(FixedLayoutCssFilepath, data: ReadBundledStyle("fixed-layout-jp.css"), mimetype: "text/css");
            }
            else if (cssFile == StylesCssFilepath)
            {
                ManifestResource(ResetCssFilepath, data: ReadBundledStyle("style-reset.css"), mimetype: "text/css");
                ManifestResource(StandardCssFilepath, data: ReadBundledStyle("style-standard.css"), mimetype: "text/css");
                ManifestResource(AdvanceCssFilepath, data: ReadBundledStyle("style-advance.css"), mimetype: "text/css");
                ManifestResource(CheckCssFilepath, data: ReadBundledStyle("style-check.css"), mimetype: "text/css");
                ManifestResource(StylesCssFilepath, data: ReadBundledStyle("book-style.css"), mimetype: "text/css");
                ManifestResource(KfxCssFilepath, data: KfxStyleCssData(), mimetype: "text/css");
            }
        }
    }

    public static byte[] ReadBundledStyle(string filename)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Styles." + filename)
                           ?? throw new FileNotFoundException("找不到內嵌樣式：" + filename);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    public byte[] KfxStyleCssData()
    {
        var lines = new List<string> { "@charset \"UTF-8\";" };
        foreach (var (selector, declarations) in ExtraCssRules.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            lines.Add(selector + " {" + declarations + "}");
        }

        if (FontFaces.Count > 0) lines.AddRange(FontFaces.OrderBy(f => f.ToString()).Select(ff => "@font-face {" + ff + "}"));
        if (CssRules.Count > 0) lines.AddRange(MergeCssRules(CssRules));
        foreach (var (mq, rules) in MediaQueries.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            lines.Add("@media " + mq + " {");
            lines.AddRange(MergeCssRules(rules).Select(line => "    " + line));
            lines.Add("}");
        }

        return Encoding.UTF8.GetBytes(string.Join("\n", lines));
    }

    public void RefreshBookStyleCss()
    {
        if (!OebpsFiles.ContainsKey(KfxCssFilepath)) return;
        PruneUnusedCssRules();
        AddOebpsFile(KfxCssFilepath, KfxStyleCssData(), "text/css");
    }

    public HashSet<string> UsedHtmlClasses()
    {
        var classes = new HashSet<string>();
        foreach (var bookPart in BookParts)
        {
            foreach (var elem in bookPart.Html.DescendantsAndSelf())
            {
                foreach (var c in (elem.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)) classes.Add(c);
            }
        }

        return classes;
    }

    public bool CssSelectorIsUsed(string selector, HashSet<string> usedClasses)
    {
        foreach (var sel in selector.Split(','))
        {
            var trimmed = sel.Trim();
            if (!trimmed.StartsWith('.')) return true;
            var match = Regex.Match(trimmed, @"^\.([A-Za-z0-9_-]+)(?:$|[\s:\.\[#])");
            if (!match.Success || usedClasses.Contains(match.Groups[1].Value)) return true;
        }

        return false;
    }

    public void PruneUnusedCssRules()
    {
        var used = UsedHtmlClasses();
        CssRules = CssRules.Where(p => CssSelectorIsUsed(p.Key, used)).ToDictionary(p => p.Key, p => p.Value);
        foreach (var mq in MediaQueries.Keys.ToList())
        {
            var filtered = MediaQueries[mq].Where(p => CssSelectorIsUsed(p.Key, used)).ToDictionary(p => p.Key, p => p.Value);
            if (filtered.Count == 0) MediaQueries.Remove(mq);
            else MediaQueries[mq] = filtered;
        }
    }

    public Style GeneratedClassStyle(XNode elem)
    {
        var style = new Style(new Dictionary<string, string>());
        foreach (var className in (elem.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // style-kfx.css 以外（電書協 utility class）的樣式不在 CssRules 中，忽略即可。
            if (!CssRules.TryGetValue(ClassSelector(className), out var classStyle)) continue;
            style.Update(classStyle, replace: true);
        }

        return style;
    }

    public void NormalizeTcySpans()
    {
        foreach (var bookPart in BookParts)
        {
            foreach (var span in bookPart.Body().FindDescendants("span").ToList())
            {
                var classes = (span.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var classStyle = GeneratedClassStyle(span);
                var isTcy = classes.Contains("tcy") || classStyle?.Get("text-combine-upright") == "all";
                if (!isTcy) continue;

                if (classStyle is not null && classStyle.Get("text-combine-upright") == "all" &&
                    classStyle.Properties.Keys.All(k => k is "text-combine-upright" or "word-break" or
                        "-webkit-word-break" or "-epub-word-break"))
                {
                    // word-break 對 2 字元的縦中横沒有作用；bw 的 tcy 也不帶這個屬性。
                    // 電書協 utility class（不在 CssRules 中）要保留，例如字級。
                    var kept = classes.Where(c => !CssRules.ContainsKey(ClassSelector(c))).Append("tcy");
                    span.Set("class", string.Join(" ", kept));
                }
                else if (!classes.Contains("tcy"))
                {
                    // 保留原本的樣式 class，再補上 tcy；ConsolidateHtml 會據此避免合併。
                    span.Set("class", string.Join(" ", classes.Append("tcy")));
                }

                SplitTcySpan(span);
            }
        }
    }

    // 縦中横以 2 個符號為一組；KFX 有時會給出 4 個字元一組，需拆回 2+2。
    private static void SplitTcySpan(XNode span)
    {
        if (span.Children.Count > 0) return;
        var text = span.Text ?? "";
        var length = TextUtil.UnicodeLen(text);
        if (length <= 2) return;

        var parent = span.Parent;
        if (parent is null) return;
        var index = parent.Children.IndexOf(span);
        var classes = span.Get("class", "");
        var style = span.Get("style", "");
        var tail = span.Tail;

        parent.RemoveChild(span);
        var inserted = 0;
        for (var offset = 0; offset < length; offset += 2)
        {
            var chunk = TextUtil.UnicodeSlice(text, offset, Math.Min(offset + 2, length));
            var tcySpan = new XNode("span");
            if (!string.IsNullOrEmpty(classes)) tcySpan.Set("class", classes);
            if (!string.IsNullOrEmpty(style)) tcySpan.Set("style", style);
            tcySpan.Text = chunk;
            parent.Insert(index + inserted, tcySpan);
            inserted++;
        }

        if (!string.IsNullOrEmpty(tail)) parent.Children[index + inserted - 1].Tail = tail;
    }

    public void NormalizeFitImageWrappers()
    {
        foreach (var bookPart in BookParts)
        {
            foreach (var div in bookPart.Body().FindDescendants("div").ToList())
            {
                if (!string.IsNullOrEmpty(div.Text) || div.Children.Count != 1) continue;
                var img = div.Children[0];
                if (!img.IsLocal("img") || !string.IsNullOrEmpty(img.Tail)) continue;
                var divStyle = GeneratedClassStyle(div);
                var imgStyle = GeneratedClassStyle(img);
                if (divStyle is null || imgStyle is null) continue;
                if (divStyle.Properties.Count != 1 || divStyle.Get("page-break-inside") != "avoid") continue;
                var allowed = new Dictionary<string, HashSet<string>>
                {
                    ["height"] = new() { "100%", "auto" },
                    ["max-height"] = new() { "100%" },
                    ["max-width"] = new() { "100%" },
                    ["width"] = new() { "100%", "auto" }
                };
                if (imgStyle.Properties.Any(p => !allowed.TryGetValue(p.Key, out var values) || !values.Contains(p.Value))) continue;
                div.Name = "p";
                div.Pop("class");
                div.Pop("style");
                img.Set("class", "fit");
                img.Pop("style");
            }
        }
    }
}

internal static class DictionaryExtensions
{
    public static string Pop(this Dictionary<string, string> dict, string key, string defaultValue = null)
    {
        if (!dict.TryGetValue(key, out var value)) return defaultValue;
        dict.Remove(key);
        return value;
    }
}
