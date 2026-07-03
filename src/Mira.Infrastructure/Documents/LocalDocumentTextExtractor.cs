namespace Mira.Infrastructure.Documents;

using System.Text;
using Mira.Core.Interfaces;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

public sealed class LocalDocumentTextExtractor : IDocumentTextExtractor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt",
        ".text",
        ".md",
        ".markdown"
    };

    public bool Supports(string fileName, string? mimeType)
    {
        var extension = Path.GetExtension(fileName);
        return IsPdf(extension, mimeType) || IsPlainText(extension, mimeType);
    }

    public async Task<DocumentTextExtractionResult> ExtractTextAsync(string fileName, string? mimeType, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var safeName = SafeDocumentName(fileName);
        var extension = Path.GetExtension(safeName);
        if (IsPdf(extension, mimeType))
        {
            return ExtractPdfText(safeName, content, cancellationToken);
        }

        if (IsPlainText(extension, mimeType))
        {
            return await ExtractUtf8TextAsync(safeName, content, cancellationToken).ConfigureAwait(false);
        }

        return DocumentTextExtractionResult.Failure($"Unsupported document type for \"{safeName}\". Send a PDF, .txt, or Markdown file.");
    }

    private static DocumentTextExtractionResult ExtractPdfText(string fileName, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            ResetIfSeekable(content);
            cancellationToken.ThrowIfCancellationRequested();

            using var document = PdfDocument.Open(content);
            var builder = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageText = page.Text;
                if (string.IsNullOrWhiteSpace(pageText))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine().AppendLine();
                }

                builder.Append(pageText.Trim());
            }

            return DocumentTextExtractionResult.Success(builder.ToString());
        }
        catch (PdfDocumentEncryptedException)
        {
            return DocumentTextExtractionResult.Failure($"I couldn't import \"{fileName}\" because the PDF is encrypted.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DocumentTextExtractionResult.Failure($"I couldn't extract text from \"{fileName}\" locally: {ex.Message}");
        }
    }

    private static async Task<DocumentTextExtractionResult> ExtractUtf8TextAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        try
        {
            ResetIfSeekable(content);
            using var reader = new StreamReader(content, StrictUtf8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return DocumentTextExtractionResult.Success(text);
        }
        catch (DecoderFallbackException)
        {
            return DocumentTextExtractionResult.Failure($"I couldn't import \"{fileName}\" because it is not valid UTF-8 text.");
        }
    }

    private static bool IsPdf(string extension, string? mimeType)
    {
        return extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlainText(string extension, string? mimeType)
    {
        return TextExtensions.Contains(extension)
            || string.Equals(mimeType, "text/plain", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mimeType, "text/markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static void ResetIfSeekable(Stream content)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }
    }

    private static string SafeDocumentName(string fileName)
    {
        return string.IsNullOrWhiteSpace(fileName) ? "document" : Path.GetFileName(fileName.Trim());
    }
}
