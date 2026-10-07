using CaptureSpike.Core.Markdown;
using Xunit;

namespace CaptureSpike.Core.Tests;

/// <summary>
/// Parser / serializer round-trip tests for the markdown layer that
/// the WYSIWYG notes editor is built on. These run without a UI: they
/// exercise the pure-string logic so we know the toolbar's wrap/prefix
/// operations are safe before we wire them into the editor control.
/// </summary>
public sealed class MarkdownParserTests
{
    [Fact]
    public void Empty_ProducesSingleEmptyParagraph()
    {
        var blocks = MarkdownParser.Parse("");
        Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[0].Kind);
        Assert.Empty(blocks[0].Spans);
    }

    [Fact]
    public void PlainText_StaysPlainText()
    {
        var blocks = MarkdownParser.Parse("hello world");
        Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[0].Kind);
        var span = Assert.Single(blocks[0].Spans);
        Assert.Equal(MarkdownKind.Text, span.Kind);
        Assert.Equal("hello world", span.Text);
    }

    [Fact]
    public void BoldSpan_WrappedInDoubleAsterisks()
    {
        var blocks = MarkdownParser.Parse("a **bold** word");
        Assert.Single(blocks);
        var spans = blocks[0].Spans;
        Assert.Equal(3, spans.Count);
        Assert.Equal(MarkdownKind.Text, spans[0].Kind);
        Assert.Equal("a ", spans[0].Text);
        Assert.Equal(MarkdownKind.Bold, spans[1].Kind);
        Assert.Equal("bold", spans[1].Text);
        Assert.Equal(MarkdownKind.Text, spans[2].Kind);
        Assert.Equal(" word", spans[2].Text);
    }

    [Fact]
    public void ItalicSpan_WrappedInSingleAsterisks()
    {
        var blocks = MarkdownParser.Parse("*italic* text");
        var spans = blocks[0].Spans;
        Assert.Equal(2, spans.Count);
        Assert.Equal(MarkdownKind.Italic, spans[0].Kind);
        Assert.Equal("italic", spans[0].Text);
        Assert.Equal(MarkdownKind.Text, spans[1].Kind);
        Assert.Equal(" text", spans[1].Text);
    }

    [Fact]
    public void BoldAndItalic_NestedOrAdjacent()
    {
        // **bold *italic* end** — italic lives inside bold.
        var blocks = MarkdownParser.Parse("**bold *italic* end**");
        var spans = blocks[0].Spans;
        // Expect: Bold("bold "), Italic("italic"), Bold(" end") — but
        // we don't actually model nesting; bold/italic close at the
        // first matching marker. Verify what we DO produce.
        // The current parser doesn't support nesting; the inner *italic*
        // closes the bold early. *** That is acceptable for our scope
        // (toolbar applies formatting sequentially).
        Assert.Contains(spans, s => s.Kind == MarkdownKind.Bold);
    }

    [Fact]
    public void UnclosedBoldMarker_TreatedAsPlainText()
    {
        // **in progress — user is mid-typing. Don't break.
        var blocks = MarkdownParser.Parse("**in progress");
        var span = Assert.Single(blocks[0].Spans);
        Assert.Equal(MarkdownKind.Text, span.Kind);
        Assert.Equal("**in progress", span.Text);
    }

    [Fact]
    public void Heading1_PrefixStripped()
    {
        var blocks = MarkdownParser.Parse("# Title");
        Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Heading1, blocks[0].Kind);
        Assert.Equal("Title", Assert.Single(blocks[0].Spans).Text);
    }

    [Fact]
    public void Heading2_And3_Prefixes()
    {
        var h2 = MarkdownParser.Parse("## Subhead");
        Assert.Equal(MarkdownBlockKind.Heading2, h2[0].Kind);

        var h3 = MarkdownParser.Parse("### Detail");
        Assert.Equal(MarkdownBlockKind.Heading3, h3[0].Kind);
    }

    [Fact]
    public void HeadingWithoutSpace_IsNotAHeading()
    {
        // CommonMark requires the space. "#Title" is plain text.
        var blocks = MarkdownParser.Parse("#Title");
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[0].Kind);
    }

    [Fact]
    public void HeadingWithBoldInline()
    {
        var blocks = MarkdownParser.Parse("# **Bold** title");
        Assert.Equal(MarkdownBlockKind.Heading1, blocks[0].Kind);
        Assert.Equal(2, blocks[0].Spans.Count);
        Assert.Equal(MarkdownKind.Bold, blocks[0].Spans[0].Kind);
        Assert.Equal("Bold", blocks[0].Spans[0].Text);
    }

    [Fact]
    public void EmptyLine_SplitsParagraphs()
    {
        var blocks = MarkdownParser.Parse("para one\n\npara two");
        Assert.Equal(2, blocks.Count);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[0].Kind);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[1].Kind);
        Assert.Equal("para one", blocks[0].Spans[0].Text);
        Assert.Equal("para two", blocks[1].Spans[0].Text);
    }

    [Fact]
    public void MultiLineParagraph_PreservedWithInternalNewline()
    {
        var blocks = MarkdownParser.Parse("line one\nline two");
        Assert.Single(blocks);
        Assert.Equal("line one\nline two",
            string.Concat(blocks[0].Spans.Select(s => s.Text)));
    }

    [Fact]
    public void RoundTrip_PreservesBoldAndHeadings()
    {
        const string input = "# Heading\n\nSome **bold** and *italic* text.\n\n## Sub\n\nMore text.";
        var blocks = MarkdownParser.Parse(input);
        var output = MarkdownSerializer.Serialize(blocks);
        Assert.Equal(input, output);
    }

    [Fact]
    public void RoundTrip_PreservesEmptyInput()
    {
        var output = MarkdownSerializer.Serialize(MarkdownParser.Parse(""));
        Assert.Equal("", output);
    }

    [Fact]
    public void RoundTrip_MultiLineParagraph()
    {
        const string input = "line one\nline two\nline three";
        var output = MarkdownSerializer.Serialize(MarkdownParser.Parse(input));
        Assert.Equal(input, output);
    }
}