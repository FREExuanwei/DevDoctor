using System.Security.Cryptography;
using System.Text;
using DevDoctor.Core;

namespace DevDoctor.Infrastructure;

public sealed class ScopeIdentity(byte[] key) : IScopeIdentity
{
    private readonly byte[] _key = key.Length == 32 ? key.ToArray() : throw new ArgumentException("Scope key must contain 32 bytes.", nameof(key));
    public string GetId(string kind, string value) => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(kind + "|" + value)));
}
