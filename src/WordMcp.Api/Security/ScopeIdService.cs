using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using WordMcp.Configuration;
using WordMcp.Domain;

namespace WordMcp.Security;

public sealed class ScopeIdService(IOptions<WordMcpOptions> options)
{
    private readonly byte[] key = Encoding.UTF8.GetBytes(options.Value.ScopeHmacKey);

    public CallerScope Create(CallerContext caller)
    {
        var userScope = Compute("word-mcp:user:v1", caller.UserId);
        var conversationScope = Compute(
            "word-mcp:conversation:v1",
            string.Concat(caller.UserId, "\n", caller.ConversationId));
        return new CallerScope(userScope, conversationScope);
    }

    public string? CreateMessageScope(CallerContext caller) =>
        string.IsNullOrWhiteSpace(caller.MessageId)
            ? null
            : Compute(
                "word-mcp:message:v1",
                string.Concat(caller.UserId, "\n", caller.ConversationId, "\n", caller.MessageId));

    private string Compute(string domain, string value)
    {
        var input = Encoding.UTF8.GetBytes(string.Concat(domain, "\0", value));
        return Convert.ToHexString(HMACSHA256.HashData(key, input)).ToLowerInvariant();
    }
}
