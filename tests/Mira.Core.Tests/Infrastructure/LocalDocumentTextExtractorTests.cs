namespace Mira.Core.Tests.Infrastructure;

using System.Text;
using Mira.Infrastructure.Documents;
using Xunit;

public sealed class LocalDocumentTextExtractorTests
{
    [Theory]
    [InlineData("notes.txt", "text/plain", "Maxim likes local-first assistants.")]
    [InlineData("notes.md", "text/markdown", "# Notes\n\nMaxim likes local-first assistants.")]
    [InlineData("notes.markdown", null, "# Notes\n\nMaxim likes local-first assistants.")]
    public async Task ExtractTextAsync_reads_utf8_text_and_markdown_documents(string fileName, string? mimeType, string content)
    {
        var extractor = new LocalDocumentTextExtractor();

        var result = await extractor.ExtractTextAsync(
            fileName,
            mimeType,
            StreamFor(content),
            TestContext.Current.CancellationToken);

        Assert.True(extractor.Supports(fileName, mimeType));
        Assert.True(result.IsSupported);
        Assert.True(result.HasText);
        Assert.Equal(content, result.Text);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public async Task ExtractTextAsync_rejects_unsupported_documents_without_guessing_content()
    {
        var extractor = new LocalDocumentTextExtractor();

        var result = await extractor.ExtractTextAsync(
            "archive.zip",
            "application/zip",
            StreamFor("Maxim likes local-first assistants."),
            TestContext.Current.CancellationToken);

        Assert.False(extractor.Supports("archive.zip", "application/zip"));
        Assert.False(result.IsSupported);
        Assert.False(result.HasText);
        Assert.Empty(result.Text);
        Assert.Contains("unsupported", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("empty.txt", "text/plain")]
    [InlineData("empty.md", "text/markdown")]
    public async Task ExtractTextAsync_marks_empty_supported_documents_as_having_no_text(string fileName, string? mimeType)
    {
        var extractor = new LocalDocumentTextExtractor();

        var result = await extractor.ExtractTextAsync(
            fileName,
            mimeType,
            StreamFor(" \r\n\t "),
            TestContext.Current.CancellationToken);

        Assert.True(extractor.Supports(fileName, mimeType));
        Assert.True(result.IsSupported);
        Assert.False(result.HasText);
        Assert.Equal(" \r\n\t ", result.Text);
        Assert.Null(result.FailureMessage);
    }

    private static MemoryStream StreamFor(string content) => new(Encoding.UTF8.GetBytes(content));
}
