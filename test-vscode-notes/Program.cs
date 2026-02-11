using System.Text.RegularExpressions;

// ── 1. Resolve the aka.ms redirect ──────────────────────────────────────────

const string redirectUrl = "https://aka.ms/vscode/updates/insiders";
const string rawGitHubBaseUrl = "https://raw.githubusercontent.com/microsoft/vscode-docs/refs/heads/main/release-notes/";

Console.WriteLine("=== VS Code Insiders Markdown Parsing Test ===\n");

using var http = new HttpClient();

Console.WriteLine($"Following redirect: {redirectUrl}");
using var request = new HttpRequestMessage(HttpMethod.Head, redirectUrl);
using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

var finalUrl = response.RequestMessage?.RequestUri?.ToString();
Console.WriteLine($"  Status:    {(int)response.StatusCode} {response.StatusCode}");
Console.WriteLine($"  Final URL: {finalUrl}");

int? resolvedVersion = null;
if (!string.IsNullOrEmpty(finalUrl))
{
    var versionMatch = Regex.Match(finalUrl, @"v1_(\d+)", RegexOptions.IgnoreCase);
    if (versionMatch.Success)
    {
        resolvedVersion = int.Parse(versionMatch.Groups[1].Value);
        Console.WriteLine($"  Extracted version: v1_{resolvedVersion}");
    }
    else
    {
        Console.WriteLine("  ⚠ Could not extract version number from URL");
    }
}
else
{
    Console.WriteLine("  ⚠ Redirect did not produce a final URL");
}

// ── 2. Build candidate raw GitHub markdown URLs ─────────────────────────────

Console.WriteLine("\n=== Candidate Markdown URLs ===\n");

var candidates = new List<string>();
if (resolvedVersion.HasValue)
{
    candidates.Add($"{rawGitHubBaseUrl}v1_{resolvedVersion.Value}.md");
    candidates.Add($"{rawGitHubBaseUrl}v1_{resolvedVersion.Value - 1}.md");
}
else
{
    Console.WriteLine("  (skipped — no version resolved)");
}

foreach (var url in candidates)
{
    Console.WriteLine($"  {url}");
}

// ── 3. Fetch & parse raw markdown for the last 7 days ───────────────────────

Console.WriteLine("\n=== Parsing Markdown Release Notes (last 7 days) ===\n");

var today = DateTime.UtcNow.Date;
var weekStart = today.AddDays(-6);
Console.WriteLine($"  Window: {weekStart:yyyy-MM-dd} to {today:yyyy-MM-dd}\n");

var dateHeadingPattern = new Regex(
    @"^##\s+(January|February|March|April|May|June|July|August|September|October|November|December)\s+(\d{1,2})(?:,\s*(\d{4}))?",
    RegexOptions.IgnoreCase);

var totalFeatures = 0;

foreach (var url in candidates)
{
    Console.WriteLine($"  Fetching {url} ...");
    try
    {
        var markdown = await http.GetStringAsync(url);

        // ── Validate front matter ──
        if (!markdown.StartsWith("---"))
        {
            Console.WriteLine("    ⚠ No front matter found, skipping.\n");
            continue;
        }

        var fmEnd = markdown.IndexOf("---", 3, StringComparison.Ordinal);
        if (fmEnd < 0)
        {
            Console.WriteLine("    ⚠ Malformed front matter, skipping.\n");
            continue;
        }

        var frontMatter = markdown[3..fmEnd];
        var hasInsiders = frontMatter
            .Split('\n')
            .Any(line =>
            {
                var trimmed = line.Trim();
                return trimmed.StartsWith("ProductEdition:", StringComparison.OrdinalIgnoreCase)
                       && trimmed["ProductEdition:".Length..].Trim()
                           .Equals("Insiders", StringComparison.OrdinalIgnoreCase);
            });

        Console.WriteLine($"    Front matter ProductEdition: {(hasInsiders ? "Insiders ✓" : "NOT Insiders ✗")}");
        if (!hasInsiders)
        {
            Console.WriteLine("    Skipping non-Insiders page.\n");
            continue;
        }

        // ── Parse date sections and bullet features ──
        var lines = markdown.Split('\n');
        var datesFound = new List<DateTime>();
        var featureCount = 0;
        DateTime? currentDate = null;
        var currentBulletLines = new List<string>();
        var sectionFeatureCount = 0;

        void FlushBullet()
        {
            if (currentBulletLines.Count == 0) return;
            var text = string.Join(" ", currentBulletLines).Trim();
            currentBulletLines.Clear();
            if (text.Length >= 5) sectionFeatureCount++;
        }

        void FlushSection()
        {
            FlushBullet();
            if (currentDate != null && sectionFeatureCount > 0
                && currentDate.Value.Date >= weekStart && currentDate.Value.Date <= today)
            {
                featureCount += sectionFeatureCount;
            }
            sectionFeatureCount = 0;
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');
            var dateMatch = dateHeadingPattern.Match(trimmed);

            if (dateMatch.Success)
            {
                FlushSection();
                var parsedDate = ParseDate(dateMatch, today.Year);
                if (parsedDate != null)
                {
                    currentDate = parsedDate.Value;
                    datesFound.Add(parsedDate.Value);
                    var inWindow = parsedDate.Value.Date >= weekStart && parsedDate.Value.Date <= today;
                    Console.WriteLine($"    {parsedDate.Value:yyyy-MM-dd} \"{trimmed[..Math.Min(trimmed.Length, 70)]}\" {(inWindow ? "✓" : "○")}");
                }
                continue;
            }

            if (currentDate == null) continue;

            if (trimmed.StartsWith("* "))
            {
                FlushBullet();
                currentBulletLines.Add(trimmed[2..].TrimEnd());
                continue;
            }

            if (currentBulletLines.Count > 0 && !string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith('#'))
            {
                currentBulletLines.Add(trimmed.TrimEnd());
                continue;
            }

            if (currentBulletLines.Count > 0)
            {
                FlushBullet();
            }
        }

        FlushSection();

        Console.WriteLine($"    Dates on page: {string.Join(", ", datesFound.Select(d => d.ToString("MMM d")))}");
        Console.WriteLine($"    Features in window: {featureCount}\n");
        totalFeatures += featureCount;
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"    HTTP error: {ex.StatusCode} {ex.Message}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    Error: {ex.Message}\n");
    }
}

Console.WriteLine($"=== Total features found in window: {totalFeatures} ===");

// ── Helpers ──────────────────────────────────────────────────────────────────

static DateTime? ParseDate(Match match, int defaultYear)
{
    try
    {
        var month = match.Groups[1].Value;
        var day = int.Parse(match.Groups[2].Value);
        var year = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : defaultYear;
        var monthNumber = DateTime.ParseExact(month, "MMMM", System.Globalization.CultureInfo.InvariantCulture).Month;
        return new DateTime(year, monthNumber, day);
    }
    catch
    {
        return null;
    }
}
