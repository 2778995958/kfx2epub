using Kfx2Epub.Core;

namespace Kfx2Epub.Kfx;

public sealed class YjFragment
{
    public string Fid { get; }
    public string Ftype { get; }
    public object Value { get; }

    public YjFragment(string ftype, string fid = null, object value = null)
    {
        Ftype = ftype;
        Fid = fid ?? ftype;
        Value = value;
    }

    public override string ToString() => $"{(Fid == Ftype ? Ftype : Fid + "/" + Ftype)}";

    public bool IsSingle => Fid == Ftype;
}

public sealed class YjFragmentList : List<YjFragment>
{
    public List<YjFragment> GetAll(string ftype)
        => this.Where(f => f.Ftype == ftype).ToList();

    public YjFragment Get(string ftype, string fid = null, YjFragment defaultValue = null, bool first = false)
    {
        IEnumerable<YjFragment> matches = this.Where(f => f.Ftype == ftype);
        if (fid is not null)
        {
            matches = matches.Where(f => f.Fid == fid);
        }

        var list = matches.ToList();
        if (list.Count == 0) return defaultValue;
        if (list.Count > 1 && !first) throw new InvalidOperationException($"YJFragmentList 有多個符合項目：{ftype} {fid}");
        return list[0];
    }

    public HashSet<string> Ftypes() => this.Select(f => f.Ftype).ToHashSet();
}

public static class FragmentTypes
{
    public static readonly HashSet<string> RawFragmentTypes = new() { "$418", "$417" };
}

public abstract class YjContainer
{
    protected IonSymbolTable Symtab;
    public DataFile DataFile { get; protected set; }
    public YjFragmentList Fragments { get; protected set; }

    protected YjContainer(IonSymbolTable symtab, DataFile dataFile = null, YjFragmentList fragments = null)
    {
        Symtab = symtab;
        DataFile = dataFile;
        Fragments = fragments ?? new YjFragmentList();
    }

    public abstract void Deserialize();

    public virtual YjFragmentList GetFragments() => Fragments;
}
