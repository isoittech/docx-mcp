using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;

namespace WordMcp.Security;

public sealed class CursorTokenService(IOptions<WordMcpOptions> options)
{
    private const byte Version = 1;
    private readonly byte[] key = Encoding.UTF8.GetBytes(options.Value.ScopeHmacKey);

    public string Create(string analysisId, string kind, int offset, CallerScope scope)
    {
        Span<byte> offsetBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(offsetBytes, offset);
        var context = Canonical(analysisId, kind, scope);
        var payload = new byte[1 + offsetBytes.Length + 16];
        payload[0] = Version;
        offsetBytes.CopyTo(payload.AsSpan(1, 4));
        var signatureInput = new byte[context.Length + 5];
        context.CopyTo(signatureInput, 0);
        payload.AsSpan(0, 5).CopyTo(signatureInput.AsSpan(context.Length));
        HMACSHA256.HashData(key, signatureInput).AsSpan(0, 16).CopyTo(payload.AsSpan(5));
        return string.Concat("cur_", WebEncoders.Base64UrlEncode(payload));
    }

    public int Parse(string cursor, string analysisId, string kind, CallerScope scope)
    {
        if (!cursor.StartsWith("cur_", StringComparison.Ordinal))
        {
            throw Invalid();
        }

        byte[] payload;
        try
        {
            payload = WebEncoders.Base64UrlDecode(cursor["cur_".Length..]);
        }
        catch (FormatException)
        {
            throw Invalid();
        }

        if (payload.Length != 21 || payload[0] != Version)
        {
            throw Invalid();
        }

        var context = Canonical(analysisId, kind, scope);
        var signatureInput = new byte[context.Length + 5];
        context.CopyTo(signatureInput, 0);
        payload.AsSpan(0, 5).CopyTo(signatureInput.AsSpan(context.Length));
        var expected = HMACSHA256.HashData(key, signatureInput).AsSpan(0, 16);
        if (!CryptographicOperations.FixedTimeEquals(expected, payload.AsSpan(5, 16)))
        {
            throw Invalid();
        }

        var offset = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(1, 4));
        if (offset < 0)
        {
            throw Invalid();
        }

        return offset;
    }

    private static byte[] Canonical(string analysisId, string kind, CallerScope scope) =>
        Encoding.UTF8.GetBytes(string.Join('\n', "word-mcp:cursor:v1", analysisId, kind, scope.ConversationScope));

    private static WordMcpException Invalid() => new(
        "invalid_cursor",
        "$.cursor",
        "The analysis cursor is invalid or belongs to another scope.",
        "Use next_cursor exactly as returned by word_get_analysis_chunk.");
}
