namespace AutoTweetRss.Services;

internal static class ReleaseSummarizerVSCodePrompts
{
    public static string BuildVSCodeUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems)
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
- This output may be split into a threaded social post, so formatting must stay stable line-by-line
- Group features by category (e.g., Chat, Terminal, Editor, Extensions, etc.)
- Include as many important features as possible, organized by their categories
- NEVER include user names, contributor names, or issue/PR numbers
- NEVER include the @ character, URLs, links, or raw domain names
- Prefer plain text, but sprinkle in some emoji for the most impactful items
- Keep emoji selective and sparse: use them on roughly 15-20% of category headers or feature lines, never more than one emoji per line
- If you include overview sentences, add a blank line before the first category header
- Whenever you output a feature list, EVERY feature item line MUST start with ""• ""
- NEVER output plain unbulleted feature lines
- Keep category headers unbulleted and end them with a colon
- Only use emoji where it adds emphasis or clarity; never decorate every item
- Use clear category headers to organize the features
- Keep individual feature descriptions informative but concise (50-80 chars each)
- If there are more items than you can include, add ""...and X more"" at the end
- DO NOT include markdown headers with #
- Make it exciting and highlight the most impactful changes

Example output format:
VS Code Insiders delivers a major update with significant improvements across chat, terminal, and editor experiences. This release focuses on performance, discoverability, and enhanced workflows. New AI-powered features and refined UI elements make development more efficient.

Chat & AI:
• Improved inline chat discoverability with new UI
• Chat overlay hover interactions enhanced
• Better performance for long chat sessions
• New workspace context improvements

Terminal:
• Sticky scroll setting for better navigation
• Faster rendering for large outputs
• Fixed Unicode character display issues

Editor:
• New IntelliSense improvements for TypeScript
• Multi-cursor enhancements
• Refined syntax highlighting for JSX
• Improved file watcher performance

Extensions & Settings:
• New extension marketplace filters
• Enhanced security for extension installations
• Better extension documentation display

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
- Output ONLY a list of the most important features, one per line
- Do NOT include any introductory sentences, paragraph summary, or commentary
- This output will be split into a threaded social post, so every feature must remain a standalone line
- NEVER include user names, contributor names, or issue/PR numbers
- NEVER include the @ character, URLs, links, or raw domain names
- Focus ONLY on what the feature does, not who contributed it
- Prefer plain text, but sprinkle in some emoji for the most impactful items
- Keep emoji selective and sparse: use them on roughly 15-20% of feature lines, never more than one emoji per line
- EVERY feature line MUST start with ""• ""
- NEVER return a plain sentence list without the leading ""• "" marker
- Only use emoji where it adds emphasis or clarity; never decorate every line
- Keep descriptions VERY concise (25-40 characters per line) to maximize the number of features shown
- Prioritize brevity over detail - shorter descriptions allow more features to be listed
- CRITICAL: ONLY add ""...and X more"" if you had to OMIT genuinely distinct features due to space constraints
- DO NOT add ""...and X more"" if you consolidated multiple related items into a single feature description
- DO NOT add ""...and X more"" if all meaningful features are already shown
- DO NOT include any markdown formatting or headers
- Output ONLY the formatted feature list, nothing else

- REMEMBER: More features shown explicitly is ALWAYS better than longer descriptions!

Example output format (showing 4 features from 6 total, with 2 omitted):
• Faster bracket colorization
• Improved terminal rendering
• New sticky scroll setting
• Enhanced chat overlay UI
...and 2 more

Example output format (showing 5 consolidated features from 8 list items, all features shown):
• Faster bracket colorization
• Improved terminal rendering
• New sticky scroll setting
• Enhanced chat overlay UI
• Fixed file watcher issue

Example output format (single feature from multiple related list items):
• Major terminal performance improvements";
    }

    public static string BuildVSCodeWeeklyUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems)
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
            "- Leave exactly one blank line after the summary sentences before the feature list",
            $"- After the summary sentences, include UP TO {targetItems} of the most important features across the week",
            "- Deduplicate similar items across days; focus on themes and top features",
            "- NEVER include user names, contributor names, or issue/PR numbers in the summary",
            "- NEVER include the @ character, URLs, links, or raw domain names in the summary",
            "- Focus ONLY on what the features do, not who contributed them",
            "- Prefer plain text, but sprinkle in some emoji for the most impactful items",
            "- Keep emoji selective and sparse: use them on roughly 15-20% of feature lines, never more than one emoji per line",
            "- EVERY feature line MUST start with \"• \"",
            "- NEVER output plain unbulleted feature lines",
            "- Only use emoji where it adds emphasis or clarity; never decorate every line",
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
            "• Improved inline chat discoverability",
            "• Faster terminal rendering for large output",
            "• New sticky scroll setting in terminal",
            "• Chat overlay hover UI enhanced",
            "...and 5 more"
        };

        return string.Join("\n", lines);
    }

    public static string BuildVSCodeAiUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems)
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
            "- Leave exactly one blank line after the summary sentences before the feature list",
            $"- After the summary sentences, include UP TO {targetItems} of the most important AI features",
            "- If there are no AI-related updates, respond with: No notable AI updates in this release.",
            "- NEVER include user names, contributor names, or issue/PR numbers in the summary",
            "- NEVER include the @ character, URLs, links, or raw domain names in the summary",
            "- Prefer plain text, but sprinkle in some emoji for the most impactful items",
            "- Keep emoji selective and sparse: use them on roughly 15-20% of feature lines, never more than one emoji per line",
            "- EVERY feature line MUST start with \"• \"",
            "- NEVER output plain unbulleted feature lines",
            "- Only use emoji where it adds emphasis or clarity; never decorate every line",
            "- Each feature should be on its own line",
            "- Keep descriptions concise (aim for 40-50 characters per line)",
            "- If you show fewer items than the total AI items, add ...and X more as the FINAL line",
            "- DO NOT include any markdown formatting or headers",
            "- Output ONLY the summary and formatted feature list, nothing else",
            string.Empty,
            "Example output format:",
            "VS Code Insiders expands AI workflows with stronger chat actions and richer inline suggestions.",
            string.Empty,
            "• New inline chat actions for refactors",
            "• Smarter workspace context retrieval",
            "• Faster AI response streaming",
            "...and 2 more"
        };

        return string.Join("\n", lines);
    }

    public static string BuildVSCodeAiWeeklyUserPrompt(string releaseTitle, string releaseContent, int maxLength, int totalItemCount, int targetItems)
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
            "- Leave exactly one blank line after the summary sentences before the feature list",
            $"- After the summary sentences, include UP TO {targetItems} of the most important AI features",
            "- If there are no AI-related updates, respond with: No notable AI updates this week.",
            "- NEVER include user names, contributor names, or issue/PR numbers in the summary",
            "- NEVER include the @ character, URLs, links, or raw domain names in the summary",
            "- Prefer plain text, but sprinkle in some emoji for the most impactful items",
            "- Keep emoji selective and sparse: use them on roughly 15-20% of feature lines, never more than one emoji per line",
            "- EVERY feature line MUST start with \"• \"",
            "- NEVER output plain unbulleted feature lines",
            "- Only use emoji where it adds emphasis or clarity; never decorate every line",
            "- Each feature should be on its own line",
            "- Keep descriptions concise (aim for 35-45 characters per line)",
            "- If you show fewer items than the total AI items, add ...and X more as the FINAL line",
            "- DO NOT include any markdown formatting or headers",
            "- Output ONLY the summary and formatted feature list, nothing else",
            string.Empty,
            "Example output format:",
            "This week brings stronger AI chat workflows and better inline suggestion control.",
            string.Empty,
            "• New chat actions for docs edits",
            "• Smarter prompt variables in chat",
            "• Faster AI responses in large repos",
            "...and 3 more"
        };

        return string.Join("\n", lines);
    }
}
