namespace CaptureSpike.Core.Markdown;

/// <summary>
/// One inline span of text with a single kind of formatting.
/// Spans are produced by <see cref="MarkdownParser"/> from a markdown
/// source string and rendered by <see cref="MarkdownView"/> as a
/// styled <see cref="Avalonia.Controls.TextBlock"/> run.
///
/// Storage stays the source string. The spans are a *view* of it.
/// </summary>
public enum MarkdownKind
{
    /// <summary>Plain text (no inline formatting).</summary>
    Text,
    /// <summary>**bold** — <see cref="Text"/> inside a span of kind Bold.</summary>
    Bold,
    /// <summary>*italic* — <see cref="Text"/> inside a span of kind Italic.</summary>
    Italic,
}

/// <summary>
/// Heading level or list marker for line-prefixed blocks. Headings
/// are line-prefixed (`#`, `##`, `###`); list items are `- ` (bullet)
/// or `N. ` (numbered) — parsed by the parser, stripped from the
/// span text, and rendered with a leading glyph.
/// </summary>
public enum MarkdownBlockKind
{
    Paragraph,
    Heading1,
    Heading2,
    Heading3,
    /// <summary>Bullet list item, prefixed with "- " in the source.</summary>
    BulletListItem,
    /// <summary>Numbered list item, prefixed with "N. " in the source.</summary>
    NumberedListItem,
}

/// <summary>
/// A single line-prefixed block: heading level + the inline content
/// (already parsed into spans, with the heading prefix stripped).
/// </summary>
public sealed record MarkdownBlock(MarkdownBlockKind Kind, IReadOnlyList<MarkdownSpan> Spans);

/// <summary>
/// One inline span: text + the formatting applied to it.
/// </summary>
public sealed record MarkdownSpan(MarkdownKind Kind, string Text)
{
    public bool IsPlain => Kind == MarkdownKind.Text && string.IsNullOrEmpty(Text) is false;
}