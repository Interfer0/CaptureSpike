namespace CaptureSpike.Core.Markdown;

/// <summary>
/// Parses a markdown source string into a list of <see cref="MarkdownBlock"/>s.
/// Pure function: same input always produces same output. The parser is
/// intentionally small — it handles inline bold/italic and headings
/// (the only formatting the toolbar exposes) and delegates everything
/// else to plain text. Round-trip with <see cref="MarkdownSerializer"/>
/// is verified by unit tests.
///
/// Why a custom parser instead of Markdig:
///   * Markdig parses to HTML; we need a structured tree of spans.
///   * The toolbar manipulates the source string directly; the parser
///     just needs to understand what those manipulations produce.
///   * Adding Markdig here would be a hidden dependency on a 1MB+ DLL
///     for ~50 lines of regex-ish logic.
/// </summary>
public static class MarkdownParser
{
    /// <summary>Parse the markdown source into a list of blocks.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string source)
    {
        if (string.IsNullOrEmpty(source))
            return [new MarkdownBlock(MarkdownBlockKind.Paragraph, [])];

        var lines = source.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<MarkdownBlock>();
        var paragraphSpans = new List<MarkdownSpan>();

        void FlushParagraph()
        {
            if (paragraphSpans.Count == 0) return;
            // Collapse adjacent text spans so the renderer doesn't get
            // ten "Text,Text,Text" entries for one line.
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph,
                CollapseAdjacentText(paragraphSpans)));
            paragraphSpans.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine;
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            // Heading: line begins with "# ", "## ", or "### ".
            // The parser is intentionally permissive — "##foo" (no
            // space) is NOT a heading, only the space-separated form.
            if (TryParseHeading(line, out var headingKind, out var headingContent))
            {
                FlushParagraph();
                var inline = ParseInline(headingContent);
                blocks.Add(new MarkdownBlock(headingKind, inline));
                continue;
            }

            // Bullet list item: "- " at the start of the line.
            if (line.StartsWith("- ") && IsAtStart(line, 2))
            {
                FlushParagraph();
                var inline = ParseInline(line[2..]);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.BulletListItem, inline));
                continue;
            }

            // Numbered list item: "N. " at the start of the line, where
            // N is one or more digits. The number itself isn't part of
            // the visible text — the renderer draws the bullet.
            if (TryParseNumberedList(line, out var numberedContent))
            {
                FlushParagraph();
                var inline = ParseInline(numberedContent);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.NumberedListItem, inline));
                continue;
            }

            // Mid-document line — accumulate into the current paragraph.
            // If we already have inline spans and this line is non-empty,
            // add a literal "\n" so a multi-line paragraph renders with
            // a line break between the two lines.
            if (paragraphSpans.Count > 0)
                paragraphSpans.Add(new MarkdownSpan(MarkdownKind.Text, "\n"));
            paragraphSpans.AddRange(ParseInline(line));
        }
        FlushParagraph();

        // Trailing empty input → an empty paragraph so the editor has
        // something to render. (Helps the cursor positioning.)
        if (blocks.Count == 0)
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, []));

        return blocks;
    }

    private static bool TryParseHeading(string line, out MarkdownBlockKind kind, out string content)
    {
        // Match "# ", "## ", "### " at the beginning. The space is required
        // (matches CommonMark); "#Title" is plain text.
        if (line.StartsWith("# ") && IsAtStart(line, 2))
        {
            kind = MarkdownBlockKind.Heading1;
            content = line[2..];
            return true;
        }
        if (line.StartsWith("## ") && IsAtStart(line, 3))
        {
            kind = MarkdownBlockKind.Heading2;
            content = line[3..];
            return true;
        }
        if (line.StartsWith("### ") && IsAtStart(line, 4))
        {
            kind = MarkdownBlockKind.Heading3;
            content = line[4..];
            return true;
        }
        kind = default;
        content = "";
        return false;
    }

    private static bool IsAtStart(string s, int prefixLen) =>
        // A heading prefix is only valid if it's at offset 0. Caller
        // already verified StartsWith; this exists to make the call
        // site explicit.
        prefixLen > 0 && prefixLen <= s.Length;

    /// <summary>
    /// Match "1. ", "2. ", etc. — one or more digits followed by ". "
    /// at the start of the line. Returns the content after the prefix.
    /// </summary>
    private static bool TryParseNumberedList(string line, out string content)
    {
        content = "";
        var i = 0;
        while (i < line.Length && char.IsDigit(line[i])) i++;
        if (i == 0) return false;       // no leading digits
        if (i + 1 >= line.Length) return false; // need ". " after digits
        if (line[i] != '.' || line[i + 1] != ' ') return false;
        content = line[(i + 2)..];
        return true;
    }

    /// <summary>
    /// Parse inline formatting in a single line: **bold** and *italic*.
    /// Unclosed markers are treated as plain text (so the user can
    /// type "**in progress" without breaking the parser).
    /// </summary>
    private static IReadOnlyList<MarkdownSpan> ParseInline(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var spans = new List<MarkdownSpan>();
        var i = 0;
        var bufStart = 0;

        while (i < text.Length)
        {
            // **bold** — two consecutive asterisks.
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = FindMarkerEnd(text, i + 2, "**");
                if (end > i + 2)
                {
                    if (bufStart < i)
                        spans.Add(new MarkdownSpan(MarkdownKind.Text, text[bufStart..i]));
                    spans.Add(new MarkdownSpan(MarkdownKind.Bold, text[(i + 2)..end]));
                    i = end + 2;
                    bufStart = i;
                    continue;
                }
                // No closing ** — fall through and treat as plain text.
            }
            // *italic* — single asterisk (but not ** which we already
            // handled above; this branch only fires for orphan *).
            else if (text[i] == '*')
            {
                var end = FindMarkerEnd(text, i + 1, "*");
                if (end > i + 1)
                {
                    if (bufStart < i)
                        spans.Add(new MarkdownSpan(MarkdownKind.Text, text[bufStart..i]));
                    spans.Add(new MarkdownSpan(MarkdownKind.Italic, text[(i + 1)..end]));
                    i = end + 1;
                    bufStart = i;
                    continue;
                }
            }

            i++;
        }
        if (bufStart < text.Length)
            spans.Add(new MarkdownSpan(MarkdownKind.Text, text[bufStart..]));

        return CollapseAdjacentText(spans);
    }

    /// <summary>
    /// Find the next occurrence of <paramref name="marker"/> starting at
    /// <paramref name="from"/>. Returns -1 if not found. The marker is
    /// treated as a literal sequence; asterisks inside the run are
    /// allowed (so "**a*b**" finds the closing ** at the end).
    /// </summary>
    private static int FindMarkerEnd(string text, int from, string marker)
    {
        var i = from;
        while (i <= text.Length - marker.Length)
        {
            var idx = text.IndexOf(marker, i, StringComparison.Ordinal);
            if (idx < 0) return -1;
            return idx;
        }
        return -1;
    }

    private static IReadOnlyList<MarkdownSpan> CollapseAdjacentText(List<MarkdownSpan> input)
    {
        var result = new List<MarkdownSpan>(input.Count);
        foreach (var s in input)
        {
            if (s.Kind == MarkdownKind.Text && result.Count > 0 &&
                result[^1].Kind == MarkdownKind.Text)
            {
                result[^1] = result[^1] with { Text = result[^1].Text + s.Text };
            }
            else
            {
                result.Add(s);
            }
        }
        return result;
    }
}