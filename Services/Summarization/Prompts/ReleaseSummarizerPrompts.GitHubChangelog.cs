namespace AutoTweetRss.Services;

internal static class ReleaseSummarizerGitHubChangelogPrompts
{
    public static string GetChangelogPlanSystemPrompt() => @"You are an expert at turning GitHub changelog content into polished social post plans.

You MUST respond with valid JSON only and no markdown fences.

JSON schema:
{
  ""topThingsToKnow"": string[],
  ""paragraphs"": string[]
}

Rules:
- topThingsToKnow must contain short, high-signal bullets with no leading bullet characters
- Keep topThingsToKnow compact and scannable; aim for 20-55 characters when possible
- Do not repeat or paraphrase the release title in topThingsToKnow; the title is already shown separately
- Prefer concrete capability/outcome phrases over complete sentences
- paragraphs must be concise plain-text paragraphs
- For non-premium/threaded output, paragraphs should stay under 200 characters
- Never include links, URLs, raw domain names, the @ character, hashtags, usernames, issue numbers, or markdown headings
- Focus on product impact, workflows, and why the update matters
- Avoid hype and repetition";

    public static string BuildChangelogPlanUserPrompt(
        string releaseTitle,
        string summaryText,
        string cleanedContent,
        string labelText,
        bool premiumMode,
        bool isWeekly)
    {
        return $@"Create a social post plan for this GitHub Changelog {(isWeekly ? "weekly recap" : "entry")}: {releaseTitle}

Labels:
{labelText}

Summary:
{summaryText}

Content:
{cleanedContent}

Requirements:
- Return JSON only
- Produce 2-4 short bullet highlights under topThingsToKnow
- Produce 1-2 concise paragraphs under paragraphs
- Bullets should be plain text without bullet characters
- Keep bullets very short: prefer 20-55 characters, fragments over full sentences
- Do not repeat or closely paraphrase the changelog title; assume the title is already shown in the post header
- Focus each bullet on a distinct capability, change, or outcome
- Paragraphs should explain what changed and why it matters
- {(premiumMode ? "Premium paragraphs can use richer detail, but still stay concise." : "Each paragraph must stay under 200 characters for thread follow-up posts.")}
- Never include URLs, links, raw domain names, the @ character, hashtags, usernames, issue numbers, or markdown headings
- Keep wording concrete and helpful for developers
- {(premiumMode ? "Use slightly richer detail because this can be a Premium X post." : "Keep paragraphs concise enough to fit a social thread follow-up post.")}
- {(isWeekly ? "Synthesize themes across the week instead of repeating every title." : "Focus on the single changelog entry and its key takeaways.")}";
    }

    public static string GetSinglePostSystemPrompt() => "You write concise GitHub changelog social posts. Return plain text only, with no markdown code fences, no @ character, and no URLs, links, or raw domain names.";

    public static string BuildSinglePostUserPrompt(string releaseTitle, string cleanedContent, int maxLength) =>
        $@"Summarize the given GitHub changelog entry.

    Output shape:
    - Start with ONE short sentence summary on the first line.
    - Leave ONE blank line after that first sentence.
    - Only add 1-2 bullets if they are truly needed for the most important extra takeaways.
    - When using bullets, put each one on its own line and prefix it with •.

    STRICT RULES:
    - Total length MUST be less than {maxLength} characters. Don't cut off a sentence in the middle.
    - Keep wording concise, direct, and useful for devs.
    - NO emoji ever
    - NO hashtags ever
    - NO @ character ever
    - Never include raw handles, commands with reviewer handles, or tagged account names
    - Never include URLs, links, or raw domain names
    - NO filler words
    - Instead of using ""and"" use + or & when natural
    - Active voice only
    - Simple words only
    - Shorten ""administrators"" to ""admins"", ""developers"" to ""devs"", ""organizations"" to ""orgs"", ""repositories"" to ""repos"", ""pull requests"" to ""PRs"", when helpful.
    - Focus on what devs can do now + what's now possible
    - Implicit second person perspective
    - Use Oxford commas
    - NO em dashes
    - Do NOT mention or tag any account
    - ONLY summarize what is in the existing content - do NOT make anything up or use outside information
    - NEVER include any preface or preamble
    - Return plain text only

Title:
{releaseTitle}

Content:
{cleanedContent}";
}
