using System.Diagnostics;
using Kfx2Epub.Kfx;

namespace Kfx2Epub.Cli;

public static class ProbeCommand
{
    public static Dictionary<string, object> Execute(ParsedCommand parsed)
    {
        var input = BookInput.Normalize(CommandLine.RequireSinglePositional(parsed));
        var stopwatch = Stopwatch.StartNew();

        BookInput.ValidateFile(input);
        var format = BookInput.RequireSupportedFormat(input);

        var summary = new BookSummary();
        string kfxVersion = null;
        var drm = false;

        try
        {
            var book = new YjBook(input);
            // 只做到 DecodeBook，直接讀 fragment；不建構 KfxEpub、不產生檔案。
            book.DecodeBook(retainYjLocals: false, ignoreDrm: true);
            drm = book.DrmDetected;
            summary = BookMetadataReader.Read(book);
            kfxVersion = book.KfxVersion?.ToString();
        }
        catch (KfxDrmException)
        {
            drm = true;
        }
        catch (KfxFormatException ex)
        {
            throw new CliException("INPUT_NOT_KFX", "輸入不是可辨識的 KFX 檔案。", ex.ToString(), ex);
        }
        catch (KfxCorruptException ex)
        {
            throw new CliException("CORRUPT_INPUT", "KFX 檔案不完整或已損壞。", ex.ToString(), ex);
        }
        catch (EndOfStreamException ex)
        {
            throw new CliException("CORRUPT_INPUT", "KFX 檔案不完整或已損壞。", ex.ToString(), ex);
        }

        stopwatch.Stop();

        return new Dictionary<string, object>
        {
            ["api"] = CliContract.Api,
            ["ok"] = true,
            ["command"] = "probe",
            ["input"] = input,
            ["format"] = format,
            ["kfx_version"] = kfxVersion,
            ["title"] = string.IsNullOrEmpty(summary.Title) ? null : summary.Title,
            ["authors"] = summary.Authors,
            ["language"] = string.IsNullOrEmpty(summary.Language) ? null : summary.Language,
            ["page_count"] = summary.PageCount,
            ["drm"] = drm,
            ["fixed_layout"] = summary.FixedLayout,
            ["pdf_backed"] = summary.PdfBacked,
            ["elapsed_ms"] = (int)stopwatch.ElapsedMilliseconds
        };
    }
}
