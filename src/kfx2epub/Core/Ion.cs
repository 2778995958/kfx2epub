using System.Collections;
using System.Text;

namespace Kfx2Epub.Core;

// Ion 資料模型。刻意區分 IonSymbol 與一般字串，因為 KFX 內容大量依賴兩者差異。
public sealed class IonSymbol : IEquatable<IonSymbol>, IComparable<IonSymbol>
{
    public string Value { get; }

    public IonSymbol(string value)
    {
        Value = value;
    }

    public override string ToString() => Value;

    public bool Equals(IonSymbol other) => other is not null && Value == other.Value;

    public override bool Equals(object obj) => obj is IonSymbol other && Equals(other);

    public override int GetHashCode() => Value?.GetHashCode() ?? 0;

    public int CompareTo(IonSymbol other) => string.CompareOrdinal(Value, other?.Value);
}

public sealed class IonAnnotation
{
    public List<IonSymbol> Annotations { get; }
    public object Value { get; }

    public IonAnnotation(IEnumerable<IonSymbol> annotations, object value)
    {
        Annotations = annotations.ToList();
        if (Annotations.Count == 0)
        {
            throw new InvalidOperationException("IonAnnotation 不能沒有 annotation");
        }

        if (value is IonAnnotation)
        {
            throw new InvalidOperationException("IonAnnotation 不能標註另一個 IonAnnotation");
        }

        Value = value;
    }

    public bool IsSingle => Annotations.Count == 1;

    public bool IsAnnotation(string name) => IsSingle && Annotations[0].Value == name;

    public string GetAnnotation()
    {
        if (!IsSingle)
        {
            throw new InvalidOperationException("IonAnnotation 不是單一 annotation");
        }

        return Annotations[0].Value;
    }

    public IonAnnotation VerifyAnnotation(string name)
    {
        if (!IsAnnotation(name))
        {
            throw new InvalidOperationException($"預期 annotation {name}，實際為 {string.Join(",", Annotations.Select(a => a.Value))}");
        }

        return this;
    }

    public override string ToString() => string.Join(" ", Annotations.Select(a => a.Value + "::")) + Value;
}

public sealed class IonBLOB
{
    public byte[] Data { get; }

    public IonBLOB(byte[] data)
    {
        Data = data;
    }

    public int Length => Data.Length;

    public override string ToString() => $"*** {Data.Length} byte BLOB ***";

    public override bool Equals(object obj) => obj is IonBLOB other && Data.AsSpan().SequenceEqual(other.Data);

    public override int GetHashCode() => Data.Length == 0 ? 0 : BitConverter.ToInt32(Data, 0);
}

public sealed class IonCLOB
{
    public byte[] Data { get; }

    public IonCLOB(byte[] data)
    {
        Data = data;
    }
}

public sealed class IonSExp : List<object>
{
}

public sealed class IonStruct : IEnumerable<KeyValuePair<string, object>>
{
    private readonly List<string> _order = new();
    private readonly Dictionary<string, object> _map = new();

    public int Count => _order.Count;

    public IReadOnlyList<string> Keys => _order;

    public object this[string key]
    {
        get => _map[key];
        set
        {
            if (!_map.ContainsKey(key))
            {
                _order.Add(key);
            }

            _map[key] = value;
        }
    }

    public bool ContainsKey(string key) => _map.ContainsKey(key);

    public bool TryGetValue(string key, out object value) => _map.TryGetValue(key, out value);

    public object GetValueOrDefault(string key, object defaultValue = null)
        => _map.TryGetValue(key, out var value) ? value : defaultValue;

    public object Pop(string key, object defaultValue = null)
    {
        if (!_map.TryGetValue(key, out var value))
        {
            return defaultValue;
        }

        _map.Remove(key);
        _order.Remove(key);
        return value;
    }

    public void Remove(string key)
    {
        if (_map.Remove(key))
        {
            _order.Remove(key);
        }
    }

    public void Clear()
    {
        _order.Clear();
        _map.Clear();
    }

    public IonStruct Clone()
    {
        var clone = new IonStruct();
        foreach (var key in _order)
        {
            clone[key] = IonUtil.DeepCopy(_map[key]);
        }

        return clone;
    }

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        foreach (var key in _order)
        {
            yield return new KeyValuePair<string, object>(key, _map[key]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString()
        => "{" + string.Join(", ", _order.Select(k => $"{k}: {IonUtil.Repr(_map[k])}")) + "}";
}

public sealed class IonTimestamp
{
    public DateTime DateTime { get; }
    public int? OffsetMinutes { get; }
    public string Format { get; }

    public IonTimestamp(DateTime dateTime, int? offsetMinutes, string format = null)
    {
        DateTime = dateTime;
        OffsetMinutes = offsetMinutes;
        Format = format;
    }

    public override string ToString() => DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss");
}

public static class IonValueExtensions
{
    public static string ToStr(this object value)
        => value is IonSymbol symbol ? symbol.Value : value is string text ? text : null;
}

public static class IonUtil
{
    public static bool IsString(object value) => value is string;

    public static string SymbolValue(object value)
        => value is IonSymbol symbol ? symbol.Value : value is string text ? text : null;

    public static bool IsSymbol(object value, string name) => value is IonSymbol s && s.Value == name;

    public static object Unannotated(object value) => value is IonAnnotation ann ? ann.Value : value;

    public static string Repr(object value)
    {
        return value switch
        {
            null => "null",
            IonSymbol s => s.Value,
            string s => "\"" + s + "\"",
            IonBLOB b => b.ToString(),
            IonStruct st => st.ToString(),
            IonSExp sx => "(" + string.Join(", ", sx.Select(Repr)) + ")",
            IEnumerable<object> list => "[" + string.Join(", ", list.Select(Repr)) + "]",
            _ => value.ToString()
        };
    }

    public static object DeepCopy(object value)
    {
        return value switch
        {
            null => null,
            IonSymbol => value,
            string => value,
            int or long or double or decimal or bool => value,
            IonBLOB b => new IonBLOB(b.Data.ToArray()),
            IonCLOB c => new IonCLOB(c.Data.ToArray()),
            IonTimestamp t => new IonTimestamp(t.DateTime, t.OffsetMinutes, t.Format),
            IonAnnotation a => new IonAnnotation(a.Annotations, DeepCopy(a.Value)),
            IonSExp sx => new IonSExp { Capacity = sx.Count }.Also(e => { foreach (var v in sx) e.Add(DeepCopy(v)); }),
            IonStruct st => st.Clone(),
            List<object> list => list.Select(DeepCopy).ToList(),
            _ => value
        };
    }

    public static string BytesToSeparatedHex(ReadOnlySpan<byte> data, string sep = " ")
    {
        var sb = new StringBuilder();
        for (var i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(sep);
            sb.Append(data[i].ToString("x2"));
        }

        return sb.ToString();
    }
}
