namespace Mira.Core.Interfaces;

public interface IDocumentTextExtractor
{
    bool Supports(string fileName, string? mimeType);

    Task<DocumentTextExtractionResult> ExtractTextAsync(string fileName, string? mimeType, Stream content, CancellationToken cancellationToken = default);
}

public sealed record DocumentTextExtractionResult(bool IsSupported, string Text, string? FailureMessage)
{
    public bool HasText => IsSupported && !string.IsNullOrWhiteSpace(Text);

    public static DocumentTextExtractionResult Success(string text) => new(true, text, null);

    public static DocumentTextExtractionResult Failure(string message) => new(false, string.Empty, message);
}
