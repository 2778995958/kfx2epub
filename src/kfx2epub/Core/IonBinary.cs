using System.Buffers.Binary;
using System.Text;

namespace Kfx2Epub.Core;

public sealed class IonNop
{
    public static readonly IonNop Instance = new();
    private IonNop() { }
}

public sealed class IonBinary
{
    public const byte VersionMarker = 0xe0;
    public static readonly byte[] Signature = { 0xe0, 1, 0, 0xea };

    private const int SortedStructFlag = 1;
    private const int VariableLenFlag = 14;
    private const int NullFlag = 15;

    public IonSymbolTable SymbolTable { get; }
    private bool _importSymbols;

    public IonBinary(IonSymbolTable symtab)
    {
        SymbolTable = symtab;
    }

    public List<object> DeserializeMultipleValues(byte[] data, bool importSymbols = false, bool withOffsets = false)
    {
        _importSymbols = importSymbols;
        var serial = new Deserializer(data);
        var signature = serial.Extract(4);
        if (!signature.AsSpan().SequenceEqual(Signature))
        {
            throw new InvalidOperationException($"Ion signature 不正確（{IonUtil.BytesToSeparatedHex(signature)}）");
        }

        var result = new List<object>();
        while (serial.Remaining > 0)
        {
            if (serial.Buffer[serial.Offset] == VersionMarker)
            {
                signature = serial.Extract(4);
                if (!signature.AsSpan().SequenceEqual(Signature))
                {
                    throw new InvalidOperationException("內嵌 Ion signature 不正確");
                }

                continue;
            }

            var valueOffset = serial.Offset;
            var value = DeserializeValue(serial);
            if (_importSymbols && value is IonAnnotation annotation)
            {
                if (annotation.IsAnnotation("$ion_symbol_table"))
                {
                    SymbolTable.Create((IonStruct)annotation.Value);
                }
                else if (annotation.IsAnnotation("$ion_shared_symbol_table"))
                {
                    SymbolTable.Catalog.CreateSharedSymbolTable((IonStruct)annotation.Value);
                }
            }

            if (value is IonNop) continue;

            if (withOffsets)
            {
                result.Add(new object[] { valueOffset, serial.Offset - valueOffset, value });
            }
            else
            {
                result.Add(value);
            }
        }

        return result;
    }

    public object DeserializeSingleValue(byte[] data)
    {
        var values = DeserializeMultipleValues(data);
        if (values.Count != 1)
        {
            throw new InvalidOperationException($"預期單一 Ion 值，實際找到 {values.Count} 個");
        }

        return values[0];
    }

    public IonAnnotation DeserializeAnnotatedValue(byte[] data, string expectAnnotation = null)
    {
        var value = DeserializeSingleValue(data);
        if (value is not IonAnnotation annotation)
        {
            throw new InvalidOperationException("預期 IonAnnotation");
        }

        if (expectAnnotation is not null)
        {
            annotation.VerifyAnnotation(expectAnnotation);
        }

        return annotation;
    }

    private object DeserializeValue(Deserializer serial)
    {
        var descriptor = serial.UnpackByte();
        if (descriptor == VersionMarker)
        {
            throw new InvalidOperationException("資料流中出現非預期的 Ion version marker");
        }

        var signature = descriptor >> 4;
        var flag = descriptor & 0x0f;

        if (flag == NullFlag && signature != 0)
        {
            return null;
        }

        switch (signature)
        {
            case 0:
                return DeserializeNullValue(flag, serial);
            case 1:
                if (flag > 1) throw new InvalidOperationException($"未知的 IonBool flag：{flag}");
                return flag != 0;
            case 2:
                return (long)DeserializeUnsignedInt(ExtractData(serial, flag));
            case 3:
                return -(long)DeserializeUnsignedInt(ExtractData(serial, flag));
            case 4:
                return DeserializeFloat(ExtractData(serial, flag));
            case 5:
                return DeserializeDecimal(ExtractData(serial, flag));
            case 6:
                return DeserializeTimestamp(ExtractData(serial, flag));
            case 7:
                return SymbolTable.GetSymbol((long)DeserializeUnsignedInt(ExtractData(serial, flag)));
            case 8:
                return Encoding.UTF8.GetString(ExtractData(serial, flag));
            case 9:
                return new IonCLOB(ExtractData(serial, flag));
            case 10:
                return new IonBLOB(ExtractData(serial, flag));
            case 11:
                return DeserializeList(ExtractData(serial, flag));
            case 12:
                return new IonSExp { Capacity = 0 }.Also(e =>
                {
                    foreach (var item in DeserializeList(ExtractData(serial, flag))) e.Add(item);
                });
            case 13:
                return DeserializeStruct(flag, serial);
            case 14:
                return DeserializeAnnotation(ExtractData(serial, flag));
            case 15:
                throw new InvalidOperationException("不支援 reserved Ion value signature");
            default:
                throw new InvalidOperationException($"未知的 Ion value signature：{signature}");
        }
    }

    private static byte[] ExtractData(Deserializer serial, int flag)
    {
        if (flag == VariableLenFlag)
        {
            var length = checked((int)DeserializeVluint(serial));
            return serial.Extract(length);
        }

        return serial.Extract(flag);
    }

    private static object DeserializeNullValue(int flag, Deserializer serial)
    {
        if (flag == NullFlag) return null;
        var length = flag == VariableLenFlag ? checked((int)DeserializeVluint(serial)) : flag;
        serial.Extract(length);
        return IonNop.Instance;
    }

    private static double DeserializeFloat(byte[] data)
    {
        if (data.Length == 0) return 0.0;
        if (data.Length == 4) return BinaryPrimitives.ReadSingleBigEndian(data);
        if (data.Length == 8) return BinaryPrimitives.ReadDoubleBigEndian(data);
        throw new InvalidOperationException("IonFloat 長度不正確");
    }

    private static decimal DeserializeDecimal(byte[] data)
    {
        if (data.Length == 0) return 0m;
        var serial = new Deserializer(data);
        var exponent = DeserializeVlsint(serial);
        var magnitude = DeserializeSignedInt(serial.Extract());
        return magnitude * Pow10(exponent);
    }

    private static decimal Pow10(int exponent)
    {
        if (exponent >= 0)
        {
            var value = 1m;
            for (var i = 0; i < exponent; i++) value *= 10m;
            return value;
        }

        var result = 1m;
        for (var i = 0; i < -exponent; i++) result /= 10m;
        return result;
    }

    private static IonTimestamp DeserializeTimestamp(byte[] data)
    {
        var serial = new Deserializer(data);
        var offset = DeserializeVlsint(serial, allowMinusZero: true);
        var year = (int)DeserializeVluint(serial);
        int? month = serial.Remaining > 0 ? (int)DeserializeVluint(serial) : null;
        int? day = serial.Remaining > 0 ? (int)DeserializeVluint(serial) : null;
        int? hour = serial.Remaining > 0 ? (int)DeserializeVluint(serial) : null;
        int? minute = serial.Remaining > 0 ? (int)DeserializeVluint(serial) : null;
        int? second = serial.Remaining > 0 ? (int)DeserializeVluint(serial) : null;
        int? microsecond = null;
        var fractionExponent = 0;

        if (serial.Remaining > 0)
        {
            fractionExponent = DeserializeVlsint(serial);
            var coefficient = serial.Remaining > 0 ? DeserializeSignedInt(serial.Extract()) : 0;
            if (!(coefficient == 0 && fractionExponent > -1))
            {
                var factor = 1L;
                for (var i = 0; i < -fractionExponent; i++) factor *= 10;
                var value = coefficient * 1000000 / factor;
                if (value is >= 0 and <= 999999) microsecond = (int)value;
            }
        }

        var format = month is null ? "%YT" : day is null ? "%Y-%mT" : hour is null ? "%Y-%m-%d" :
            second is null ? "%Y-%m-%dT%H:%M" : microsecond is null ? "%Y-%m-%dT%H:%M:%S" : "%Y-%m-%dT%H:%M:%S.%f";
        var dateTime = new DateTime(year, month ?? 1, day ?? 1, hour ?? 0, minute ?? 0, second ?? 0,
            microsecond is null ? 0 : microsecond.Value);
        return new IonTimestamp(dateTime, month is null || day is null || hour is null ? null : offset, format);
    }

    private List<object> DeserializeList(byte[] data)
    {
        var serial = new Deserializer(data);
        var result = new List<object>();
        while (serial.Remaining > 0)
        {
            var value = DeserializeValue(serial);
            if (value is not IonNop) result.Add(value);
        }

        return result;
    }

    private IonStruct DeserializeStruct(int flag, Deserializer serial)
    {
        if (flag == SortedStructFlag) flag = VariableLenFlag;
        var length = flag == VariableLenFlag ? checked((int)DeserializeVluint(serial)) : flag;
        var serial2 = new Deserializer(serial.Extract(length));
        var result = new IonStruct();
        while (serial2.Remaining > 0)
        {
            var id = (long)DeserializeVluint(serial2);
            var key = SymbolTable.GetSymbol(id).Value;
            var value = DeserializeValue(serial2);
            if (value is not IonNop)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private IonAnnotation DeserializeAnnotation(byte[] data)
    {
        var serial = new Deserializer(data);
        var annotationLength = checked((int)DeserializeVluint(serial));
        var annotationData = new Deserializer(serial.Extract(annotationLength));
        var value = DeserializeValue(serial);
        var annotations = new List<IonSymbol>();
        while (annotationData.Remaining > 0)
        {
            annotations.Add(SymbolTable.GetSymbol((long)DeserializeVluint(annotationData)));
        }

        if (annotations.Count == 0)
        {
            throw new InvalidOperationException("IonAnnotation 沒有 annotation");
        }

        return new IonAnnotation(annotations, value);
    }

    public static ulong DeserializeUnsignedInt(byte[] data)
    {
        if (data.Length > 8) throw new InvalidOperationException("IonInt 太大");
        ulong value = 0;
        foreach (var b in data) value = (value << 8) | b;
        return value;
    }

    public static long DeserializeSignedInt(byte[] data)
    {
        if (data.Length == 0) return 0;
        var negative = (data[0] & 0x80) != 0;
        if (negative)
        {
            var copy = data.ToArray();
            copy[0] &= 0x7f;
            var magnitude = (long)DeserializeUnsignedInt(copy);
            return -magnitude;
        }

        return (long)DeserializeUnsignedInt(data);
    }

    public static ulong DeserializeVluint(Deserializer serial)
    {
        ulong value = 0;
        while (true)
        {
            var b = serial.UnpackByte();
            value = (value << 7) | (uint)(b & 0x7f);
            if ((b & 0x80) != 0) return value;
            if (value == 0) throw new InvalidOperationException("IonVLUInt 有前導 0");
            if (value > 0x7fffffffffffff) throw new InvalidOperationException("IonVLUInt 過大");
        }
    }

    public static int DeserializeVlsint(Deserializer serial, bool allowMinusZero = false)
    {
        var first = serial.UnpackByte();
        var ibyte = (byte)(first & 0xbf);
        var bytes = new List<byte>();
        if (ibyte != 0) bytes.Add(ibyte);
        while ((ibyte & 0x80) == 0)
        {
            ibyte = serial.UnpackByte();
            bytes.Add(ibyte);
        }

        var value = (int)DeserializeVluint(new Deserializer(bytes.ToArray()));
        if ((first & 0x40) != 0)
        {
            if (value != 0) value = -value;
            else if (!allowMinusZero) throw new InvalidOperationException("非預期的 -0 VLSInt");
        }

        return value;
    }
}

internal static class IonListExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
