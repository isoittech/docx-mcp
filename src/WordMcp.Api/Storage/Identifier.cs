using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace WordMcp.Storage;

internal static class Identifier
{
    public static string New(string prefix)
    {
        Span<byte> bytes = stackalloc byte[18];
        RandomNumberGenerator.Fill(bytes);
        return string.Concat(prefix, WebEncoders.Base64UrlEncode(bytes));
    }

    public static bool IsValid(string? value, string prefix) =>
        value is not null
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.Length is >= 20 and <= 64
        && value[prefix.Length..].All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
