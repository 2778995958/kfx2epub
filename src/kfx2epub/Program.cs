using System.Runtime.InteropServices;

namespace Kfx2Epub;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

    [STAThread]
    private static int Main(string[] args)
    {
        var result = Cli.CliRunner.Run(args);

        // 從檔案總管拖曳或雙擊啟動時，Windows 會開一個只有本程式的主控台，
        // 程式一結束視窗就關掉。這時停下來等 Enter，讓使用者看得到結果與錯誤。
        // 子命令、--no-pause、重導向的 stdout 一律不等待。
        if (result.AllowPause) PauseIfLaunchedFromExplorer();
        return result.Code;
    }

    private static void PauseIfLaunchedFromExplorer()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected) return;

        // 主控台只有自己一個行程，代表是檔案總管新開的視窗；命令列啟動時至少還有 shell。
        var processes = new uint[4];
        if (GetConsoleProcessList(processes, (uint)processes.Length) != 1) return;

        Console.WriteLine();
        Console.Write("按 Enter 關閉視窗...");
        Console.ReadLine();
    }

    internal static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("使用方式：kfx2epub [選項] [輸入資料夾或檔案] [輸出資料夾]");
        writer.WriteLine();
        writer.WriteLine("選項：");
        writer.WriteLine(
            $"  -w, --workers <N>  平行轉換的工作者數量（預設 {BatchConverter.DefaultWorkers()}，即 CPU 邏輯核心數的一半）");
        writer.WriteLine("  --no-pause         結束後不要等待按 Enter");
        writer.WriteLine("  -h, --help         顯示本說明");
        writer.WriteLine();
        writer.WriteLine("輸入資料夾：拖曳或指定資料夾時會遞迴搜尋子資料夾；直接雙擊（使用預設 archived_kfx）只搜尋最上層。");
        writer.WriteLine("未指定輸出路徑時，輸出到執行檔旁的 archived_epub。");
        writer.WriteLine();
        writer.WriteLine("子命令：kfx2epub <version|capabilities|probe|convert> ...（見 CLI_CONTRACT.md）");
    }
}
