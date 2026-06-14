namespace AutoTweetRss.Services;

internal static class ReleaseNoteStructureHelper
{
    public static bool IsStructuralReleaseSectionHeading(string text)
    {
        var normalized = text.Trim().TrimEnd(':');
        return normalized.Equals("Added", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Changed", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Fixed", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Security", StringComparison.OrdinalIgnoreCase);
    }
}
