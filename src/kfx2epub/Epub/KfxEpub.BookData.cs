using System.Text.RegularExpressions;
using Kfx2Epub.Core;
using Kfx2Epub.Kfx;

namespace Kfx2Epub.Epub;

public sealed class FragmentMap : Dictionary<string, object>
{
    public object Pop(string key, object defaultValue = null)
    {
        if (!TryGetValue(key, out var value)) return defaultValue;
        Remove(key);
        return value;
    }
}

public partial class KfxEpub
{
    public YjBook Book { get; private set; }
    public Dictionary<string, object> BookData { get; } = new();
    public HashSet<string> BookSymbols { get; } = new();
    public Dictionary<(string Ftype, string Fid), bool> UsedFragments { get; } = new();
    public bool BookHasIllustratedLayoutConditionalPageTemplate { get; set; }
    public string BookSymbolFormat { get; set; }

    public static readonly Dictionary<string, string> FragmentNameSymbol = new()
    {
        ["$266"] = "$180", ["$164"] = "$175", ["$391"] = "$239", ["$393"] = "$240",
        ["$260"] = "$174", ["$608"] = "$598", ["$259"] = "$176", ["$157"] = "$173"
    };

    public void InitializeBookData(YjBook book)
    {
        Book = book;
        BookHasIllustratedLayoutConditionalPageTemplate = book.HasIllustratedLayoutConditionalPageTemplate();
        BookData.Clear();
        OrganizeFragmentsByType(book.Fragments);
        DetermineBookSymbolFormat();
    }

    public Dictionary<string, object> OrganizeFragmentsByType(YjFragmentList fragments)
    {
        var categorized = new Dictionary<string, object>();
        var fontCount = 0;
        string lastContainerId = null;

        foreach (var fragment in fragments)
        {
            var id = fragment.Fid;
            BookSymbols.Add(id);

            if (fragment.Ftype == "$270" && fragment.Value is IonStruct container)
            {
                id = $"{container.GetValueOrDefault("$161", "")}:{container.GetValueOrDefault("$409", "")}";
                lastContainerId = id;
            }
            else if (fragment.Ftype == "$593")
            {
                id = lastContainerId;
            }
            else if (fragment.Ftype == "$262")
            {
                id = $"{id}-font-{fontCount:000}";
                fontCount++;
            }
            else if (fragment.Ftype == "$387" && fragment.Value is IonStruct layout)
            {
                id = $"{id}:{layout.GetValueOrDefault("$215", "")}";
            }

            if (!categorized.TryGetValue(fragment.Ftype, out var target))
            {
                target = new FragmentMap();
                categorized[fragment.Ftype] = target;
            }

            if (target is not FragmentMap map)
            {
                map = new FragmentMap();
                categorized[fragment.Ftype] = map;
            }

            if (!map.ContainsKey(id)) map[id] = ReplaceIonData(fragment.Value);
            else if (fragment.Ftype == "$597" && IonDataEqual(map[id], ReplaceIonData(fragment.Value)))
            {
                // 有些 KFX-ZIP 會把同一個容器打包兩份；auxiliary_data 內容相同時保留第一份。
                Log.Info($"忽略重複的 {fragment.Ftype} fragment：{id}");
            }
            else Log.Error($"書籍包含多個 {fragment.Ftype} fragment");
        }

        foreach (var category in categorized.Keys.ToList())
        {
            if (categorized[category] is FragmentMap ids && ids.Count == 1)
            {
                var pair = ids.First();
                if (pair.Key == category) categorized[category] = pair.Value;
            }
        }

        foreach (var pair in categorized) BookData[pair.Key] = pair.Value;
        return BookData;
    }

    public object ReplaceIonData(object value)
    {
        switch (value)
        {
            case IonAnnotation ann:
                return ReplaceIonData(ann.Value);
            case IonSExp sx:
                return new IonSExp { Capacity = 0 }.Also(e =>
                {
                    foreach (var item in sx) e.Add(ReplaceIonData(item));
                });
            case List<object> list:
                return list.Select(ReplaceIonData).ToList();
            case IonStruct st:
                var copy = new IonStruct();
                foreach (var pair in st)
                {
                    copy[pair.Key] = ReplaceIonData(pair.Value);
                }

                return copy;
            case IonSymbol symbol:
                BookSymbols.Add(symbol.Value);
                return value;
            default:
                return value;
        }
    }

    // 只用來判斷重複的 auxiliary_data 是否內容相同；不做完整 Ion 正規化。
    private static bool IonDataEqual(object left, object right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        switch (left)
        {
            case IonStruct a when right is IonStruct b:
                if (a.Count != b.Count) return false;
                using (var ea = a.GetEnumerator())
                using (var eb = b.GetEnumerator())
                {
                    while (ea.MoveNext() && eb.MoveNext())
                    {
                        if (ea.Current.Key != eb.Current.Key) return false;
                        if (!IonDataEqual(ea.Current.Value, eb.Current.Value)) return false;
                    }
                }

                return true;
            case IonAnnotation a when right is IonAnnotation b:
                return a.Annotations.Select(x => x.Value).SequenceEqual(b.Annotations.Select(x => x.Value))
                       && IonDataEqual(a.Value, b.Value);
            case IonBLOB a when right is IonBLOB b:
                return a.Data.AsSpan().SequenceEqual(b.Data);
            case List<object> a when right is List<object> b:
                if (a.Count != b.Count) return false;
                for (var i = 0; i < a.Count; i++)
                {
                    if (!IonDataEqual(a[i], b[i])) return false;
                }

                return true;
            case IonSymbol a when right is IonSymbol b:
                return a.Value == b.Value;
            default:
                return left.Equals(right);
        }
    }

    public void DetermineBookSymbolFormat()
    {
        var counts = new Dictionary<string, int>
        {
            [SymTypes.Common] = 0, [SymTypes.Dictionary] = 0, [SymTypes.Original] = 0,
            [SymTypes.Base64] = 0, [SymTypes.Short] = 0, [SymTypes.Shared] = 0, [SymTypes.Unknown] = 0
        };

        foreach (var symbol in BookSymbols)
        {
            counts[Book.ClassifySymbol(symbol)]++;
        }

        counts[SymTypes.Original] += counts[SymTypes.Unknown] / 10;
        var quorum = (counts[SymTypes.Dictionary] + counts[SymTypes.Short] + counts[SymTypes.Base64] +
                      counts[SymTypes.Original]) / 2;

        if (counts[SymTypes.Short] >= quorum ||
            (BookData.TryGetValue("$538", out var doc) && doc is IonStruct ds && ds.ContainsKey("max_id")))
        {
            BookSymbolFormat = SymTypes.Short;
        }
        else if (counts[SymTypes.Dictionary] >= quorum) BookSymbolFormat = SymTypes.Dictionary;
        else if (counts[SymTypes.Base64] >= quorum) BookSymbolFormat = SymTypes.Base64;
        else BookSymbolFormat = SymTypes.Original;

        if (BookSymbolFormat != SymTypes.Short) Log.Info($"書籍符號格式為 {BookSymbolFormat}");
    }

    public string UniquePartOfLocalSymbol(string symbol)
    {
        var name = symbol ?? "";
        switch (BookSymbolFormat)
        {
            case SymTypes.Short:
                name = Regex.Replace(name, "^resource/", "", RegexOptions.None, TimeSpan.FromSeconds(1));
                break;
            case SymTypes.Dictionary:
                name = name.StartsWith('G') ? name[1..] : name;
                break;
            case SymTypes.Base64:
                name = Regex.Replace(name, @"^(resource/)?[a-zA-Z0-9_-]{22}", "");
                break;
            default:
                name = Regex.Replace(name,
                    @"^V_[0-9]_[0-9](-PARA|-CHAR)?-[0-9]_[0-9]_[0-9a-f]{12,16}_[0-9a-f]{1,5}", "");
                name = Regex.Replace(name,
                    @"^(fonts/|images/)?(res|resource)_[0-9]_[0-9]_[0-9a-f]{12,16}_[0-9a-f]{1,5}_", "");
                name = Regex.Replace(name,
                    @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "");
                break;
        }

        return name.TrimStart('-', '_');
    }

    public string PrefixUniquePartOfSymbol(string uniquePart, string prefix)
    {
        if (string.IsNullOrEmpty(uniquePart)) return prefix;
        if (Regex.IsMatch(uniquePart, @"^[A-Za-z0-9]+(-.+)?$") || !Regex.IsMatch(uniquePart, "^[A-Za-z]"))
        {
            return prefix + "_" + uniquePart;
        }

        return uniquePart;
    }

    public object GetFragment(string ftype = null, object fid = null, bool delete = true)
    {
        // 與 Python 版一致：若 fid 不是字串或符號，代表呼叫端已直接傳入 fragment 內容。
        var fidStr = fid.ToStr();
        if (fidStr is null || ftype is null)
        {
            return fid;
        }

        FragmentMap container = null;
        if (BookData.TryGetValue(ftype, out var category))
        {
            container = category as FragmentMap;
        }
        else if (ftype == "$393" && BookData.TryGetValue("$394", out var conditional))
        {
            container = conditional as FragmentMap;
        }

        object data = null;
        if (container is not null && container.TryGetValue(fidStr, out var found))
        {
            if (delete) container.Remove(fidStr);
            data = found;
        }

        if (data is null)
        {
            if (UsedFragments.ContainsKey((ftype, fidStr)))
            {
                Log.Error($"書籍 fragment 被使用多次：{ftype} {fidStr}");
                data = new IonStruct();
            }
            else
            {
                Log.Error($"書籍缺少 fragment：{ftype} {fidStr}");
                data = new IonStruct();
            }
        }
        else
        {
            UsedFragments[(ftype, fidStr)] = true;
        }

        var dataName = GetFragmentName(data, ftype, delete: false);
        if (!string.IsNullOrEmpty(dataName) && dataName != fidStr)
        {
            Log.Error($"預期 {ftype} 名稱為 {fidStr}，實際為 {dataName}");
        }

        return data;
    }

    public object GetNamedFragment(object structure, string ftype = null, bool delete = true, string nameSymbol = null)
    {
        if (structure is not IonStruct st) return structure;
        var key = nameSymbol ?? FragmentNameSymbol.GetValueOrDefault(ftype);
        var fid = (delete ? st.Pop(key) : st.GetValueOrDefault(key)).ToStr();
        return GetFragment(ftype, fid, delete);
    }

    public void CheckFragmentName(object fragmentData, string ftype, string fid, bool delete = true)
    {
        var name = GetFragmentName(fragmentData, ftype, delete);
        if (name != fid) Log.Error($"Fragment {ftype} {fid} 名稱不正確：{name}");
    }

    public string GetFragmentName(object fragmentData, string ftype, bool delete = true)
        => GetStructureName(fragmentData, FragmentNameSymbol.GetValueOrDefault(ftype), delete);

    public string GetStructureName(object structure, string nameKey, bool delete = true)
    {
        if (structure is not IonStruct st) return null;
        var value = delete ? st.Pop(nameKey) : st.GetValueOrDefault(nameKey);
        return value.ToStr() ?? (value is IonSymbol symbol ? symbol.Value : null);
    }

    public void CheckEmpty(object value, string name)
    {
        if (value is IonStruct st && st.Count > 0)
        {
            Log.Error($"{name} 仍有未處理資料：{st}");
        }
        else if (value is FragmentMap map && map.Count > 0)
        {
            Log.Error($"{name} 仍有未處理資料：{string.Join(",", map.Keys)}");
        }
        else if (value is List<object> list && list.Count > 0)
        {
            Log.Error($"{name} 仍有未處理資料");
        }
    }

    public (string Id, int Offset) GetPosition(object position)
    {
        if (position is not IonStruct st) return (null, 0);
        var id = GetLocationId(st);
        var off = st.Pop("$143", 0);
        var offset = Convert.ToInt32(off);
        CheckEmpty(st, "position");
        return (id, offset);
    }

    public string GetLocationId(object structure)
    {
        if (structure is not IonStruct st) return null;
        var value = st.Pop("$155");
        if (value is null) value = st.Pop("$598");
        return value?.ToString();
    }
}

internal static class AlsoExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
