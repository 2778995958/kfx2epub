using System.Text;

namespace Kfx2Epub.Cli;

public sealed class ExitResult
{
    public int Code { get; init; }
    public bool AllowPause { get; init; }

    public ExitResult(int code, bool allowPause = false)
    {
        Code = code;
        AllowPause = allowPause;
    }
}

public static class CliRunner
{
    public static ExitResult Run(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            Console.InputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 沒有主控台時設定輸入編碼可能失敗；不影響轉換。
        }

        if (CommandLine.HasApiVersion(args))
        {
            CliJson.WriteTextLine(CliContract.Api.ToString());
            return new ExitResult(0);
        }

        var command = CommandLine.FirstMeaningfulToken(args);
        if (command is not null && CommandLine.ReservedCommands.Contains(command))
        {
            return RunSubcommand(command, args);
        }

        return RunOldBatch(args);
    }

    private static ExitResult RunSubcommand(string command, string[] args)
    {
        ParsedCommand parsed;
        try
        {
            parsed = CommandLine.Parse(command, args);
        }
        catch (CliException ex)
        {
            PrintSubcommandUsage(command, Console.Error);
            CliConsole.Error($"❌ {ex.Message}");
            return new ExitResult(ex.ExitCode);
        }

        try
        {
            CliConsole.Configure(parsed.Globals);

            if (parsed.Help)
            {
                PrintSubcommandUsage(command, Console.Out);
                return new ExitResult(0);
            }

            switch (command)
            {
                case "version":
                    CommandLine.ValidateNoPositionals(parsed);
                    if (parsed.Globals.Json) CliJson.WriteLine(InfoCommands.VersionInfo());
                    else CliJson.WriteTextLine(FormatVersion(InfoCommands.VersionInfo()));
                    return new ExitResult(0);

                case "capabilities":
                    CommandLine.ValidateNoPositionals(parsed);
                    CliJson.WriteLine(InfoCommands.Capabilities());
                    return new ExitResult(0);

                case "probe":
                    CliJson.WriteLine(ProbeCommand.Execute(parsed));
                    return new ExitResult(0);

                case "convert":
                    var result = ConvertCommand.Execute(parsed);
                    // --out - 時 stdout 必須是純 EPUB 位元組，不得再輸出 JSON。
                    if (parsed.Globals.Json && Equals(result.GetValueOrDefault("output"), "-") == false)
                    {
                        CliJson.WriteLine(result);
                    }

                    return new ExitResult(0);

                default:
                    throw CliException.Usage($"未知的子命令：{command}");
            }
        }
        catch (CliException ex)
        {
            return HandleFailure(command, parsed, ex);
        }
        catch (Exception ex)
        {
            return HandleFailure(command, parsed, CliException.From(ex));
        }
        finally
        {
            CliConsole.Shutdown();
        }
    }

    private static ExitResult HandleFailure(string command, ParsedCommand parsed, CliException error)
    {
        if (error.Code == "USAGE_ERROR" && !error.EmitJson)
        {
            PrintSubcommandUsage(command, Console.Error);
            CliConsole.Error($"❌ {error.Message}");
            return new ExitResult(2);
        }

        var toStdout = command == "probe" ||
                       (command == "convert" && parsed.Globals.Json &&
                        !Equals(parsed.Value("--out"), "-"));
        if (toStdout) CliJson.WriteLine(CliJson.Error(command, error, parsed.Globals.Verbose));
        else CliConsole.Error($"❌ {error.Message}");

        return new ExitResult(error.ExitCode);
    }

    private static string FormatVersion(Dictionary<string, object> info)
    {
        var commit = info.GetValueOrDefault("commit") as string;
        var suffix = string.IsNullOrEmpty(commit) ? "" : $" ({commit})";
        return $"kfx2epub {info["version"]}{suffix}  api {info["api"]}  {info["runtime"]}  {info["platform"]}";
    }

    // 舊批次：維持既有參數與輸出；新增 -- 與 --no-pause。
    private static ExitResult RunOldBatch(string[] args)
    {
        var positional = new List<string>();
        var workers = 0;
        var noPause = false;
        var stopOptions = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!stopOptions && arg == "--")
            {
                stopOptions = true;
                continue;
            }

            if (!stopOptions && arg is "-h" or "--help")
            {
                Program.PrintUsage(Console.Out);
                return new ExitResult(0, !noPause);
            }

            if (!stopOptions && arg is "-w" or "--workers")
            {
                if (i + 1 >= args.Length || !TryParseWorkers(args[++i], out workers))
                {
                    Program.PrintUsage(Console.Error);
                    return new ExitResult(2, !noPause);
                }

                continue;
            }

            if (!stopOptions && arg.StartsWith("--workers=", StringComparison.Ordinal))
            {
                if (!TryParseWorkers(arg["--workers=".Length..], out workers))
                {
                    Program.PrintUsage(Console.Error);
                    return new ExitResult(2, !noPause);
                }

                continue;
            }

            if (!stopOptions && arg is "--no-pause")
            {
                noPause = true;
                continue;
            }

            if (!stopOptions && arg is "--verbose")
            {
                Core.Log.Verbose = true;
                continue;
            }

            if (!stopOptions && arg.StartsWith('-') && arg.Length > 1)
            {
                Console.Error.WriteLine($"❌ 未知的選項：{arg}");
                Program.PrintUsage(Console.Error);
                return new ExitResult(2, !noPause);
            }

            positional.Add(arg);
        }

        if (positional.Count > 2)
        {
            Program.PrintUsage(Console.Error);
            return new ExitResult(2, !noPause);
        }

        // 沒有參數（雙擊）時只掃描預設 archived_kfx 的最上層；
        // 拖曳資料夾或由命令列指定資料夾時才遞迴子資料夾。
        var recursive = positional.Count > 0;

        var appDir = AppContext.BaseDirectory;
        var input = positional.Count > 0 ? positional[0] : Path.Combine(appDir, "archived_kfx");
        var output = positional.Count > 1 ? positional[1] : Path.Combine(appDir, "archived_epub");

        try
        {
            var code = BatchConverter.Run(input, output, workers, recursive);
            return new ExitResult(code, !noPause);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ 錯誤：{ex.Message}");
            return new ExitResult(1, !noPause);
        }
    }

    private static bool TryParseWorkers(string value, out int workers)
        => int.TryParse(value, out workers) && workers > 0;

    public static void PrintSubcommandUsage(string command, TextWriter writer)
    {
        writer.WriteLine("使用方式：");
        switch (command)
        {
            case "version":
                writer.WriteLine("  kfx2epub version [--json]");
                break;
            case "capabilities":
                writer.WriteLine("  kfx2epub capabilities");
                break;
            case "probe":
                writer.WriteLine("  kfx2epub probe <input> [--json]");
                break;
            case "convert":
                writer.WriteLine("  kfx2epub convert <input> --out <path|-> [--force] [--format epub3|epub2]");
                writer.WriteLine("                           [--force-cover] [--reproducible] [--json]");
                break;
            default:
                writer.WriteLine("  kfx2epub <version|capabilities|probe|convert> ...");
                break;
        }

        writer.WriteLine();
        writer.WriteLine("全域選項：--json --quiet --verbose --no-pause --log-file <path>");
        writer.WriteLine("舊版批次：kfx2epub [--] [-w N] [輸入] [輸出]");
    }
}
