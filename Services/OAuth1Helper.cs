using System.Security.Cryptography;
using System.Text;

namespace AutoTweetRss.Services;

public class OAuth1Helper
{
    private readonly string? _consumerKey;
    private readonly string? _consumerSecret;
    private readonly string? _accessToken;
    private readonly string? _accessTokenSecret;

    /// <summary>
    /// Whether all required credentials are configured.
    /// </summary>
    public bool IsConfigured { get; }

    public OAuth1Helper(string envVarPrefix = "TWITTER_")
    {
        _consumerKey = Environment.GetEnvironmentVariable($"{envVarPrefix}API_KEY");
        _consumerSecret = Environment.GetEnvironmentVariable($"{envVarPrefix}API_SECRET");
        _accessToken = Environment.GetEnvironmentVariable($"{envVarPrefix}ACCESS_TOKEN");
        _accessTokenSecret = Environment.GetEnvironmentVariable($"{envVarPrefix}ACCESS_TOKEN_SECRET");

        IsConfigured = !string.IsNullOrEmpty(_consumerKey)
            && !string.IsNullOrEmpty(_consumerSecret)
            && !string.IsNullOrEmpty(_accessToken)
            && !string.IsNullOrEmpty(_accessTokenSecret);
    }

    public string GenerateAuthorizationHeader(string httpMethod, string url)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("OAuth1 credentials are not configured.");
        }

        var timestamp = GetTimestamp();
        var nonce = GetNonce();

        var oauthParams = new SortedDictionary<string, string>
        {
            { "oauth_consumer_key", _consumerKey! },
            { "oauth_nonce", nonce },
            { "oauth_signature_method", "HMAC-SHA1" },
            { "oauth_timestamp", timestamp },
            { "oauth_token", _accessToken! },
            { "oauth_version", "1.0" }
        };

        // Create signature base string
        var parameterString = string.Join("&", 
            oauthParams.Select(kvp => $"{PercentEncode(kvp.Key)}={PercentEncode(kvp.Value)}"));
        
        var signatureBaseString = $"{httpMethod.ToUpper()}&{PercentEncode(url)}&{PercentEncode(parameterString)}";

        // Create signing key
        var signingKey = $"{PercentEncode(_consumerSecret!)}&{PercentEncode(_accessTokenSecret!)}";

        // Generate signature
        var signature = GenerateSignature(signatureBaseString, signingKey);
        oauthParams.Add("oauth_signature", signature);

        // Build Authorization header
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
        // OAuth 1.0a requires RFC 3986 percent encoding
        var encoded = Uri.EscapeDataString(value);
        
        // Uri.EscapeDataString doesn't encode some characters that OAuth requires
        encoded = encoded
            .Replace("!", "%21")
            .Replace("*", "%2A")
            .Replace("'", "%27")
            .Replace("(", "%28")
            .Replace(")", "%29");
        
        return encoded;
    }
}
