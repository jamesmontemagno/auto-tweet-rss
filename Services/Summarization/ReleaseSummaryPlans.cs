namespace AutoTweetRss.Services;

/// <summary>
/// Represents an AI-generated ranked list of features for thread assembly.
/// </summary>
public class ThreadPlan
{
    public int TotalCount { get; set; }
    public List<string> Items { get; set; } = [];
}

/// <summary>
/// Represents an AI-generated categorized plan for Premium X mega-post rendering.
/// </summary>
public class PremiumPostPlan
{
    public int TotalCount { get; set; }
    public List<string> TopFeatures { get; set; } = [];
    public List<string> Enhancements { get; set; } = [];
    public List<string> BugFixes { get; set; } = [];
    public List<string> Misc { get; set; } = [];
}
