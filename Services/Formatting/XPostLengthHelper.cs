using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTweetRss.Services;

internal static partial class XPostLengthHelper
{
    private const int TransformedUrlLength = 23;

    [GeneratedRegex(@"https?://[^\s]+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    public static int GetWeightedLength(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var normalized = text.Normalize(NormalizationForm.FormC);
        var total = 0;
        var currentIndex = 0;

        foreach (Match match in UrlPattern().Matches(normalized))
        {
            if (match.Index > currentIndex)
            {
                total += GetNonUrlWeightedLength(normalized[currentIndex..match.Index]);
            }

            total += TransformedUrlLength;
            currentIndex = match.Index + match.Length;
        }

        if (currentIndex < normalized.Length)
        {
            total += GetNonUrlWeightedLength(normalized[currentIndex..]);
        }

        return total;
    }

    public static bool FitsWithinLimit(string? text, int limit)
        => GetWeightedLength(text) <= limit;

    public static string TruncateToWeightedLength(string? text, int limit, string ellipsis = "...")
    {
        if (string.IsNullOrWhiteSpace(text) || limit <= 0)
        {
            return string.Empty;
        }

        var normalized = text.Normalize(NormalizationForm.FormC).Trim();
        if (FitsWithinLimit(normalized, limit))
        {
            return normalized;
        }

        var ellipsisWeight = GetWeightedLength(ellipsis);
        if (ellipsisWeight >= limit)
        {
            return string.Empty;
        }

        var targetContentWeight = limit - ellipsisWeight;
        var builder = new StringBuilder();
        var currentIndex = 0;
        var accumulatedWeight = 0;

        foreach (Match match in UrlPattern().Matches(normalized))
        {
            if (match.Index > currentIndex)
            {
                AppendFittingTextElements(normalized[currentIndex..match.Index], targetContentWeight, builder, ref accumulatedWeight);
                if (accumulatedWeight >= targetContentWeight)
                {
                    return builder.ToString().TrimEnd() + ellipsis;
                }
            }

            if (accumulatedWeight + TransformedUrlLength > targetContentWeight)
            {
                return builder.ToString().TrimEnd() + ellipsis;
            }

            builder.Append(match.Value);
            accumulatedWeight += TransformedUrlLength;
            currentIndex = match.Index + match.Length;
        }

        if (currentIndex < normalized.Length)
        {
            AppendFittingTextElements(normalized[currentIndex..], targetContentWeight, builder, ref accumulatedWeight);
        }

        return builder.ToString().TrimEnd() + ellipsis;
    }

    private static void AppendFittingTextElements(string text, int targetWeight, StringBuilder builder, ref int accumulatedWeight)
    {
        if (string.IsNullOrEmpty(text) || accumulatedWeight >= targetWeight)
        {
            return;
        }

        var enumerator = StringInfo.GetTextElementEnumerator(text.Normalize(NormalizationForm.FormC));
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var weight = GetTextElementWeight(element);
            if (accumulatedWeight + weight > targetWeight)
            {
                break;
            }

            builder.Append(element);
            accumulatedWeight += weight;
        }
    }

    private static int GetNonUrlWeightedLength(string text)
    {
        var length = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text.Normalize(NormalizationForm.FormC));
        while (enumerator.MoveNext())
        {
            length += GetTextElementWeight(enumerator.GetTextElement());
        }

        return length;
    }

    private static int GetTextElementWeight(string textElement)
    {
        if (ContainsEmoji(textElement))
        {
            return 2;
        }

        var weight = 0;
        foreach (var rune in textElement.EnumerateRunes())
        {
            weight += IsSingleWeightRune(rune.Value) ? 1 : 2;
        }

        return weight;
    }

    private static bool ContainsEmoji(string textElement)
    {
        foreach (var rune in textElement.EnumerateRunes())
        {
            var value = rune.Value;

            if (value is 0x200D or 0xFE0F)
            {
                return true;
            }

            if (value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                return true;
            }

            if (value is >= 0x1F3FB and <= 0x1F3FF)
            {
                return true;
            }

            if (IsEmojiRune(value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEmojiRune(int value)
        => value is >= 0x231A and <= 0x231B
            or >= 0x23E9 and <= 0x23EC
            or >= 0x23F0 and <= 0x23FA
            or >= 0x24C2 and <= 0x24C2
            or >= 0x25AA and <= 0x25AB
            or >= 0x25B6 and <= 0x25B6
            or >= 0x25C0 and <= 0x25C0
            or >= 0x25FB and <= 0x25FE
            or >= 0x2600 and <= 0x27BF
            or >= 0x2934 and <= 0x2935
            or >= 0x2B05 and <= 0x2B55
            or >= 0x3030 and <= 0x303D
            or >= 0x3297 and <= 0x3299
            or >= 0x1F000 and <= 0x1FAFF;

    private static bool IsSingleWeightRune(int value)
        => value is >= 0x0000 and <= 0x10FF
            or >= 0x2000 and <= 0x200D
            or >= 0x2010 and <= 0x201F
            or >= 0x2032 and <= 0x2037;
}