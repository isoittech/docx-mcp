using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;

namespace WordMcp.Security;

public sealed class ArtifactTokenService(IOptions<WordMcpOptions> options, TimeProvider timeProvider)
{
    private const string Version = "v1";
    private readonly byte[] key = Encoding.UTF8.GetBytes(options.Value.ArtifactSigningKey);
    private readonly TimeSpan lifetime = TimeSpan.FromMinutes(options.Value.ArtifactUrlMinutes);

    public (string Token, DateTimeOffset ExpiresAt) Create(
        string jobId,
        string artifactId,
        string fileName,
        string disposition)
    {
        var expiresAt = timeProvider.GetUtcNow().Add(lifetime);
        var expires = expiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = Sign(jobId, artifactId, fileName, expires, disposition);
        return ($"{Version}.{expires}.{WebEncoders.Base64UrlEncode(signature)}", expiresAt);
    }

    public bool Validate(
        string jobId,
        string artifactId,
        string fileName,
        string disposition,
        string token)
    {
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != Version
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < DateTimeOffset.MinValue.ToUnixTimeSeconds()
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            || DateTimeOffset.FromUnixTimeSeconds(seconds) < timeProvider.GetUtcNow())
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = WebEncoders.Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = Sign(jobId, artifactId, fileName, parts[1], disposition);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private byte[] Sign(string jobId, string artifactId, string fileName, string expires, string disposition)
    {
        var canonical = string.Join('\n', Version, jobId, artifactId, fileName, expires, disposition);
        return HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical));
    }
}
