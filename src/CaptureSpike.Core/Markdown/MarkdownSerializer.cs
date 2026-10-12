// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2024-2026 Daniel Bigelow

namespace CaptureSpike.Core.Markdown;

/// <summary>
/// Inverse of <see cref="MarkdownParser"/>: turn a list of blocks
/// back into markdown source. Used by the editor when the user types
/// or when the toolbar wraps a selection — the parser produces spans
/// from the current text, the toolbar edits the spans, the serializer
/// writes the new source string, and the editor parses again.
///
/// The serializer is lossless on the round-trip cases the toolbar
/// produces. It preserves line-prefix markers (headings) and inline
/// markers (bold/italic) exactly as they came in.
///
/// Blocks are separated by a blank line ("\n\n"), matching CommonMark
/// paragraph boundaries. The parser accepts any whitespace but the
/// canonical form is two consecutive newlines.
/// </summary>
public static class MarkdownSerializer
{
    /// <summary>Serialize blocks back to markdown source.</summary>
    public static string Serialize(IEnumerable<MarkdownBlock> blocks)
    {
        var sb = new System.Text.StringBuilder();
        var first = true;
        foreach (var block in blocks)
        {
            if (!first)
                sb.Append("\n\n");
            first = false;
            SerializeBlock(sb, block);
        }
        return sb.ToString();
    }

    private static void SerializeBlock(System.Text.StringBuilder sb, MarkdownBlock block)
    {
        switch (block.Kind)
        {
            case MarkdownBlockKind.Heading1: sb.Append("# "); break;
            case MarkdownBlockKind.Heading2: sb.Append("## "); break;
            case MarkdownBlockKind.Heading3: sb.Append("### "); break;
            case MarkdownBlockKind.BulletListItem: sb.Append("- "); break;
            case MarkdownBlockKind.NumberedListItem: sb.Append("1. "); break;
            case MarkdownBlockKind.Paragraph: break;
        }
        SerializeInline(sb, block.Spans);
    }

    private static void SerializeInline(System.Text.StringBuilder sb, IReadOnlyList<MarkdownSpan> spans)
    {
        foreach (var span in spans)
        {
            // Text spans may contain newlines (multi-line paragraph
            // segments); emit them as-is so the line break is preserved.
            if (span.Kind == MarkdownKind.Text)
            {
                sb.Append(span.Text);
            }
            else if (span.Kind == MarkdownKind.Bold)
            {
                sb.Append("**").Append(span.Text).Append("**");
            }
            else if (span.Kind == MarkdownKind.Italic)
            {
                sb.Append("*").Append(span.Text).Append("*");
            }
        }
    }
}