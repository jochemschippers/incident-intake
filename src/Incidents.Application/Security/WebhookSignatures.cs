using System.Security.Cryptography;
using System.Text;

namespace Incidents.Application.Security;

// Every inbound webhook is authenticated with HMAC-SHA256 over the raw body using a shared secret.
// Constant-time compare blocks timing attacks; Slack's timestamp window blocks replays.
public static class WebhookSignatures
{
    public static readonly TimeSpan SlackMaxAge = TimeSpan.FromMinutes(5);

    // https://api.slack.com/authentication/verifying-requests-from-slack
    public static bool VerifySlack(string signingSecret, string? timestamp, string body, string? signature, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(signingSecret) || signature is null || !long.TryParse(timestamp, out var ts)) return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(ts)).Duration() > SlackMaxAge) return false;
        return Matches(signingSecret, $"v0:{timestamp}:{body}", "v0=", signature);
    }

    // https://docs.github.com/webhooks/using-webhooks/validating-webhook-deliveries
    public static bool VerifyGitHub(string secret, string body, string? signature) =>
        !string.IsNullOrEmpty(secret) && signature is not null && Matches(secret, body, "sha256=", signature);

    public static string Sign(string secret, string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();

    private static bool Matches(string secret, string payload, string prefix, string signature) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(prefix + Sign(secret, payload)),
            Encoding.ASCII.GetBytes(signature));
}
