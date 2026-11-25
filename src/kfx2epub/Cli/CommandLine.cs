namespace Kfx2Epub.Cli;

public sealed class ParsedCommand
{
    public string Command { get; init; }
    public GlobalOptions Globals { get; } = new();
    public List<string> Positionals { get; } = new();
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
    public bool Help { get; set; }
    public bool ApiVersion { get; set; }

    public string Value(string name) => Values.GetValueOrDefault(name);
}

public static class CommandLine
{
    public static readonly HashSet<string> ReservedCommands = new(StringComparer.Ordinal)
    {
        "probe", "convert", "capabilities", "version"
    };

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "--log-file", "--out", "--format", "--progress", "--workers", "-w"
    };

    private static readonly HashSet<string> FlagOptions = new(StringComparer.Ordinal)
    {
        "--json", "--quiet", "--verbose", "--no-pause", "--help", "-h",
        "--force", "--force-cover", "--reproducible", "--api-version"
    };

    public static bool HasApiVersion(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg == "--") return false;
            if (arg == "--api-version") return true;
        }

        return false;
    }

    // 第一個「非選項」參數若是保留字才是子命令；Explorer 拖放傳入的是絕對路徑，不會衝突。
    public static string FirstMeaningfulToken(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--") return null;

            var (name, hasInlineValue) = SplitOption(arg);
            if (name is not null)
            {
                if (ValueOptions.Contains(name) && !hasInlineValue) i++;
                continue;
            }

            return arg;
        }

        return null;
    }

    public static ParsedCommand Parse(string command, string[] args)
    {
        var parsed = new ParsedCommand { Command = command };
        var skippedCommand = false;
        var stopOptions = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!stopOptions && arg == "--")
            {
                stopOptions = true;
                continue;
            }

            if (!stopOptions && arg.StartsWith('-') && arg.Length > 1)
            {
                var (name, hasInlineValue) = SplitOption(arg);
                if (name is null) throw CliException.Usage($"未知的選項：{arg}");

                string value = null;
                if (ValueOptions.Contains(name))
                {
                    if (hasInlineValue)
                    {
                        value = arg[(arg.IndexOf('=') + 1)..];
                    }
                    else
                    {
                        if (i + 1 >= args.Length) throw CliException.Usage($"選項 {name} 缺少值。");
                        value = args[++i];
                    }

                    if (name == "--log-file") parsed.Globals.LogFile = value;
                    else parsed.Values[name] = value;
                    continue;
                }

                if (!FlagOptions.Contains(name)) throw CliException.Usage($"未知的選項：{arg}");
                if (hasInlineValue) throw CliException.Usage($"選項 {name} 不接受值。");

                switch (name)
                {
                    case "--json": parsed.Globals.Json = true; break;
                    case "--quiet": parsed.Globals.Quiet = true; break;
                    case "--verbose": parsed.Globals.Verbose = true; break;
                    case "--no-pause": parsed.Globals.NoPause = true; break;
                    case "--help":
                    case "-h": parsed.Help = true; break;
                    case "--api-version": parsed.ApiVersion = true; break;
                    default: parsed.Flags.Add(name); break;
                }

                continue;
            }

            if (!skippedCommand && arg == command)
            {
                skippedCommand = true;
                continue;
            }

            parsed.Positionals.Add(arg);
        }

        return parsed;
    }

    public static void ValidateNoPositionals(ParsedCommand parsed)
    {
        if (parsed.Positionals.Count > 0)
        {
            throw CliException.Usage($"命令 {parsed.Command} 不接受位置參數：{parsed.Positionals[0]}");
        }
    }

    public static string RequireSinglePositional(ParsedCommand parsed)
    {
        if (parsed.Positionals.Count != 1)
        {
            throw CliException.Usage($"命令 {parsed.Command} 需要剛好一個輸入路徑。");
        }

        return parsed.Positionals[0];
    }

    private static (string Name, bool HasInlineValue) SplitOption(string arg)
    {
        if (!arg.StartsWith('-') || arg.Length <= 1) return (null, false);
        var index = arg.IndexOf('=');
        return index < 0 ? (arg, false) : (arg[..index], true);
    }
}
