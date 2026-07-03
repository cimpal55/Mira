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
