namespace Mira.Infrastructure.Telegram;

using System.Text;

public static class TelegramMessageFormatter
{
    public static string ToTelegramHtml(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (IsUnorderedListMarker(text, index, out var markerLength))
            {
                builder.Append("• ");
                index += markerLength;
                continue;
            }

            if (IsBoldDelimiter(text, index))
            {
                var closingIndex = FindClosingBoldDelimiter(text, index + 2);
                if (closingIndex > index + 2)
                {
                    builder.Append("<b>");
                    AppendHtmlEscaped(builder, text.AsSpan(index + 2, closingIndex - index - 2));
                    builder.Append("</b>");
                    index = closingIndex + 2;
                    continue;
                }
            }

            AppendHtmlEscaped(builder, text[index]);
            index++;
        }

        return builder.ToString();
    }

    private static bool IsUnorderedListMarker(string text, int index, out int markerLength)
    {
        markerLength = 0;

        if (!IsLineStart(text, index) || index >= text.Length)
        {
            return false;
        }

        var marker = text[index];
        if (marker is not ('*' or '-'))
        {
            return false;
        }

        var whitespaceIndex = index + 1;
        if (whitespaceIndex >= text.Length || !IsMarkerWhitespace(text[whitespaceIndex]))
        {
            return false;
        }

        do
        {
            whitespaceIndex++;
        }
        while (whitespaceIndex < text.Length && IsMarkerWhitespace(text[whitespaceIndex]));

        markerLength = whitespaceIndex - index;
        return true;
    }

    private static bool IsLineStart(string text, int index) =>
        index == 0 || text[index - 1] == '\n';

    private static bool IsMarkerWhitespace(char character) =>
        character is ' ' or '\t';

    private static int FindClosingBoldDelimiter(string text, int startIndex)
    {
        for (var index = startIndex; index < text.Length - 1; index++)
        {
            if (IsBoldDelimiter(text, index))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsBoldDelimiter(string text, int index) =>
        index >= 0 && index < text.Length - 1 && text[index] == '*' && text[index + 1] == '*';

    private static void AppendHtmlEscaped(StringBuilder builder, ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            AppendHtmlEscaped(builder, character);
        }
    }

    private static void AppendHtmlEscaped(StringBuilder builder, char character)
    {
        _ = character switch
        {
            '&' => builder.Append("&amp;"),
            '<' => builder.Append("&lt;"),
            '>' => builder.Append("&gt;"),
            _ => builder.Append(character)
        };
    }
}
