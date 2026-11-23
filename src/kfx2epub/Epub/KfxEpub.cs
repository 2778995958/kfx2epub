using Kfx2Epub.Core;
using Kfx2Epub.Kfx;

namespace Kfx2Epub.Epub;

public partial class KfxEpub
{
    public KfxEpub(YjBook book, bool epub2Desired = false, bool forceCover = false)
    {
        // 第二個建構子不會呼叫無參數建構子，必須在此初始化通用字型名稱。
        foreach (var name in PropertyData.GenericFontNames) FixFontName(name, add: true, generic: true);

        Epub2Desired = epub2Desired;
        GenerateEpub2 = epub2Desired;
        ForceCover = forceCover;

        InitializeBookData(book);
        ProcessContentFeatures();
        ProcessFonts();
        ProcessDocumentData();
        ProcessDictionaryRules();
        ProcessMetadata();

        Book.IsDictionary = IsDictionary;
        Book.IsMagazine = IsMagazine;
        Book.IsPrintReplica = IsPrintReplica;
        Book.IsSample = IsSample;

        SetConditionOperators();
        ProcessAnchors();
        ProcessNavigation();

        if (!string.IsNullOrEmpty(CoverResource) && !HtmlCover)
        {
            var manifestEntry = ProcessExternalResource(CoverResource, imageRole: "cover").ManifestEntry;
            if (manifestEntry is not null) manifestEntry.IsCoverImage = true;
        }

        ProcessReadingOrder();
        FixupAnchorsAndHrefs();
        FixupIllustratedLayoutAnchors();
        UpdateDefaultFontAndLanguage();
        SetHtmlDefaults();
        FixupStylesAndClasses();
        NormalizeTcySpans();
        NormalizeFitImageWrappers();
        PrepareBookParts();
        ReportMissingPositions();
    }

    public byte[] DecompileToEpub() => GenerateEpub();

    public void PrepareBookParts()
    {
        foreach (var bookPart in BookParts)
        {
            var body = bookPart.Body();
            ReplaceEolWithBr(body);
            ResetPreformat();
            PreformatSpaces(body);
        }
    }

    public void ReplaceEolWithBr(XNode body)
    {
        var eolChars = new[] { "\n", "\r", "\u2028", "\u2029" };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var e in body.Descendants().ToList())
            {
                if (!string.IsNullOrEmpty(e.Text))
                {
                    foreach (var eol in eolChars)
                    {
                        var index = e.Text.IndexOf(eol, StringComparison.Ordinal);
                        if (index < 0) continue;
                        var pre = e.Text[..index];
                        var post = e.Text[(index + eol.Length)..];
                        e.Text = pre;
                        var br = new XNode("br");
                        e.Insert(0, br);
                        if (post.Length > 0) br.Tail = post;
                        changed = true;
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(e.Tail))
                {
                    foreach (var eol in eolChars)
                    {
                        var index = e.Tail.IndexOf(eol, StringComparison.Ordinal);
                        if (index < 0) continue;
                        var post = e.Tail[(index + eol.Length)..];
                        e.Tail = e.Tail[..index];
                        var br = new XNode("br");
                        var parent = e.Parent;
                        parent.Insert(parent.Children.IndexOf(e) + 1, br);
                        if (post.Length > 0) br.Tail = post;
                        changed = true;
                        break;
                    }
                }

                if (changed) break;
            }
        }
    }

    private bool _psFirstInBlock;
    private string _psPreviousChar = "";
    private bool _psPreviousReplaced;
    private bool _psPriorIsTail;
    private XNode _psPriorElem;

    public void ResetPreformat()
    {
        _psFirstInBlock = true;
        _psPreviousChar = "";
        _psPreviousReplaced = false;
        _psPriorIsTail = false;
        _psPriorElem = null;
    }

    public void PreformatSpaces(XNode elem)
    {
        if (elem.IsLocal("audio") || elem.IsLocal("iframe") || elem.IsLocal("img") || elem.IsLocal("object") ||
            elem.IsLocal("video"))
        {
            _psFirstInBlock = false;
            _psPreviousChar = "?";
            _psPreviousReplaced = false;
            _psPriorElem = null;
        }
        else if (!(elem.IsLocal("a") || elem.IsLocal("b") || elem.IsLocal("bdi") || elem.IsLocal("bdo") ||
                   elem.IsLocal("em") || elem.IsLocal("i") || elem.IsLocal("path") || elem.IsLocal("rb") ||
                   elem.IsLocal("rt") || elem.IsLocal("ruby") || elem.IsLocal("span") || elem.IsLocal("strong") ||
                   elem.IsLocal("sub") || elem.IsLocal("sup") || elem.IsLocal("u") ||
                   elem.Namespace is XmlNamespaces.MathMl or XmlNamespaces.Svg))
        {
            ResetPreformat();
        }

        if (!(elem.IsNs(XmlNamespaces.Svg, "svg") || elem.IsNs(XmlNamespaces.MathMl, "math")))
        {
            PreformatText(elem);
            foreach (var child in elem.Elements()) PreformatSpaces(child);
        }

        PreformatText(elem, doTail: true);
    }

    public void PreformatText(XNode elem, bool doTail = false)
    {
        var text = doTail ? elem.Tail : elem.Text;
        if (string.IsNullOrEmpty(text)) return;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var didReplace = false;
            if (ch == ' ' && (_psFirstInBlock || _psPreviousChar == " "))
            {
                if (_psPreviousChar == " " && !_psPreviousReplaced)
                {
                    if (i > 0) text = text[..(i - 1)] + "\u00a0" + text[i..];
                    else if (_psPriorIsTail && _psPriorElem?.Tail is not null)
                        _psPriorElem.Tail = _psPriorElem.Tail[..^1] + "\u00a0";
                    else if (_psPriorElem?.Text is not null)
                        _psPriorElem.Text = _psPriorElem.Text[..^1] + "\u00a0";
                }

                text = text[..i] + "\u00a0" + text[(i + 1)..];
                didReplace = true;
            }

            _psFirstInBlock = false;
            _psPreviousChar = ch.ToString();
            _psPreviousReplaced = didReplace;
        }

        if (doTail) elem.Tail = text;
        else elem.Text = text;
        _psPriorIsTail = doTail;
        _psPriorElem = elem;
    }

}
