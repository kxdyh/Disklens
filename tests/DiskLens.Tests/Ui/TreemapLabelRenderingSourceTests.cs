namespace DiskLens.Tests.Ui;

public sealed class TreemapLabelRenderingSourceTests
{
    [Fact]
    public void Treemap_rendering_should_use_compact_label_layout_and_keep_full_hover_details()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "TreemapControl.cs");
        var source = File.ReadAllText(controlPath);

        Assert.Contains("var labelLayout = TreemapLabelPlanner.Plan(labelBounds.Width, labelBounds.Height)", source, StringComparison.Ordinal);
        Assert.Contains("if (labelLayout is null)", source, StringComparison.Ordinal);
        Assert.Contains("labelLayout.Padding", source, StringComparison.Ordinal);
        Assert.Contains("layout.FontSize", source, StringComparison.Ordinal);
        Assert.Contains("layout.MaxLineCount", source, StringComparison.Ordinal);
        Assert.Contains("CreateFormattedText(FormatBytes(node.SizeBytes)", source, StringComparison.Ordinal);
        Assert.Contains("name.Height + lineGap + size.Height <= bounds.Height", source, StringComparison.Ordinal);
        Assert.Contains("bounds.Width - size.WidthIncludingTrailingWhitespace - 6", source, StringComparison.Ordinal);
        Assert.Contains("drawingContext.PushClip(new RectangleGeometry(inkBounds))", source, StringComparison.Ordinal);
        Assert.Contains("HasVisibleChildren(rectangle.Node.Path)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateLabelText(", source, StringComparison.Ordinal);
        Assert.Contains("Trimming = TextTrimming.CharacterEllipsis", source, StringComparison.Ordinal);

        // Tooltip rendering reads the control's analysis visibility, so it is an instance method.
        var tooltipStart = source.IndexOf("object? CreateNodeToolTip(FileSystemNode? node)", StringComparison.Ordinal);
        Assert.True(tooltipStart >= 0);
        var tooltipEnd = source.IndexOf("private static TextBlock CreateToolTipTextBlock", tooltipStart, StringComparison.Ordinal);
        Assert.True(tooltipEnd > tooltipStart);
        var tooltip = source[tooltipStart..tooltipEnd];
        Assert.Contains("node.Name", tooltip, StringComparison.Ordinal);
        Assert.Contains("node.Path", tooltip, StringComparison.Ordinal);
        var tooltipText = source[tooltipEnd..source.IndexOf("private ", tooltipEnd + 1, StringComparison.Ordinal)];
        Assert.Contains("TextWrapping = TextWrapping.Wrap", tooltipText, StringComparison.Ordinal);
        Assert.Contains("TextTrimming = TextTrimming.None", tooltipText, StringComparison.Ordinal);
    }
}
