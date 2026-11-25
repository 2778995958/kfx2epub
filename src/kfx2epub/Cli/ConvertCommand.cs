using System.Diagnostics;
using System.Security.Cryptography;
using Kfx2Epub.Core;
using Kfx2Epub.Epub;
using Kfx2Epub.Kfx;

namespace Kfx2Epub.Cli;

public static class ConvertCommand
{
    public static Dictionary<string, object> Execute(ParsedCommand parsed)
    {
        var input = BookInput.Normalize(CommandLine.RequireSinglePositional(parsed));
        var outArg = parsed.Value("--out");
        if (string.IsNullOrEmpty(outArg)) throw CliException.Usage("convert 必須指定 --out <path|->。");

        var format = parsed.Value("--format") ?? "epub3";
        if (format is not ("epub3" or "epub2"))
        {
            throw CliException.Usage("--format 只接受 epub3 或 epub2。");
        }

        var progress = parsed.Value("--progress") ?? "none";
        if (progress != "none")
        {
            // capabilities.progress_ndjson 目前為 false；不得靜默忽略。
            throw new CliException("USAGE_ERROR", "此版本不支援 --progress=ndjson。", emitJson: true);
        }

        var workers = parsed.Value("--workers");
        if (workers is not null && (!int.TryParse(workers, out var workerCount) || workerCount <= 0))
        {
            throw CliException.Usage("--workers 必須是正整數。");
        }

        var force = parsed.Flags.Contains("--force");
        var forceCover = parsed.Flags.Contains("--force-cover");
        var reproducible = parsed.Flags.Contains("--reproducible");
        var toStdout = outArg == "-";

        BookInput.ValidateFile(input);
        BookInput.RequireSupportedFormat(input);

        var stopwatch = Stopwatch.StartNew();
        byte[] data;
        var summary = new BookSummary();

        Cancellation.Install();
        try
        {
            var book = new YjBook(input, reproducible);
            book.DecodeBook(retainYjLocals: true);
            Cancellation.ThrowIfRequested();

            var converter = new KfxEpub(book, format == "epub2", forceCover) { Reproducible = reproducible };
            summary.Title = converter.Title;
            summary.Authors.AddRange(converter.Authors);
            summary.Language = converter.Language;
            summary.FixedLayout = converter.FixedLayout;
            summary.PdfBacked = converter.IsPdfBacked;
            if (converter.FixedLayout)
            {
                var count = 0;
                foreach (var order in converter.ReadingOrders)
                {
                    if (order.GetValueOrDefault("$170") is List<object> locations) count += locations.Count;
                }

                summary.PageCount = count > 0 ? count : null;
            }

            data = converter.DecompileToEpub();
            book.FinalActions();
            Cancellation.ThrowIfRequested();

            if (data is not { Length: > 0 })
            {
                throw new CliException("CORRUPT_INPUT", "轉換失敗：產生的 EPUB 資料為空。");
            }

            string output;
            if (toStdout)
            {
                var stdout = Console.OpenStandardOutput();
                stdout.Write(data, 0, data.Length);
                stdout.Flush();
                output = "-";
            }
            else
            {
                if (outArg.EndsWith(Path.DirectorySeparatorChar) || Directory.Exists(outArg))
                {
                    var directory = Path.GetFullPath(outArg);
                    Directory.CreateDirectory(directory);
                    output = Path.Combine(directory, AllocateFilename(directory, summary));
                }
                else
                {
                    output = Path.GetFullPath(outArg);
                }

                WriteAtomic(output, data, force);
            }

            stopwatch.Stop();
            CliConsole.Info($"✅ {Path.GetFileName(input)} → {output}（{stopwatch.Elapsed.TotalSeconds:0.0} 秒，{data.Length} bytes）");

            return new Dictionary<string, object>
            {
                ["api"] = CliContract.Api,
                ["ok"] = true,
                ["command"] = "convert",
                ["input"] = input,
                ["output"] = output,
                ["bytes"] = data.Length,
                ["sha256"] = BookInput.ToHex(SHA256.HashData(data)),
                ["elapsed_ms"] = (int)stopwatch.ElapsedMilliseconds,
                ["book"] = new Dictionary<string, object>
                {
                    ["title"] = string.IsNullOrEmpty(summary.Title) ? null : summary.Title,
                    ["authors"] = summary.Authors,
                    ["language"] = string.IsNullOrEmpty(summary.Language) ? null : summary.Language,
                    ["page_count"] = summary.PageCount,
                    ["drm"] = false,
                    ["pdf_backed"] = summary.PdfBacked
                }
            };
        }
        finally
        {
            Cancellation.Uninstall();
        }
    }

    private static string AllocateFilename(string directory, BookSummary summary)
    {
        var title = string.IsNullOrEmpty(summary.Title) ? "Unknown" : summary.Title;
        var author = summary.Authors.Count > 0 ? summary.Authors[0] : "Unknown";
        var filename = NameUtil.SanitizeFilename($"[{author}] {title}") + ".epub";

        var candidate = Path.Combine(directory, filename);
        if (!File.Exists(candidate)) return filename;

        for (var index = 1;; index++)
        {
            var duplicate = Path.Combine(directory, $"dupname{index}", filename);
            if (!File.Exists(duplicate)) return Path.Combine($"dupname{index}", filename);
        }
    }

    // 先寫同目錄 .part，成功後才更名；未加 --force 時以不覆寫的方式更名，避免競爭產生半成品。
    private static void WriteAtomic(string finalPath, byte[] data, bool force)
    {
        var directory = Path.GetDirectoryName(finalPath);
        if (string.IsNullOrEmpty(directory)) directory = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);

        if (!force && File.Exists(finalPath))
        {
            throw new CliException("OUTPUT_EXISTS", $"輸出路徑已存在：{finalPath}");
        }

        var token = Guid.NewGuid().ToString("N")[..8];
        var temporary = Path.Combine(directory, $".{Path.GetFileName(finalPath)}.kfx2epub-{token}.part");

        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }

            Cancellation.ThrowIfRequested();

            try
            {
                File.Move(temporary, finalPath, force);
            }
            catch (IOException) when (!force && File.Exists(finalPath))
            {
                throw new CliException("OUTPUT_EXISTS", $"輸出路徑已存在：{finalPath}");
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                    // 清理失敗不覆蓋原始錯誤；呼叫端仍可依 .part 名稱自行清除。
                }
            }
        }
    }
}
