using System.IO;
using System.Text;
using Kfx2Epub.Core;
using Kfx2Epub.Epub;
using Kfx2Epub.Kfx;

namespace Kfx2Epub;

public static class BatchConverter
{
    public static int Run(string input, string output)
    {
        var inputPath = Path.GetFullPath(input);
        var outputPath = Path.GetFullPath(output);

        Console.WriteLine($"📂 輸入路徑：{inputPath}");
        Console.WriteLine($"📂 輸出路徑：{outputPath}");

        if (!Directory.Exists(inputPath) && !File.Exists(inputPath))
        {
            Console.WriteLine($"❌ 錯誤：找不到輸入路徑：{inputPath}");
            return 1;
        }

        Directory.CreateDirectory(outputPath);

        List<string> files;
        if (File.Exists(inputPath))
        {
            var ext = Path.GetExtension(inputPath).ToLowerInvariant();
            if (ext is not (".kfx" or ".kfx-zip"))
            {
                Console.WriteLine("💡 提示：輸入是檔案，但不是 .kfx 或 .kfx-zip。");
                return 1;
            }

            files = new List<string> { inputPath };
        }
        else
        {
            files = Directory.EnumerateFiles(inputPath, "*.kfx-zip", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(inputPath, "*.kfx", SearchOption.TopDirectoryOnly))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (files.Count == 0)
        {
            Console.WriteLine("💡 提示：沒有找到 .kfx-zip 或 .kfx 檔案。");
            return 1;
        }

        Console.WriteLine($"🔍 找到 {files.Count} 個檔案，開始轉換...");
        Console.WriteLine(new string('-', 50));

        var success = 0;
        var failed = 0;
        foreach (var file in files)
        {
            Console.WriteLine($"⏳ 解析來源檔案：{Path.GetFileName(file)}");
            try
            {
                var book = new YjBook(file);
                book.DecodeBook(retainYjLocals: true);
                var converter = new KfxEpub(book);
                var title = string.IsNullOrEmpty(converter.Title) ? "Unknown" : converter.Title;
                var author = converter.Authors.Count > 0 ? converter.Authors[0] : "Unknown";
                var cleanFilename = NameUtil.SanitizeFilename($"[{author}] {title}") + ".epub";
                var outputFile = UniqueOutputFile(outputPath, cleanFilename);
                Console.WriteLine($"📝 預期輸出檔名：{(outputFile.Directory!.Name != Path.GetFileName(outputPath) ? outputFile.Directory.Name + "/" : "")}{outputFile.Name}");

                var data = converter.DecompileToEpub();
                book.FinalActions();
                if (data is { Length: > 0 })
                {
                    File.WriteAllBytes(outputFile.FullName, data);
                    Console.WriteLine("   ✅ 已成功產生 EPUB。");
                    success++;
                }
                else
                {
                    Console.WriteLine("   ❌ 轉換失敗：產生的 EPUB 資料為空。");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ❌ 錯誤：{ex.Message}");
                if (Environment.GetEnvironmentVariable("KFX2EPUB_VERBOSE") == "1") Console.Error.WriteLine(ex);
                failed++;
            }

            Console.WriteLine();
        }

        Console.WriteLine(new string('-', 50));
        Console.WriteLine("🎉 批次處理完成。");
        Console.WriteLine($"📊 結果：成功 {success}，失敗 {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static FileInfo UniqueOutputFile(string outputPath, string filename)
    {
        var outputFile = new FileInfo(Path.Combine(outputPath, filename));
        if (!outputFile.Exists) return outputFile;

        var index = 1;
        while (true)
        {
            var duplicateFolder = Path.Combine(outputPath, $"dupname{index}");
            outputFile = new FileInfo(Path.Combine(duplicateFolder, filename));
            if (!outputFile.Exists)
            {
                Directory.CreateDirectory(duplicateFolder);
                return outputFile;
            }

            index++;
        }
    }
}
