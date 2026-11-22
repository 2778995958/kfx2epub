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

        var appDir = AppContext.BaseDirectory;
        var input = args.Length > 0 ? args[0] : Path.Combine(appDir, "archived_kfx");
        var output = args.Length > 1 ? args[1] : Path.Combine(appDir, "archived_epub");

        if (args.Length > 2)
        {
            Console.Error.WriteLine("使用方式：kfx2epub [輸入資料夾或檔案] [輸出資料夾]");
            return 2;
        }

        try
        {
            return BatchConverter.Run(input, output);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ 錯誤：{ex.Message}");
            return 1;
        }
    }
}
