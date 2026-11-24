using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using Kfx2Epub.Core;
using Kfx2Epub.Epub;
using Kfx2Epub.Kfx;

namespace Kfx2Epub;

public static class BatchConverter
{
    // 每本書彼此獨立，平行單位就取「檔案」；不在單本書內再切執行緒。
    // 預設取 CPU 邏輯核心數的一半，與 Python 版 `-w` 的預設值一致。
    public static int DefaultWorkers()
    {
        return Math.Max(1, Environment.ProcessorCount / 2);
    }

    public static int Run(string input, string output, int workers = 0, bool recursive = true)
    {
        var inputPath = Path.GetFullPath(input);
        var outputPath = Path.GetFullPath(output);
        var directoryInput = !File.Exists(inputPath);

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
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true
            };
            files = Directory.EnumerateFiles(inputPath, "*", options)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".kfx" or ".kfx-zip")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (files.Count == 0)
        {
            Console.WriteLine(recursive
                ? "💡 提示：沒有找到 .kfx-zip 或 .kfx 檔案（含子資料夾）。"
                : "💡 提示：沒有找到 .kfx-zip 或 .kfx 檔案（只搜尋最上層）。");

            if (!recursive)
            {
                Console.WriteLine("   把資料夾拖曳到程式上，或指定資料夾路徑，就會一併搜尋子資料夾。");
            }

            return 1;
        }

        if (workers <= 0) workers = DefaultWorkers();
        workers = Math.Clamp(workers, 1, files.Count);

        var scope = recursive ? "含子資料夾" : "僅最上層";
        Console.WriteLine(directoryInput
            ? workers > 1
                ? $"🔍 找到 {files.Count} 個檔案，開始轉換（平行工作者：{workers}，{scope}）..."
                : $"🔍 找到 {files.Count} 個檔案，開始轉換（{scope}）..."
            : workers > 1
                ? $"🔍 找到 {files.Count} 個檔案，開始轉換（平行工作者：{workers}）..."
                : $"🔍 找到 {files.Count} 個檔案，開始轉換...");
        Console.WriteLine(new string('-', 50));

        var allocator = new OutputAllocator(outputPath);
        var total = files.Count;
        var stopwatch = Stopwatch.StartNew();
        var results = new ConversionResult[total];

        if (workers == 1)
        {
            // 單一工作者直接在主執行緒（STA）跑，行為與舊版完全相同。
            for (var i = 0; i < total; i++)
            {
                results[i] = ConvertOne(i + 1, total, files[i], allocator);
                PrintResult(results[i]);
            }
        }
        else
        {
            RunWorkers(files, workers, allocator, results);
        }

        stopwatch.Stop();

        var success = results.Count(r => r is { Success: true });
        var failed = total - success;

        Console.WriteLine(new string('-', 50));
        Console.WriteLine($"🎉 批次處理完成（耗時 {stopwatch.Elapsed.TotalSeconds:0.0} 秒，工作者：{workers}）。");
        Console.WriteLine($"📊 結果：成功 {success}，失敗 {failed}");
        Console.WriteLine($"📂 輸出資料夾：{outputPath}");
        return failed == 0 ? 0 : 1;
    }

    private sealed class OutputAllocator
    {
        private readonly string _outputPath;
        private readonly object _gate = new();
        private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

        public OutputAllocator(string outputPath)
        {
            _outputPath = outputPath;
        }

        // 回傳尚未被其他工作者保留、磁碟上也不存在的輸出路徑。
        // 保留上限在記憶體中完成，避免兩本書同名時同時寫入同一個檔案。
        public FileInfo Allocate(string filename)
        {
            lock (_gate)
            {
                var candidate = new FileInfo(Path.Combine(_outputPath, filename));
                if (!candidate.Exists && _reserved.Add(candidate.FullName)) return candidate;

                for (var index = 1;; index++)
                {
                    var duplicateFolder = Path.Combine(_outputPath, $"dupname{index}");
                    candidate = new FileInfo(Path.Combine(duplicateFolder, filename));
                    if (!candidate.Exists && _reserved.Add(candidate.FullName)) return candidate;
                }
            }
        }

        // 轉換失敗時把保留的名稱放回去，讓後續同名檔案仍可寫到原位置。
        public void Release(FileInfo file)
        {
            lock (_gate)
            {
                _reserved.Remove(file.FullName);
            }
        }
    }

    private sealed record ConversionResult(
        int Index, int Total, string SourceName, bool Success, string OutputName, string Error,
        TimeSpan Elapsed, string ExceptionText);

    private static readonly object ConsoleGate = new();

    private static void RunWorkers(List<string> files, int workers, OutputAllocator allocator,
        ConversionResult[] results)
    {
        var queue = new ConcurrentQueue<(int Index, string File)>();
        for (var i = 0; i < files.Count; i++) queue.Enqueue((i + 1, files[i]));

        var threads = new List<Thread>();
        for (var worker = 0; worker < workers; worker++)
        {
            var thread = new Thread(() =>
            {
                while (queue.TryDequeue(out var item))
                {
                    var result = ConvertOne(item.Index, files.Count, item.File, allocator);
                    results[item.Index - 1] = result;
                    PrintResult(result);
                }
            })
            {
                IsBackground = true,
                Name = $"kfx2epub-worker-{worker + 1}"
            };

            // WPF 的圖片解碼／RenderTargetBitmap 在部分路徑需要 STA，而執行緒池是 MTA，
            // 所以這裡自建 STA 執行緒，而不是直接用 Parallel.ForEach。
            if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
            threads.Add(thread);
            thread.Start();
        }

        foreach (var thread in threads) thread.Join();
    }

    private static ConversionResult ConvertOne(int index, int total, string file, OutputAllocator allocator)
    {
        var sourceName = Path.GetFileName(file);
        var stopwatch = Stopwatch.StartNew();

        lock (ConsoleGate)
        {
            Console.WriteLine($"⏳ [{index}/{total}] 解析來源檔案：{sourceName}");
        }

        FileInfo outputFile = null;
        try
        {
            var book = new YjBook(file);
            book.DecodeBook(retainYjLocals: true);
            var converter = new KfxEpub(book);

            var title = string.IsNullOrEmpty(converter.Title) ? "Unknown" : converter.Title;
            var author = converter.Authors.Count > 0 ? converter.Authors[0] : "Unknown";
            var cleanFilename = NameUtil.SanitizeFilename($"[{author}] {title}") + ".epub";
            outputFile = allocator.Allocate(cleanFilename);

            var data = converter.DecompileToEpub();
            book.FinalActions();

            if (data is not { Length: > 0 })
            {
                allocator.Release(outputFile);
                return new ConversionResult(index, total, sourceName, false, null,
                    "轉換失敗：產生的 EPUB 資料為空。", stopwatch.Elapsed, null);
            }

            Directory.CreateDirectory(outputFile.Directory!.FullName);
            File.WriteAllBytes(outputFile.FullName, data);

            var outputName = outputFile.Directory!.Name.StartsWith("dupname", StringComparison.Ordinal)
                ? $"{outputFile.Directory.Name}/{outputFile.Name}"
                : outputFile.Name;
            return new ConversionResult(index, total, sourceName, true, outputName, null,
                stopwatch.Elapsed, null);
        }
        catch (Exception ex)
        {
            if (outputFile is not null) allocator.Release(outputFile);
            return new ConversionResult(index, total, sourceName, false, null, ex.Message,
                stopwatch.Elapsed, ex.ToString());
        }
    }

    private static void PrintResult(ConversionResult result)
    {
        lock (ConsoleGate)
        {
            if (result.Success)
            {
                Console.WriteLine(
                    $"✅ [{result.Index}/{result.Total}] {result.SourceName} → {result.OutputName}（{result.Elapsed.TotalSeconds:0.0} 秒）");
            }
            else
            {
                Console.WriteLine($"❌ [{result.Index}/{result.Total}] {result.SourceName}");
                Console.WriteLine($"   錯誤：{result.Error}");
            }

            if (result.ExceptionText is not null && Log.Verbose)
            {
                Console.Error.WriteLine(result.ExceptionText);
            }

            Console.WriteLine();
        }
    }
}
