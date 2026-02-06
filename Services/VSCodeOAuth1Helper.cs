using System.Security.Cryptography;
using System.Text;

namespace AutoTweetRss.Services;

public class VSCodeOAuth1Helper
{
    private readonly string _consumerKey;
    private readonly string _consumerSecret;
    private readonly string _accessToken;
    private readonly string _accessTokenSecret;

    public VSCodeOAuth1Helper()
    {
        _consumerKey = Environment.GetEnvironmentVariable("TWITTER_VSCODE_API_KEY")
            ?? throw new InvalidOperationException("TWITTER_VSCODE_API_KEY not configured");
        _consumerSecret = Environment.GetEnvironmentVariable("TWITTER_VSCODE_API_SECRET")
            ?? throw new InvalidOperationException("TWITTER_VSCODE_API_SECRET not configured");
        _accessToken = Environment.GetEnvironmentVariable("TWITTER_VSCODE_ACCESS_TOKEN")
            ?? throw new InvalidOperationException("TWITTER_VSCODE_ACCESS_TOKEN not configured");
        _accessTokenSecret = Environment.GetEnvironmentVariable("TWITTER_VSCODE_ACCESS_TOKEN_SECRET")
            ?? throw new InvalidOperationException("TWITTER_VSCODE_ACCESS_TOKEN_SECRET not configured");
    }

    public string GenerateAuthorizationHeader(string httpMethod, string url)
    {
        var timestamp = GetTimestamp();
        var nonce = GetNonce();

        var oauthParams = new SortedDictionary<string, string>
        {
            { "oauth_consumer_key", _consumerKey },
            { "oauth_nonce", nonce },
            { "oauth_signature_method", "HMAC-SHA1" },
            { "oauth_timestamp", timestamp },
            { "oauth_token", _accessToken },
            { "oauth_version", "1.0" }
        };

        var parameterString = string.Join("&",
            oauthParams.Select(kvp => $"{PercentEncode(kvp.Key)}={PercentEncode(kvp.Value)}"));

        var signatureBaseString = $"{httpMethod.ToUpper()}&{PercentEncode(url)}&{PercentEncode(parameterString)}";

        var signingKey = $"{PercentEncode(_consumerSecret)}&{PercentEncode(_accessTokenSecret)}";

        var signature = GenerateSignature(signatureBaseString, signingKey);
        oauthParams.Add("oauth_signature", signature);

        var headerParams = oauthParams.Select(kvp => $"{PercentEncode(kvp.Key)}=\"{PercentEncode(kvp.Value)}\"");
        return $"OAuth {string.Join(", ", headerParams)}";
    }

    private static string GenerateSignature(string signatureBaseString, string signingKey)
    {
        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(signingKey));
        var hash = hmac.ComputeHash(Encoding.ASCII.GetBytes(signatureBaseString));
        return Convert.ToBase64String(hash);
    }

    private static string GetTimestamp()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
    }

    private static string GetNonce()
    {
        return Guid.NewGuid().ToString("N");
    }

    private static string PercentEncode(string value)
    {
        var encoded = Uri.EscapeDataString(value);

        encoded = encoded
            .Replace("!", "%21")
            .Replace("*", "%2A")
            .Replace("'", "%27")
            .Replace("(", "%28")
            .Replace(")", "%29");

        return encoded;
    }
}
