using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using CaptureSpike.Core.Markdown;

namespace CaptureSpike.App;

/// <summary>
/// Renders a list of <see cref="MarkdownBlock"/>s as styled Avalonia
/// TextBlocks. Markup characters (**, *, #) are not emitted as visible
/// Run text; the visible runs are bold / italic / heading-font sized.
///
/// This is the "WYSIWYG" half of the hidden-markup editor — it shows
/// the formatted text. The companion MarkdownEditor (turn 3) handles
/// the caret and selection; this control is purely visual.
///
/// Storage: the underlying markdown source stays in Scene.Notes.
/// The Blocks list is recomputed from the source via MarkdownParser
/// on every change; the renderer doesn't store state.
/// </summary>
public sealed class MarkdownView : Control
{
    /// <summary>Defines the <see cref="Blocks"/> property.</summary>
    public static readonly StyledProperty<IReadOnlyList<MarkdownBlock>?> BlocksProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MarkdownBlock>?>(nameof(Blocks));

    /// <summary>The blocks to render. Set this and the view rebuilds.</summary>
    public IReadOnlyList<MarkdownBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <summary>Default font size for paragraph/list bodies.</summary>
    public static readonly StyledProperty<double> BodyFontSizeProperty =
        AvaloniaProperty.Register<MarkdownView, double>(nameof(BodyFontSize), defaultValue: 15);

    public double BodyFontSize
    {
        get => GetValue(BodyFontSizeProperty);
        set => SetValue(BodyFontSizeProperty, value);
    }

    /// <summary>Heading1 font size (default 24 — same scale as the
    /// existing app's font bump for headers).</summary>
    public static readonly StyledProperty<double> Heading1FontSizeProperty =
        AvaloniaProperty.Register<MarkdownView, double>(nameof(Heading1FontSize), defaultValue: 24);

    public double Heading1FontSize
    {
        get => GetValue(Heading1FontSizeProperty);
        set => SetValue(Heading1FontSizeProperty, value);
    }

    public static readonly StyledProperty<double> Heading2FontSizeProperty =
        AvaloniaProperty.Register<MarkdownView, double>(nameof(Heading2FontSize), defaultValue: 20);

    public double Heading2FontSize
    {
        get => GetValue(Heading2FontSizeProperty);
        set => SetValue(Heading2FontSizeProperty, value);
    }

    public static readonly StyledProperty<double> Heading3FontSizeProperty =
        AvaloniaProperty.Register<MarkdownView, double>(nameof(Heading3FontSize), defaultValue: 17);

    public double Heading3FontSize
    {
        get => GetValue(Heading3FontSizeProperty);
        set => SetValue(Heading3FontSizeProperty, value);
    }

    private Panel? _root;

    public MarkdownView()
    {
        // Rebuild on any of the styled properties changing. The
        // AvaloniaObject base class fires PropertyChanged for all
        // StyledProperty assignments; we only care about ours.
        PropertyChanged += OnAnyPropertyChanged;
    }

    private void OnAnyPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BlocksProperty ||
            e.Property == BodyFontSizeProperty ||
            e.Property == Heading1FontSizeProperty ||
            e.Property == Heading2FontSizeProperty ||
            e.Property == Heading3FontSizeProperty)
        {
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureRootPanel();
        Rebuild();
    }

    /// <summary>
    /// Called by the framework when the control is removed from a
    /// window. We don't keep state of our own, but the VisualChildren
    /// list will be cleaned up by Avalonia itself.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Clear references; Avalonia drops VisualChildren automatically.
        _root = null;
    }

    private void EnsureRootPanel()
    {
        if (_root is not null) return;
        _root = new StackPanel { Spacing = 4 };
        VisualChildren.Add(_root);
        LogicalChildren.Add(_root);
    }

    private void Rebuild()
    {
        if (_root is null) return;
        _root.Children.Clear();

        var blocks = Blocks;
        if (blocks is null || blocks.Count == 0)
        {
            // No content — show an empty placeholder paragraph so the
            // control still has a measurable height (helps the editor
            // size its hidden TextBox overlay).
            _root.Children.Add(MakeParagraph([]));
            return;
        }

        // Track consecutive list items so we can number them 1, 2, 3…
        // regardless of the digit the user typed in the source.
        var inList = false;
        var listCounter = 0;

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading1:
                case MarkdownBlockKind.Heading2:
                case MarkdownBlockKind.Heading3:
                    inList = false;
                    _root.Children.Add(MakeHeading(block));
                    break;

                case MarkdownBlockKind.BulletListItem:
                    if (!inList) listCounter = 0;
                    inList = true;
                    listCounter++;
                    _root.Children.Add(MakeListItem(block.Spans, "•  ", listCounter, isBullet: true));
                    break;

                case MarkdownBlockKind.NumberedListItem:
                    if (!inList) listCounter = 0;
                    inList = true;
                    listCounter++;
                    _root.Children.Add(MakeListItem(block.Spans, "", listCounter, isBullet: false));
                    break;

                case MarkdownBlockKind.Paragraph:
                default:
                    inList = false;
                    _root.Children.Add(MakeParagraph(block.Spans));
                    break;
            }
        }
    }

    private TextBlock MakeHeading(MarkdownBlock block) =>
        block.Kind switch
        {
            MarkdownBlockKind.Heading1 => MakeStyledText(block.Spans,
                FontWeight.Bold, Heading1FontSize, margin: new Thickness(0, 8, 0, 2)),
            MarkdownBlockKind.Heading2 => MakeStyledText(block.Spans,
                FontWeight.SemiBold, Heading2FontSize, margin: new Thickness(0, 6, 0, 2)),
            MarkdownBlockKind.Heading3 => MakeStyledText(block.Spans,
                FontWeight.SemiBold, Heading3FontSize, margin: new Thickness(0, 4, 0, 2)),
            _ => MakeParagraph(block.Spans),
        };

    private TextBlock MakeListItem(IReadOnlyList<MarkdownSpan> spans, string bullet, int counter, bool isBullet)
    {
        // Bullet list: "•  text". Numbered list: "N. text" (right-padded
        // to two characters so single digits line up with double digits).
        var prefix = isBullet ? bullet : $"{counter,2}. ";
        var item = new TextBlock
        {
            // Indent list items so the marker sits in the gutter.
            Margin = new Thickness(16, 0, 0, 2),
            TextWrapping = TextWrapping.Wrap,
        };
        // Inlines is initialized by TextBlock's constructor; the analyzer
        // doesn't track that so we !-suppress.
        item.Inlines!.Add(new Run(prefix));
        if (spans is not null)
            AddStyledSpans(item.Inlines, spans);
        return item;
    }

    private TextBlock MakeParagraph(IReadOnlyList<MarkdownSpan> spans) =>
        MakeStyledText(spans, FontWeight.Normal, BodyFontSize, margin: new Thickness(0));

    private TextBlock MakeStyledText(IReadOnlyList<MarkdownSpan> spans, FontWeight weight,
        double size, Thickness margin)
    {
        var tb = new TextBlock
        {
            FontWeight = weight,
            FontSize = size,
            Margin = margin,
            TextWrapping = TextWrapping.Wrap,
        };
        if (spans is not null)
            AddStyledSpans(tb.Inlines!, spans);
        return tb;
    }

    /// <summary>
    /// Convert spans to Run elements. Markup characters are NOT emitted
    /// as visible text — only the span's Text appears, with the
    /// appropriate FontWeight / FontStyle applied.
    /// </summary>
    private static void AddStyledSpans(InlineCollection inlines, IReadOnlyList<MarkdownSpan> spans)
    {
        foreach (var span in spans)
        {
            switch (span.Kind)
            {
                case MarkdownKind.Bold:
                    inlines.Add(new Run(span.Text) { FontWeight = FontWeight.Bold });
                    break;
                case MarkdownKind.Italic:
                    inlines.Add(new Run(span.Text) { FontStyle = FontStyle.Italic });
                    break;
                case MarkdownKind.Text:
                default:
                    inlines.Add(new Run(span.Text));
                    break;
            }
        }
    }
}