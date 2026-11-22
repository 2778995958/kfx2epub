using System.Text.RegularExpressions;

namespace Kfx2Epub.Core;

public sealed class IonSharedSymbolTable
{
    public string Name { get; }
    public int Version { get; }
    public IReadOnlyList<string> Symbols { get; }

    public IonSharedSymbolTable(string name, int version = 1, IEnumerable<string> symbols = null)
    {
        Name = name;
        Version = version;
        Symbols = (symbols ?? Array.Empty<string>()).ToList();
    }
}

public sealed class SymbolTableCatalog
{
    private readonly Dictionary<(string Name, int? Version), IonSharedSymbolTable> _tables = new();

    public SymbolTableCatalog(bool addGlobalSharedSymbolTables = false)
    {
        Clear();
        if (addGlobalSharedSymbolTables)
        {
            AddSharedSymbolTable(YjSymbolCatalog.YjSymbols);
        }
    }

    public void Clear()
    {
        _tables.Clear();
        AddSharedSymbolTable(YjSymbolCatalog.SystemSymbolTable);
    }

    public void AddSharedSymbolTable(IonSharedSymbolTable table)
    {
        _tables[(table.Name, table.Version)] = table;
        if (!_tables.TryGetValue((table.Name, null), out var latest) || latest.Version <= table.Version)
        {
            _tables[(table.Name, null)] = table;
        }
    }

    public void CreateSharedSymbolTable(IonStruct data)
    {
        AddSharedSymbolTable(new IonSharedSymbolTable(
            (string)data.GetValueOrDefault("name"),
            data.TryGetValue("version", out var version) ? Convert.ToInt32(version) : 1,
            data.TryGetValue("symbols", out var symbols) ? ((IEnumerable<object>)symbols).Cast<string>() : Array.Empty<string>()));
    }

    public IonSharedSymbolTable GetSharedSymbolTable(string name, int? version = null)
    {
        return _tables.TryGetValue((name, version), out var table)
            ? table
            : _tables.TryGetValue((name, null), out table) ? table : null;
    }
}

public sealed class SymbolTableImport
{
    public string Name { get; }
    public int Version { get; }
    public int MaxId { get; }

    public SymbolTableImport(string name, int version, int maxId)
    {
        Name = name;
        Version = version;
        MaxId = maxId;
    }
}

public sealed class IonSymbolTable
{
    public static readonly SymbolTableCatalog GlobalCatalog = new(addGlobalSharedSymbolTables: true);

    private readonly List<SymbolTableImport> _tableImports = new();
    private readonly List<string> _symbols = new();
    private readonly Dictionary<string, int> _idOfSymbol = new();
    private readonly Dictionary<int, string> _symbolOfId = new();
    private bool _creatingLocalSymbols;
    private int _localMinId;

    public string Context { get; }
    public SymbolTableCatalog Catalog { get; }
    public IReadOnlyList<string> Symbols => _symbols;
    public int LocalMinId => _localMinId;
    public HashSet<long> UndefinedIds { get; } = new();
    public HashSet<string> UndefinedSymbols { get; } = new();
    public HashSet<string> UnexpectedUsedSymbols { get; } = new();

    public IonSymbolTable(string initialImport = null, SymbolTableCatalog catalog = null)
    {
        Catalog = catalog ?? GlobalCatalog;
        Clear();
        if (!string.IsNullOrEmpty(initialImport))
        {
            ImportSharedSymbolTable(initialImport);
        }
    }

    public void Clear()
    {
        _tableImports.Clear();
        _symbols.Clear();
        _idOfSymbol.Clear();
        _symbolOfId.Clear();
        _creatingLocalSymbols = false;
        ImportSymbols(Catalog.GetSharedSymbolTable("$ion")!.Symbols);
        _localMinId = _symbols.Count + 1;
    }

    public void Create(IonStruct symbolTableData, bool yjLocalSymbols = false)
    {
        if (symbolTableData.ContainsKey("imports"))
        {
            var imports = symbolTableData["imports"];
            if (imports is IonSymbol symbol)
            {
                if (symbol.Value != "$ion_symbol_table")
                {
                    throw new InvalidOperationException($"非預期的 imports 值：{symbol.Value}");
                }
            }
            else
            {
                Clear();
                foreach (var symImport in (IEnumerable<object>)imports)
                {
                    var data = (IonStruct)symImport;
                    ImportSharedSymbolTable(
                        (string)data.GetValueOrDefault("name"),
                        data.TryGetValue("version", out var version) ? Convert.ToInt32(version) : 1,
                        data.TryGetValue("max_id", out var maxId) ? Convert.ToInt32(maxId) : null);
                }
            }
        }
        else
        {
            Clear();
        }

        var symbolList = symbolTableData.TryGetValue("symbols", out var symbolsValue)
            ? IonUtil.Unannotated(symbolsValue) as IEnumerable<object>
            : Array.Empty<object>();

        _creatingLocalSymbols = true;
        ImportSymbols(symbolList ?? Array.Empty<object>());

        if (symbolTableData.TryGetValue("max_id", out var expectedMax))
        {
            var expected = expectedMax is null ? -1 : Convert.ToInt32(expectedMax);
            if (expected >= 0 && expected != _symbols.Count)
            {
                Log.Warning($"符號表 max_id 預期 {expected}，實際 {_symbols.Count}");
            }
        }
    }

    public void ImportSharedSymbolTable(string name, int? version = null, int? maxId = null)
    {
        if (_creatingLocalSymbols)
        {
            throw new InvalidOperationException("已在建立 local symbol 後匯入 shared symbol");
        }

        if (name == "$ion") return;

        var table = Catalog.GetSharedSymbolTable(name, version);
        if (table is null)
        {
            Log.Warning($"匯入未知的 shared symbol table：{name}");
            table = new IonSharedSymbolTable(name ?? "", version ?? 1);
        }

        var actualVersion = version ?? table.Version;
        var tableLength = table.Symbols.Count;
        var effectiveMax = maxId ?? tableLength;
        if (effectiveMax < 0) throw new InvalidOperationException($"symbol table {name} max_id 不合法");

        _tableImports.Add(new SymbolTableImport(name, actualVersion, effectiveMax));

        IEnumerable<string> symbolList;
        if (effectiveMax < tableLength)
        {
            symbolList = table.Symbols.Take(effectiveMax);
        }
        else if (effectiveMax > tableLength)
        {
            symbolList = table.Symbols.Concat(Enumerable.Repeat<string>(null, effectiveMax - tableLength));
        }
        else
        {
            symbolList = table.Symbols;
        }

        ImportSymbols(symbolList);
        _localMinId = _symbols.Count + 1;
    }

    public void ImportSymbols(IEnumerable<object> symbols)
    {
        foreach (var raw in symbols)
        {
            var symbol = IonUtil.Unannotated(raw);
            string value = null;
            if (symbol is not null)
            {
                if (symbol is string s) value = s;
                else
                {
                    Log.Warning($"匯入的 symbol 型別不是字串：{symbol}");
                }
            }

            AddSymbol(value);
        }
    }

    public void ImportSymbols(IEnumerable<string> symbols)
        => ImportSymbols(symbols.Cast<object>());

    public IonSymbol CreateLocalSymbol(string symbol)
    {
        _creatingLocalSymbols = true;
        if (!_idOfSymbol.ContainsKey(symbol))
        {
            AddSymbol(symbol);
        }

        return new IonSymbol(symbol);
    }

    private int AddSymbol(string symbol)
    {
        if (symbol is null)
        {
            _symbols.Add(null);
            return -1;
        }

        if (symbol.Length == 0) throw new InvalidOperationException("symbol 長度為零");

        if (!_creatingLocalSymbols && symbol.EndsWith('?'))
        {
            symbol = symbol[..^1];
        }

        _symbols.Add(symbol);
        if (!_idOfSymbol.ContainsKey(symbol))
        {
            var id = _symbols.Count;
            _idOfSymbol[symbol] = id;
            _symbolOfId[id] = symbol;
            return id;
        }

        _symbolOfId[_symbols.Count] = symbol;
        return _idOfSymbol[symbol];
    }

    public IonSymbol GetSymbol(long symbolId)
    {
        if (!_symbolOfId.TryGetValue(checked((int)symbolId), out var symbol) || symbol is null)
        {
            symbol = "$" + symbolId;
            UndefinedIds.Add(symbolId);
        }

        return new IonSymbol(symbol);
    }

    public int GetId(IonSymbol ionSymbol, bool used = true)
    {
        var symbol = ionSymbol.Value;
        int symbolId;
        if (Regex.IsMatch(symbol, @"^\$[0-9]+$"))
        {
            symbolId = int.Parse(symbol[1..]);
            if (!_symbolOfId.ContainsKey(symbolId)) UndefinedIds.Add(symbolId);
        }
        else if (!_idOfSymbol.TryGetValue(symbol, out symbolId))
        {
            if (used) UndefinedSymbols.Add(symbol);
            symbolId = 0;
        }

        return symbolId;
    }

    public bool IsSharedSymbol(string name)
    {
        var id = GetId(new IonSymbol(name), used: false);
        return id > 0 && id < _localMinId;
    }

    public bool IsLocalSymbol(string name) => GetId(new IonSymbol(name), used: false) >= _localMinId;

    public IReadOnlyList<string> GetLocalSymbols() => _symbols.Skip(_localMinId - 1).ToList();

    public void DiscardLocalSymbols()
    {
        var symbolId = _localMinId;
        foreach (var symbol in _symbols.Skip(_localMinId - 1))
        {
            if (symbol is not null) _idOfSymbol.Remove(symbol);
            _symbolOfId.Remove(symbolId);
            symbolId++;
        }

        _symbols.RemoveRange(_localMinId - 1, _symbols.Count - (_localMinId - 1));
    }
}
