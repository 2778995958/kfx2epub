using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Kfx2Epub;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [STAThread]
    private static int Main(string[] args)
    {
        var exitCode = Execute(args);

        // 從檔案總管拖曳或雙擊啟動時，Windows 會開一個只有本程式的主控台，
        // 程式一結束視窗就關掉。這時停下來等 Enter，讓使用者看得到結果與錯誤。
        PauseIfLaunchedFromExplorer();
        return exitCode;
    }

    private static int Execute(string[] args)
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

        // 沒有參數（雙擊）時只掃描預設 archived_kfx 的最上層；
        // 拖曳資料夾或由命令列指定資料夾時才遞迴子資料夾。
        var recursive = positional.Count > 0;

        var appDir = AppContext.BaseDirectory;
        var input = positional.Count > 0 ? positional[0] : Path.Combine(appDir, "archived_kfx");
        var output = positional.Count > 1 ? positional[1] : Path.Combine(appDir, "archived_epub");

        try
        {
            return BatchConverter.Run(input, output, workers, recursive);
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

    private static void PauseIfLaunchedFromExplorer()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected) return;

        // 主控台只有自己一個行程，代表是檔案總管新開的視窗；命令列啟動時至少還有 shell。
        var processes = new uint[4];
        if (GetConsoleProcessList(processes, (uint)processes.Length) != 1) return;

        Console.WriteLine();
        Console.Write("按 Enter 關閉視窗...");
        Console.ReadLine();
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("使用方式：kfx2epub [選項] [輸入資料夾或檔案] [輸出資料夾]");
        writer.WriteLine();
        writer.WriteLine("選項：");
        writer.WriteLine(
            $"  -w, --workers <N>  平行轉換的工作者數量（預設 {BatchConverter.DefaultWorkers()}，即 CPU 邏輯核心數的一半）");
        writer.WriteLine("  -h, --help         顯示本說明");
        writer.WriteLine();
        writer.WriteLine("輸入資料夾：拖曳或指定資料夾時會遞迴搜尋子資料夾；直接雙擊（使用預設 archived_kfx）只搜尋最上層。");
        writer.WriteLine("未指定輸出路徑時，輸出到執行檔旁的 archived_epub。");
    }
}
