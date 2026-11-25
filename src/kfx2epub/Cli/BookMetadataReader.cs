using Kfx2Epub.Core;
using Kfx2Epub.Epub;
using Kfx2Epub.Kfx;

namespace Kfx2Epub.Cli;

public sealed class BookSummary
{
    public string Title { get; set; }
    public List<string> Authors { get; } = new();
    public string Language { get; set; }
    public string Asin { get; set; }
    public string BookId { get; set; }
    public bool FixedLayout { get; set; }
    public bool PdfBacked { get; set; }
    public int? PageCount { get; set; }
}

// PROBE 只讀 metadata fragment（$490／$258）與 document data（$538），
// 不建構 KfxEpub、不產生任何檔案。
public static class BookMetadataReader
{
    public static BookSummary Read(YjBook book)
    {
        var summary = new BookSummary();
        var metadata = book.Fragments.Get("$490", first: true)?.Value as IonStruct;
        var simple = book.Fragments.Get("$258", first: true)?.Value as IonStruct;

        if (metadata is not null)
        {
            foreach (var category in (metadata.GetValueOrDefault("$491") as List<object>)?.OfType<IonStruct>() ??
                                     Enumerable.Empty<IonStruct>())
            {
                var cat = category.GetValueOrDefault("$495").ToStr() ?? "";
                foreach (var item in (category.GetValueOrDefault("$258") as List<object>)?.OfType<IonStruct>() ??
                                      Enumerable.Empty<IonStruct>())
                {
                    var key = item.GetValueOrDefault("$492").ToStr() ?? "";
                    Apply(summary, cat + "/" + key, item.GetValueOrDefault("$307"));
                }
            }
        }

        if (simple is not null)
        {
            foreach (var pair in simple)
            {
                var key = KfxEpub.MetadataNames.GetValueOrDefault(pair.Key, pair.Key);
                Apply(summary, key, pair.Value);
            }
        }

        if (summary.FixedLayout)
        {
            summary.PageCount = CountReadingOrderLocations(book);
        }

        return summary;
    }

    private static void Apply(BookSummary summary, string key, object value)
    {
        switch (key)
        {
            case "kindle_title_metadata/title":
            case "title":
                if (string.IsNullOrEmpty(summary.Title)) summary.Title = (value.ToStr() ?? "").Trim();
                break;
            case "kindle_title_metadata/author":
                if (value is string author && author.Length > 0) summary.Authors.Add(author);
                break;
            case "author":
                if (summary.Authors.Count == 0 && value is string authors)
                {
                    summary.Authors.AddRange(authors.Split('&')
                        .Select(a => a.Trim())
                        .Where(a => a.Length > 0));
                }

                break;
            case "kindle_title_metadata/language":
            case "language":
                summary.Language = value.ToStr() ?? "";
                break;
            case "kindle_title_metadata/ASIN":
            case "ASIN":
                if (string.IsNullOrEmpty(summary.Asin)) summary.Asin = value.ToStr() ?? "";
                break;
            case "kindle_title_metadata/book_id":
                summary.BookId = value.ToStr() ?? "";
                break;
            case "kindle_capability_metadata/yj_fixed_layout":
                summary.FixedLayout = true;
                if (value is int layout && layout is 2 or 3) summary.PdfBacked = true;
                break;
            case "kindle_capability_metadata/yj_textbook":
                summary.FixedLayout = true;
                summary.PdfBacked = true;
                break;
        }
    }

    public static int? CountReadingOrderLocations(YjBook book)
    {
        var document = book.Fragments.Get("$538", first: true)?.Value as IonStruct;
        var orders = document?.GetValueOrDefault("$169") as List<object>;
        if (orders is null)
        {
            var metadata = book.Fragments.Get("$490", first: true)?.Value as IonStruct;
            foreach (var category in (metadata?.GetValueOrDefault("$491") as List<object>)?.OfType<IonStruct>() ??
                                     Enumerable.Empty<IonStruct>())
            {
                if (category.GetValueOrDefault("$495").ToStr() != "kindle_title_metadata") continue;
                foreach (var item in (category.GetValueOrDefault("$258") as List<object>)?.OfType<IonStruct>() ??
                                      Enumerable.Empty<IonStruct>())
                {
                    if (item.GetValueOrDefault("$492").ToStr() != "reading_orders") continue;
                    orders = item.GetValueOrDefault("$307") as List<object>;
                }
            }
        }

        if (orders is null) return null;

        var count = 0;
        foreach (var order in orders.OfType<IonStruct>())
        {
            if (order.GetValueOrDefault("$170") is List<object> locations) count += locations.Count;
        }

        return count > 0 ? count : null;
    }
}
