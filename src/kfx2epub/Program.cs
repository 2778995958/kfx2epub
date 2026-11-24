using System.IO;
using System.Text;

namespace Kfx2Epub;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        Core.Log.Verbose = Environment.GetEnvironmentVariable("KFX2EPUB_VERBOSE") == "1";

        var positional = new List<string>();
        var workers = 0;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                PrintUsage(Console.Out);
                return 0;
            }

            if (arg is "-w" or "--workers")
            {
                if (i + 1 >= args.Length || !TryParseWorkers(args[++i], out workers))
                {
                    PrintUsage(Console.Error);
                    return 2;
                }

                continue;
            }

            if (arg.StartsWith("--workers=", StringComparison.Ordinal))
            {
                if (!TryParseWorkers(arg["--workers=".Length..], out workers))
                {
                    PrintUsage(Console.Error);
                    return 2;
                }

                continue;
            }

            if (arg.StartsWith('-') && arg.Length > 1)
            {
                Console.Error.WriteLine($"❌ 未知的選項：{arg}");
                PrintUsage(Console.Error);
                return 2;
            }

            positional.Add(arg);
        }

        if (positional.Count > 2)
        {
            PrintUsage(Console.Error);
            return 2;
        }

        var appDir = AppContext.BaseDirectory;
        var input = positional.Count > 0 ? positional[0] : Path.Combine(appDir, "archived_kfx");
        var output = positional.Count > 1 ? positional[1] : Path.Combine(appDir, "archived_epub");

        try
        {
            return BatchConverter.Run(input, output, workers);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ 錯誤：{ex.Message}");
            return 1;
        }
    }

    private static bool TryParseWorkers(string value, out int workers)
    {
        return int.TryParse(value, out workers) && workers > 0;
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("使用方式：kfx2epub [選項] [輸入資料夾或檔案] [輸出資料夾]");
        writer.WriteLine();
        writer.WriteLine("選項：");
        writer.WriteLine(
            $"  -w, --workers <N>  平行轉換的工作者數量（預設 {BatchConverter.DefaultWorkers()}，即 CPU 邏輯核心數的一半）");
        writer.WriteLine("  -h, --help         顯示本說明");
    }
}
