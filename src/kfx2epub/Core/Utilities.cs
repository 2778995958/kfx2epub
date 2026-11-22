using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kfx2Epub.Core;

public static class TextUtil
{
    public static string Repeat(char c, int count) => new(c, count);

    public static string PadNumber(int value, int width) => value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');

    public static string SequenceNumber(int index, int? total = null, int minimumDigits = 3)
    {
        var width = Math.Max(minimumDigits, (total ?? index).ToString(CultureInfo.InvariantCulture).Length);
        return index.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
    }

    public static string ValueStr(object quantity, string unit = "", bool emitZeroUnit = false)
    {
        if (quantity is null) return unit;

        string value;
        if (quantity is decimal dec)
        {
            if (Math.Abs(dec) < 0.000001m) value = "0";
            else value = RoundSignificant(dec).ToString(CultureInfo.InvariantCulture);
        }
        else if (quantity is double dbl)
        {
            if (Math.Abs(dbl) < 1e-6) value = "0";
            else value = dbl.ToString("G6", CultureInfo.InvariantCulture);
        }
        else if (quantity is float flt)
        {
            if (Math.Abs(flt) < 1e-6f) value = "0";
            else value = flt.ToString("G6", CultureInfo.InvariantCulture);
        }
        else
        {
            value = Convert.ToString(quantity, CultureInfo.InvariantCulture);
        }

        if (value.Contains('e') || value.Contains('E'))
        {
            value = Convert.ToDouble(quantity, CultureInfo.InvariantCulture).ToString("0.0000", CultureInfo.InvariantCulture);
        }

        if (value.Contains('.'))
        {
            value = value.TrimEnd('0').TrimEnd('.');
        }

        if (value is "0" or "-0" && !emitZeroUnit) return "0";
        return value + unit;
    }

    private static decimal RoundSignificant(decimal value, int digits = 6)
    {
        if (value == 0) return 0;
        var exponent = (int)Math.Floor(Math.Log10((double)Math.Abs(value)));
        var scale = Math.Pow(10, digits - 1 - exponent);
        var rounded = Math.Round((double)value * scale) / scale;
        return (decimal)rounded;
    }

    public static (decimal? Quantity, string Unit) SplitValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return (null, value ?? "");
        var match = Regex.Match(value, @"^([+-]?[0-9]+\.?[0-9]*)");
        if (!match.Success) return (null, value);
        var number = decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return (number, value[match.Groups[1].Value.Length..]);
    }

    public static string ZeroQuantity(string value)
    {
        if (Regex.IsMatch(value, @"^#[0-9a-f]+$", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(value, @"^rgba\([0-9]+,[0-9]+,[0-9]+,[0-9.]+\)$") ||
            ColorNames.Contains(value))
        {
            return "#0";
        }

        if (Regex.IsMatch(value, @"^([+-]?[0-9]+\.?[0-9]*)(|em|ex|ch|rem|vw|vh|vmin|vmax|%|cm|mm|in|px|pt|pc)$"))
        {
            return "0";
        }

        return value;
    }

    public static string CapitalizeFontName(string name)
        => string.Join(" ", name.Split(' ').Select(word => word.Length > 2
            ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
            : word.ToUpperInvariant()));

    public static string RemoveDuplicates(IEnumerable<string> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var item in items)
        {
            if (seen.Add(item)) result.Add(item);
        }

        return string.Join(",", result);
    }

    public static int UnicodeLen(string text)
        => string.IsNullOrEmpty(text) ? 0 : text.EnumerateRunes().Count();

    public static string UnicodeSlice(string text, int start, int? stop = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var runes = text.EnumerateRunes().ToArray();
        var s = Math.Clamp(start, 0, runes.Length);
        var e = Math.Clamp(stop ?? runes.Length, 0, runes.Length);
        if (e <= s) return "";
        var builder = new StringBuilder();
        for (var i = s; i < e; i++) builder.Append(runes[i].ToString());
        return builder.ToString();
    }

    public static string TypeName(object value)
    {
        if (value is null) return "NoneType";
        if (value is IonSymbol) return "IonSymbol";
        if (value is IonStruct) return "IonStruct";
        if (value is IonBLOB) return "IonBLOB";
        if (value is IonAnnotation) return "IonAnnotation";
        if (value is IonSExp) return "IonSExp";
        if (value is List<object>) return "IonList";
        return value.GetType().Name;
    }

    public static string ListSymbols(IEnumerable<string> values)
        => string.Join(", ", values.OrderBy(v => v, StringComparer.Ordinal));

    public static string ListNumbers(IEnumerable<int> values)
        => string.Join(", ", values.OrderBy(v => v));

    public static string NaturalSortKey(string value)
    {
        return string.Join("\u0001", Regex.Split(value ?? "", @"(\d+)")
            .Select(part => int.TryParse(part, out var number)
                ? number.ToString("D10", CultureInfo.InvariantCulture)
                : part));
    }

    public static readonly HashSet<string> ColorNames = new()
    {
        "black", "navy", "blue", "green", "teal", "lime", "cyan", "maroon", "purple", "olive", "gray",
        "red", "magenta", "yellow", "white"
    };
}

public static class NameUtil
{
    public static string MakeUniqueName(string rootName, ISet<string> checkSet, string sep = "", bool alwaysSuffix = false)
    {
        if (!alwaysSuffix && !string.IsNullOrEmpty(rootName) && !checkSet.Contains(rootName)) return rootName;

        var index = 0;
        while (true)
        {
            var unique = rootName + sep + index.ToString(CultureInfo.InvariantCulture);
            if (!checkSet.Contains(unique)) return unique;
            index++;
        }
    }

    public static string FixHtmlId(string id, bool illustratedLayout = false)
    {
        if (illustratedLayout) id = id.Replace(".", "_");
        id = Regex.Replace(id, "[\u0660-\u0669\u06f0-\u06f9]",
            m => ((char)((m.Value[0] & 0x0f) + 0x30)).ToString());
        id = Regex.Replace(id, @"[^A-Za-z0-9_.-]", "_");
        if (id.Length == 0 || !char.IsAsciiLetter(id[0])) id = "id_" + id;
        return id;
    }

    public static string SanitizeFilename(string name)
    {
        var table = new Dictionary<char, char>
        {
            ['\\'] = '＼', ['/'] = '／', [':'] = '：', ['*'] = '＊', ['?'] = '？',
            ['"'] = '＂', ['<'] = '＜', ['>'] = '＞', ['|'] = '｜'
        };
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(table.TryGetValue(c, out var safe) ? safe : c);
        return sb.ToString();
    }
}
