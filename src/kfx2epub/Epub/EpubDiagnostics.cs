using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kfx2Epub.Core;

namespace Kfx2Epub.Epub;

// 診斷用：設定環境變數 KFX2EPUB_REPORT_DIR 後，把每本書 CSS／HTML 的轉換去向
// 寫成 JSON。未設定時所有方法都立刻返回，轉換結果與關閉時完全相同。
public static class EpubDiagnostics
{
    private static readonly string RootDir = Environment.GetEnvironmentVariable("KFX2EPUB_REPORT_DIR");

    public static bool Enabled => !string.IsNullOrEmpty(RootDir);

    [ThreadStatic] private static BookReport _report;

    private const int MaxEvents = 200000;

    private sealed class BookReport
    {
        public string Source;
        public string Output;
        public string Error;
        public string Title;
        public string Author;
        public readonly List<Dictionary<string, object>> StyleToClass = new();
        public readonly List<Dictionary<string, object>> Publisher = new();
        public readonly List<Dictionary<string, object>> Inlined = new();
        public readonly List<Dictionary<string, object>> PageTypes = new();
        public readonly List<Dictionary<string, object>> KeptRules = new();
        public readonly List<Dictionary<string, object>> GeneratedRules = new();
        public readonly List<Dictionary<string, object>> CssFiles = new();
        public readonly Dictionary<string, object> Stats = new(StringComparer.Ordinal);
        public bool Truncated;

        public bool CanAdd => StyleToClass.Count + Publisher.Count + Inlined.Count + PageTypes.Count < MaxEvents;
    }

    private static bool Ready(out BookReport report)
    {
        report = _report;
        return Enabled && report is not null;
    }

    public static void BeginBook(string sourcePath)
    {
        if (!Enabled) return;
        _report = new BookReport { Source = sourcePath };
    }

    public static void SetBookInfo(string title, string author)
    {
        if (!Ready(out var report)) return;
        report.Title = title;
        report.Author = author;
    }

    public static void RecordFinal(KfxEpub epub)
    {
        if (!Ready(out var report)) return;

        var elements = 0;
        var inlineStyles = 0;
        var emptyStyles = 0;
        var classNames = new HashSet<string>(StringComparer.Ordinal);
        var files = 0;
        foreach (var bookPart in epub.BookParts)
        {
            if (bookPart.Html is null) continue;
            files++;
            foreach (var elem in bookPart.Html.DescendantsAndSelf())
            {
                elements++;
                var style = elem.Get("style");
                if (style is null) continue;
                inlineStyles++;
                if (style.Trim().Length == 0) emptyStyles++;
                foreach (var name in (elem.Get("class", "") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    classNames.Add(name);
                }
            }
        }

        report.Stats["htmlFiles"] = files;
        report.Stats["elements"] = elements;
        report.Stats["inlineStyleElements"] = inlineStyles;
        report.Stats["emptyStyleAttributes"] = emptyStyles;
        report.Stats["classesInHtml"] = classNames.Count;
    }

    // 在 InlineRemainingGeneratedStyles 開始前記錄所有生成規則，之後才能判斷每條規則的去向。
    public static void RecordGeneratedRules(Dictionary<string, Style> cssRules, Dictionary<string, string> extraRules,
        Dictionary<string, Dictionary<string, Style>> mediaQueries)
    {
        if (!Ready(out var report)) return;

        foreach (var (selector, style) in cssRules)
        {
            report.GeneratedRules.Add(new Dictionary<string, object>
            {
                ["selector"] = selector,
                ["decls"] = style.ToString(),
                ["media"] = null
            });
        }

        foreach (var (selector, decls) in extraRules)
        {
            report.GeneratedRules.Add(new Dictionary<string, object>
            {
                ["selector"] = selector,
                ["decls"] = decls,
                ["media"] = null
            });
        }

        foreach (var (media, rules) in mediaQueries)
        {
            foreach (var (selector, style) in rules)
            {
                report.GeneratedRules.Add(new Dictionary<string, object>
                {
                    ["selector"] = selector,
                    ["decls"] = style.ToString(),
                    ["media"] = media
                });
            }
        }
    }

    public static void StyleToClassEvent(string file, XNode element, string originalStyle, string className,
        IReadOnlyCollection<string> utilities, string declarations, bool dropped)
    {
        if (!Ready(out var report)) return;
        if (!report.CanAdd)
        {
            report.Truncated = true;
            return;
        }
        report.StyleToClass.Add(new Dictionary<string, object>
        {
            ["file"] = file,
            ["path"] = ElementPath(element),
            ["tag"] = element.Name,
            ["text"] = Snippet(element),
            ["style"] = originalStyle,
            ["class"] = className,
            ["utilities"] = utilities.ToList(),
            ["decls"] = declarations,
            ["dropped"] = dropped
        });
    }

    public static void StyleKeptInlineEvent(string file, XNode element, string originalStyle, string className,
        string declarations)
    {
        if (!Ready(out var report)) return;
        if (!report.CanAdd)
        {
            report.Truncated = true;
            return;
        }
        report.StyleToClass.Add(new Dictionary<string, object>
        {
            ["file"] = file,
            ["path"] = ElementPath(element),
            ["tag"] = element.Name,
            ["text"] = Snippet(element),
            ["style"] = originalStyle,
            ["class"] = className,
            ["utilities"] = new List<string>(),
            ["decls"] = declarations,
            ["dropped"] = false,
            ["fixedLayoutKeep"] = true
        });
    }

    public static void PageTypeEvent(string file, string beforeClass, string afterClass, string removedClass,
        string pageClass, bool publisherCss, string reason)
    {
        if (!Ready(out var report)) return;
        if (!report.CanAdd)
        {
            report.Truncated = true;
            return;
        }
        report.PageTypes.Add(new Dictionary<string, object>
        {
            ["file"] = file,
            ["before"] = beforeClass,
            ["after"] = afterClass,
            ["removed"] = removedClass,
            ["pageClass"] = pageClass,
            ["publisherCss"] = publisherCss,
            ["reason"] = reason
        });
    }

    public static void PublisherEvent(string file, XNode element, string oldClass, string newClass,
        string residual, string ruleDecls, string kind)
    {
        if (!Ready(out var report)) return;
        if (!report.CanAdd)
        {
            report.Truncated = true;
            return;
        }
        report.Publisher.Add(new Dictionary<string, object>
        {
            ["file"] = file,
            ["path"] = ElementPath(element),
            ["tag"] = element.Name,
            ["text"] = Snippet(element),
            ["old"] = oldClass,
            ["new"] = newClass,
            ["residual"] = residual,
            ["rule"] = ruleDecls,
            ["kind"] = kind
        });
    }

    public static void InlineEvent(string file, XNode element, IReadOnlyCollection<string> classes,
        string beforeStyle, string declarations, string afterStyle)
    {
        if (!Ready(out var report)) return;
        if (!report.CanAdd)
        {
            report.Truncated = true;
            return;
        }
        report.Inlined.Add(new Dictionary<string, object>
        {
            ["file"] = file,
            ["path"] = ElementPath(element),
            ["tag"] = element.Name,
            ["text"] = Snippet(element),
            ["classes"] = classes.ToList(),
            ["styleBefore"] = beforeStyle,
            ["decls"] = declarations,
            ["styleAfter"] = afterStyle
        });
    }

    public static void KeptRule(string selector, string declarations, string media = null)
    {
        if (!Ready(out var report)) return;
        report.KeptRules.Add(new Dictionary<string, object>
        {
            ["selector"] = selector,
            ["decls"] = declarations,
            ["media"] = media
        });
    }

    // 剪掉沒用到的規則後，還留在 style-kfx.css 裡的規則（含 @media 內的規則）。
    public static void RecordKeptRules(Dictionary<string, Style> cssRules, Dictionary<string, string> extraRules,
        Dictionary<string, Dictionary<string, Style>> mediaQueries)
    {
        if (!Ready(out var report)) return;
        foreach (var (selector, style) in cssRules) KeptRule(selector, style.ToString());
        foreach (var (selector, decls) in extraRules) KeptRule(selector, decls);
        foreach (var (media, rules) in mediaQueries)
        {
            foreach (var (selector, style) in rules) KeptRule(selector, style.ToString(), media);
        }
    }

    // 記錄實際輸出到 EPUB 的樣式檔（含 EnsureLinkedCssFiles 補上的 fixed-layout-jp.css）。
    public static void RecordCssFiles(KfxEpub epub)
    {
        if (!Ready(out var report)) return;
        foreach (var (path, file) in epub.OebpsFiles
                     .Where(p => p.Value.Mimetype == "text/css")
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var text = Encoding.UTF8.GetString(file.BinaryData);
            var blocks = Regex.Matches(text, @"\{").Count;
            var imports = Regex.Matches(text, "@import\\s+\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value)
                .ToList();
            report.CssFiles.Add(new Dictionary<string, object>
            {
                ["path"] = path,
                ["bytes"] = file.BinaryData.Length,
                ["blocks"] = blocks,
                ["imports"] = imports
            });
        }
    }

    public static void Stat(string key, object value)
    {
        if (!Ready(out var report)) return;
        report.Stats[key] = value;
    }

    public static void EndBook(string outputName, string error)
    {
        if (!Ready(out var report)) return;
        _report = null;

        report.Output = outputName;
        report.Error = error;
        report.Stats["truncated"] = report.Truncated;
        report.Stats["styleToClassEvents"] = report.StyleToClass.Count;
        report.Stats["publisherMappings"] = report.Publisher.Count;
        report.Stats["inlinedElements"] = report.Inlined.Count;
        report.Stats["keptRules"] = report.KeptRules.Count;
        report.Stats["generatedRules"] = report.GeneratedRules.Count;
        report.Stats["inlinedDeclarations"] = report.Inlined.Sum(e =>
            ((string)e["decls"]).Split(';', StringSplitOptions.RemoveEmptyEntries).Length);

        var payload = new Dictionary<string, object>
        {
            ["source"] = report.Source,
            ["output"] = report.Output,
            ["title"] = report.Title,
            ["author"] = report.Author,
            ["error"] = report.Error,
            ["stats"] = report.Stats,
            ["cssFiles"] = report.CssFiles,
            ["generatedRules"] = report.GeneratedRules,
            ["classes"] = BuildClasses(report),
            ["events"] = new Dictionary<string, object>
            {
                ["styleToClass"] = report.StyleToClass,
                ["pageTypes"] = report.PageTypes,
                ["publisher"] = report.Publisher,
                ["inlined"] = report.Inlined,
                ["keptRules"] = report.KeptRules
            }
        };

        try
        {
            var booksDir = Path.Combine(RootDir, "books");
            Directory.CreateDirectory(booksDir);
            var name = NameUtil.SanitizeFilename(Path.GetFileNameWithoutExtension(report.Source));
            name = name.Length > 80 ? name[..80] : name;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(report.Source)))[..8];
            var path = Path.Combine(booksDir, $"{name}-{hash}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warning($"寫入轉換診斷資料失敗：{ex.Message}");
        }
    }

    // 每條生成規則最後去了哪裡：publisher（換成出版社 class）、inline（併進 style）、
    // kept（因 ::first-line 等選擇器留在樣式表）、unused（沒有元素使用而被剪掉）。
    private static List<Dictionary<string, object>> BuildClasses(BookReport report)
    {
        var publisher = new HashSet<string>(report.Publisher.Select(e => (string)e["old"]), StringComparer.Ordinal);
        var inlined = new HashSet<string>(report.Inlined.SelectMany(e => (List<string>)e["classes"]), StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in report.KeptRules)
        {
            foreach (Match match in Regex.Matches((string)rule["selector"], @"\.([A-Za-z0-9_-]+)"))
            {
                kept.Add(match.Groups[1].Value);
            }
        }

        var usage = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var evt in report.StyleToClass)
        {
            var name = (string)evt["class"];
            usage[name] = usage.GetValueOrDefault(name) + 1;
        }

        var result = new List<Dictionary<string, object>>();
        foreach (var rule in report.GeneratedRules)
        {
            var selector = (string)rule["selector"];
            var match = Regex.Match(selector, @"^\.([A-Za-z0-9_-]+)");
            if (!match.Success) continue;
            var className = match.Groups[1].Value;
            var destination = publisher.Contains(className) ? "publisher"
                : kept.Contains(className) ? "kept"
                : inlined.Contains(className) ? "inline"
                : "unused";
            result.Add(new Dictionary<string, object>
            {
                ["class"] = className,
                ["selector"] = selector,
                ["decls"] = rule["decls"],
                ["media"] = rule["media"],
                ["used"] = usage.GetValueOrDefault(className),
                ["destination"] = destination
            });
        }

        return result;
    }

    public static string ElementPath(XNode element)
    {
        var parts = new List<string>();
        for (var node = element; node is not null; node = node.Parent)
        {
            var index = 1;
            if (node.Parent is not null)
            {
                foreach (var sibling in node.Parent.Children)
                {
                    if (ReferenceEquals(sibling, node)) break;
                    if (sibling.Name == node.Name) index++;
                }
            }

            var id = node.Get("id");
            parts.Add(string.IsNullOrEmpty(id) ? $"{node.Name}[{index}]" : $"{node.Name}#{id}[{index}]");
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    public static string Snippet(XNode element)
    {
        var builder = new StringBuilder();
        foreach (var node in element.DescendantsAndSelf())
        {
            if (string.IsNullOrEmpty(node.Text)) continue;
            foreach (var ch in node.Text)
            {
                if (char.IsWhiteSpace(ch)) continue;
                builder.Append(ch);
                if (builder.Length >= 24) return builder.ToString();
            }
        }

        return builder.ToString();
    }
}
