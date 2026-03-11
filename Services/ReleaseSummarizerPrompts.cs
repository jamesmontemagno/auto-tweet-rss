namespace AutoTweetRss.Services;

internal static class ReleaseSummarizerPrompts
{
    public static string GetSystemPrompt() => @"You are an expert at analyzing software release notes and creating concise, engaging summaries for social media.

Your task is to:
1. Identify the most exciting and impactful features or changes from release notes
2. Format them in a concise way with appropriate emojis
3. Ensure the summary fits within the specified character limit
4. Use emojis strategically to enhance readability and appeal
5. NEVER include user names, contributor names, or issue numbers
6. Focus ONLY on features, fixes, and improvements - not who contributed them

Emoji guidelines:
- ✨ for new features
- ⚡ for performance improvements
- 🐛 for bug fixes
- 🔒 for security updates
- 📖 for documentation
- 🎉 for major milestones

Keep the tone exciting and developer-friendly. Focus on what matters most to users.";

    public static string BuildUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, string feedType)
    {
        var isCliWeekly = string.Equals(feedType, "cli-weekly", StringComparison.OrdinalIgnoreCase);
        var estimatedCharsPerItem = feedType.StartsWith("cli", StringComparison.OrdinalIgnoreCase) ? 50 : 40;
        if (isCliWeekly)
        {
            estimatedCharsPerItem = 38;
        }
        var reserveForSuffix = totalItemCount > 5 ? 15 : 0;
        var maxItems = Math.Max(3, (maxLength - reserveForSuffix) / estimatedCharsPerItem);
        maxItems = Math.Min(maxItems, isCliWeekly ? 12 : 10);
        if (totalItemCount > 0)
        {
            maxItems = Math.Min(maxItems, totalItemCount);
        }

        if (feedType == "cli-weekly")
        {
            return BuildCliWeeklyUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
        }
        if (feedType == "cli")
        {
            return BuildCliUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
        }
        if (feedType == "cli-paragraph")
        {
            return BuildCliParagraphUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, Math.Min(maxItems, 6));
        }
        if (feedType == "vscode-week")
        {
            return BuildVSCodeWeeklyUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
        }
        if (feedType == "vscode")
        {
            return BuildVSCodeUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
        }
        if (feedType == "vscode-ai")
        {
            return BuildVSCodeAiUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, Math.Min(maxItems, 8));
        }
        if (feedType == "vscode-week-ai")
        {
            return BuildVSCodeAiWeeklyUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, Math.Min(maxItems, 8));
        }
        return BuildSdkUserPrompt(releaseTitle, releaseContent, maxLength, totalItemCount, maxItems);
    }

    private static string BuildCliUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems) =>
        $@"Summarize the following Copilot CLI release notes for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release: {totalItemCount}
Target items to show: {targetItems}

Requirements:
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- Include UP TO {targetItems} of the most important/exciting features that fit within the character limit
- Prioritize: Show as many HIGH-RELEVANCE items as possible while staying under {maxLength} characters
- NEVER include user names, contributor names, or issue/PR numbers in the summary
- Focus ONLY on what the feature does, not who contributed it
- Use emojis to make it visually appealing
- Each feature should be on its own line
- IMPORTANT: CLI feature descriptions are often long - you MUST shorten/summarize them to fit more items
- Keep each feature line concise (aim for 40-50 characters) to maximize count
- CRITICAL: If you show fewer items than the total ({totalItemCount} items), you MUST add ""...and X more"" as the FINAL line where X = items not shown
- DO NOT include any markdown formatting or headers
- DO NOT include the version number (it will be added separately)
- DO NOT truncate feature descriptions mid-sentence with ""..."" - either shorten them properly or omit them
- Output ONLY the formatted feature list, nothing else
- MAXIMIZE the number of items shown - more items is better than longer descriptions!

Example output format (when total items = 8, showing 5):
✨ Show compaction status in timeline
✨ Add Esc-Esc to undo file changes
✨ Support for GHE Cloud remote agents
⚡ Improved workspace indexing speed
🐛 Fixed file watcher memory leak
...and 3 more

Example output format (when total items = 3, showing 3):
✨ Show compaction status in timeline
✨ Add Esc-Esc to undo file changes
✨ Support for GHE Cloud remote agents";

    private static string BuildCliWeeklyUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems) =>
        $@"Create a weekly recap summary of the following Copilot CLI release notes for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release window: {totalItemCount}
Target items to show: {targetItems}

Requirements:
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- Include UP TO {targetItems} of the most important/high-impact changes across the week
- Deduplicate similar items across releases; focus on themes and top features
- EXCLUDE any items that mention ""staff flag"" or internal labels
- NEVER include user names, contributor names, or issue/PR numbers in the summary
- DO NOT include version numbers or dates
- Focus ONLY on what the features do, not who contributed them
- Use emojis to make it visually appealing
- Each highlight should be on its own line
- Keep each highlight line ultra concise (aim for 20-35 characters) to maximize count
- Prefer short noun-phrase highlights over sentences
- CRITICAL: If you show fewer items than the total ({totalItemCount} items), you MUST add ""...and X more"" as the FINAL line where X = items not shown
- DO NOT include any markdown formatting or headers
- Output ONLY the formatted highlight list, nothing else

Example output format (when total items = 9, showing 5):
✨ Smarter repo context for agents
✨ New interactive setup flow
⚡ Faster indexing for large workspaces
🐛 Fixed auth refresh edge cases
🔒 Hardened token storage behavior
...and 4 more

Example output format (when total items = 3, showing 3):
✨ Smarter repo context for agents
⚡ Faster indexing for large workspaces
🐛 Fixed auth refresh edge cases";

    private static string BuildCliParagraphUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems) =>
        $@"Write a single-paragraph summary of the following Copilot CLI release notes for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release: {totalItemCount}
Target features to mention: {targetItems}

Requirements:
- Output MUST be a single paragraph with no line breaks or bullet lists
- Use 2-4 sentences that highlight the most important features
- Include 2-4 emojis in the paragraph to emphasize major areas
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- NEVER include user names, contributor names, or issue/PR numbers
- Focus ONLY on what the features do, not who contributed them
- DO NOT include the version number (it will be added separately)
- DO NOT include markdown or headers
- Output ONLY the paragraph, nothing else

Example output:
✨ Copilot CLI now ships faster completions and smarter context selection, making large repos feel more responsive. 🧭 New navigation and help improvements streamline common workflows, while ⚡ performance and 🐛 fix updates reduce friction across everyday commands.";

    private static string BuildSdkUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems) =>
        $@"Summarize the following release notes for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release: {totalItemCount}
Target items to show: {targetItems}

Note on content format: releases may use either the old format (<h2>What's Changed</h2> with flat <li> items)
or the new format where major features are in <h3>Feature: ...</h3> headings and smaller changes are in
<h3>Other changes</h3> with <li> items prefixed by ""feature:"", ""improvement:"", or ""bugfix:"".
Prioritise the <h3> named features as the most important items; treat the ""Other changes"" <li> items as secondary.

Requirements:
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- Include UP TO {targetItems} of the most important/exciting features that fit within the character limit
- Prioritize: Show as many HIGH-RELEVANCE items as possible while staying under {maxLength} characters
- NEVER include user names, contributor names, or issue/PR numbers in the summary
- Focus ONLY on what the feature does, not who contributed it
- Use emojis to make it visually appealing
- Each feature should be on its own line
- Keep descriptions concise (aim for 35-40 characters per line) to fit more items
- CRITICAL: If you show fewer items than the total ({totalItemCount} items), you MUST add ""...and X more"" as the FINAL line where X = items not shown
- DO NOT include any markdown formatting or headers
- DO NOT include the version number (it will be added separately)
- Output ONLY the formatted feature list, nothing else
- MAXIMIZE the number of items shown - more items is better than longer descriptions!

Example output format (when total items = 8, showing 6):
✨ New AI code completion engine
⚡ 40% faster suggestion generation
🐛 Fixed context window overflow
✨ Support for Rust language
🔒 Updated security dependencies
📖 Added API migration guide
...and 2 more

Example output format (when total items = 4, showing 4):
✨ New AI code completion engine
⚡ 40% faster suggestion generation
🐛 Fixed context window overflow
✨ Support for Rust language";

    private static string BuildVSCodeUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems)
    {
        var isRichSummary = maxLength > 1000;

        if (isRichSummary)
        {
            return $@"Create a comprehensive, detailed summary of the following VS Code Insiders release for {releaseTitle}.

Release Content:
{releaseContent}

Total items in release: {totalItemCount}

Requirements:
- This is a FULL RELEASE SUMMARY - be comprehensive and detailed
- Start with 3-5 sentences providing a high-level overview of the major themes and improvements
- Maximum length: {maxLength} characters (you have plenty of space - use it wisely)
- Group features by category (e.g., Chat, Terminal, Editor, Extensions, etc.)
- Include as many important features as possible, organized by their categories
- NEVER include user names, contributor names, or issue/PR numbers
- Use emojis strategically to make it visually appealing and scannable
- Use clear category headers to organize the features
- Keep individual feature descriptions informative but concise (50-80 chars each)
- If there are more items than you can include, add ""...and X more"" at the end
- DO NOT include markdown headers with #
- Make it exciting and highlight the most impactful changes

Example output format:
VS Code Insiders delivers a major update with significant improvements across chat, terminal, and editor experiences. This release focuses on performance, discoverability, and enhanced workflows. New AI-powered features and refined UI elements make development more efficient.

🤖 Chat & AI:
✨ Improved inline chat discoverability with new UI
✨ Chat overlay hover interactions enhanced
⚡ Better performance for long chat sessions
✨ New @workspace context improvements

⌨️ Terminal:
✨ Sticky scroll setting for better navigation
⚡ Faster rendering for large outputs
🐛 Fixed Unicode character display issues

✏️ Editor:
✨ New IntelliSense improvements for TypeScript
✨ Multi-cursor enhancements
🎨 Refined syntax highlighting for JSX
⚡ Improved file watcher performance

🔧 Extensions & Settings:
✨ New extension marketplace filters
🔒 Enhanced security for extension installations
📖 Better extension documentation display

...and 15 more updates across debugging, source control, and themes.";
        }

        return $@"Summarize the following VS Code Insiders release notes for {releaseTitle}.

Release Content:
{releaseContent}

Raw list items in changelog: {totalItemCount} (note: many items may be related or duplicates — consolidate related items into single features)
Target features to show: {targetItems}

Requirements:
- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)
- CRITICAL: Your PRIMARY GOAL is to show AS MANY distinct features as possible within the character limit
- Consolidate related list items into single feature descriptions rather than listing each separately
- Output ONLY a list of the most important features, one per line, each starting with an emoji
- Do NOT include any introductory sentences, paragraph summary, or commentary
- NEVER include user names, contributor names, or issue/PR numbers
- Focus ONLY on what the feature does, not who contributed it
- Use varied emojis to make it visually appealing (✨ ⚡ 🔧 🎨 🐛 🔒 📖 etc.)
- Keep descriptions VERY concise (25-40 characters per line) to maximize the number of features shown
- Prioritize brevity over detail - shorter descriptions allow more features to be listed
- CRITICAL: ONLY add ""...and X more"" if you had to OMIT genuinely distinct features due to space constraints
- DO NOT add ""...and X more"" if you consolidated multiple related items into a single feature description
- DO NOT add ""...and X more"" if all meaningful features are already shown
- DO NOT include any markdown formatting or headers
- Output ONLY the formatted feature list, nothing else

- REMEMBER: More features shown explicitly is ALWAYS better than longer descriptions!

Example output format (showing 4 features from 6 total, with 2 omitted):
✨ Faster bracket colorization
⚡ Improved terminal rendering
🔧 New sticky scroll setting
🎨 Enhanced chat overlay UI
...and 2 more

Example output format (showing 5 consolidated features from 8 list items, all features shown):
✨ Faster bracket colorization
⚡ Improved terminal rendering
🔧 New sticky scroll setting
🎨 Enhanced chat overlay UI
🐛 Fixed file watcher issue

Example output format (single feature from multiple related list items):
✨ Major terminal performance improvements";
    }

    private static string BuildVSCodeWeeklyUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems)
    {
        var lines = new[]
        {
            $"Create a weekly recap summary of the following VS Code Insiders release notes for {releaseTitle}.",
            string.Empty,
            "Release Content:",
            releaseContent,
            string.Empty,
            $"Total items in release window: {totalItemCount}",
            $"Target items to show: {targetItems}",
            string.Empty,
            "Requirements:",
            $"- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)",
            "- Start with 1-2 sentences summarizing the week's most impactful VS Code Insiders changes",
            $"- After the summary sentences, include UP TO {targetItems} of the most important features across the week",
            "- Deduplicate similar items across days; focus on themes and top features",
            "- NEVER include user names, contributor names, or issue/PR numbers in the summary",
            "- Focus ONLY on what the features do, not who contributed them",
            "- Use emojis to make it visually appealing",
            "- Each feature should be on its own line",
            "- Keep descriptions concise (aim for 35-45 characters per line)",
            "- If you show fewer items than the total, add ...and X more as the FINAL line",
            "- DO NOT include any markdown formatting or headers",
            "- DO NOT include version numbers or dates",
            "- Output ONLY the summary and formatted feature list, nothing else",
            string.Empty,
            "Example output format:",
            "This week VS Code Insiders brings chat refinements, faster terminal rendering, and new editor conveniences.",
            string.Empty,
            "✨ Improved inline chat discoverability",
            "⚡ Faster terminal rendering for large output",
            "🔧 New sticky scroll setting in terminal",
            "🎨 Chat overlay hover UI enhanced",
            "...and 5 more"
        };

        return string.Join("\n", lines);
    }

    private static string BuildVSCodeAiUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems)
    {
        var lines = new[]
        {
            $"Summarize ONLY the AI-related updates from the following VS Code Insiders release for {releaseTitle}.",
            string.Empty,
            "Release Content:",
            releaseContent,
            string.Empty,
            $"Total items in release: {totalItemCount}",
            $"Target items to show: {targetItems}",
            string.Empty,
            "Requirements:",
            $"- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)",
            "- Include ONLY AI-related features (chat, copilots, inline suggestions, AI tools, model support, prompt features)",
            "- Start with 1-2 sentences summarizing AI themes for the release",
            $"- After the summary sentences, include UP TO {targetItems} of the most important AI features",
            "- If there are no AI-related updates, respond with: No notable AI updates in this release.",
            "- NEVER include user names, contributor names, or issue/PR numbers in the summary",
            "- Use emojis to make it visually appealing",
            "- Each feature should be on its own line",
            "- Keep descriptions concise (aim for 40-50 characters per line)",
            "- If you show fewer items than the total AI items, add ...and X more as the FINAL line",
            "- DO NOT include any markdown formatting or headers",
            "- Output ONLY the summary and formatted feature list, nothing else",
            string.Empty,
            "Example output format:",
            "VS Code Insiders expands AI workflows with stronger chat actions and richer inline suggestions.",
            string.Empty,
            "✨ New inline chat actions for refactors",
            "✨ Smarter @workspace context retrieval",
            "⚡ Faster AI response streaming",
            "...and 2 more"
        };

        return string.Join("\n", lines);
    }

    private static string BuildVSCodeAiWeeklyUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems)
    {
        var lines = new[]
        {
            $"Create a weekly recap that ONLY highlights AI-related updates from the following VS Code Insiders release notes for {releaseTitle}.",
            string.Empty,
            "Release Content:",
            releaseContent,
            string.Empty,
            $"Total items in release window: {totalItemCount}",
            $"Target items to show: {targetItems}",
            string.Empty,
            "Requirements:",
            $"- Maximum length: {maxLength} characters (this is CRITICAL - count characters carefully)",
            "- Include ONLY AI-related features (chat, copilots, inline suggestions, AI tools, model support, prompt features)",
            "- Start with 1-2 sentences summarizing AI themes across the week",
            $"- After the summary sentences, include UP TO {targetItems} of the most important AI features",
            "- If there are no AI-related updates, respond with: No notable AI updates this week.",
            "- NEVER include user names, contributor names, or issue/PR numbers in the summary",
            "- Use emojis to make it visually appealing",
            "- Each feature should be on its own line",
            "- Keep descriptions concise (aim for 35-45 characters per line)",
            "- If you show fewer items than the total AI items, add ...and X more as the FINAL line",
            "- DO NOT include any markdown formatting or headers",
            "- Output ONLY the summary and formatted feature list, nothing else",
            string.Empty,
            "Example output format:",
            "This week brings stronger AI chat workflows and better inline suggestion control.",
            string.Empty,
            "✨ New chat actions for docs edits",
            "✨ Smarter prompt variables in chat",
            "⚡ Faster AI responses in large repos",
            "...and 3 more"
        };

        return string.Join("\n", lines);
    }

    public static string GetThreadPlanSystemPrompt() => @"You are an expert at analyzing software release notes for social media.

Your task is to extract, rank, and format features from release notes. You MUST respond with valid JSON only - no prose, no code fences.

The JSON must have exactly these fields:
- ""totalCount"": integer, total number of distinct features/fixes/changes
- ""items"": array of strings, ALL features ranked by importance/excitement (most exciting first)

Rules:
- Each item must start with an appropriate emoji (✨ ⚡ 🐛 🔒 📖 🎉 🔧 🎨)
- Each item should be 40-70 characters - descriptive but concise
- NEVER include user names, contributor names, or issue/PR numbers
- Deduplicate similar items
- Focus on WHAT changed and WHY it matters to users";

    public static string GetPremiumPostSystemPrompt() => @"You are an expert social media release editor.

You MUST respond with valid JSON only and no markdown fences.

Create a Premium X mega-post plan organized into these exact categories:
- topFeatures
- enhancements
- bugFixes
- misc

Rules:
- Include the most important updates under topFeatures first
- Place performance, UX polish, and iterative upgrades under enhancements
- Place defects/regressions under bugFixes
- Place docs/tooling/other updates under misc
- Every item must start with a single relevant emoji and be concise
- Use varied emojis that fit the content; avoid using the same emoji for every item
- Prefer emoji palettes by section:
    - topFeatures: ✨ 🎉 🚀 🔥
    - enhancements: ⚡ 🔧 🎨 🛠️
    - bugFixes: 🐛 🩹 ✅
    - misc: 📖 🧰 🔒 🏗️
- Avoid repeating the same emoji on adjacent items when a different relevant emoji would work
- Never include usernames, PR numbers, or issue IDs
- Deduplicate overlapping items
- Keep wording useful for developers, not marketing fluff

JSON schema:
{
  ""totalCount"": number,
  ""topFeatures"": string[],
  ""enhancements"": string[],
  ""bugFixes"": string[],
  ""misc"": string[]
}";

    public static string GetGitHubChangelogSystemPrompt() => @"You are an expert at turning GitHub changelog content into polished social post plans.

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
- Never include links, hashtags, usernames, issue numbers, or markdown headings
- Focus on product impact, workflows, and why the update matters
- Avoid hype and repetition";
}
