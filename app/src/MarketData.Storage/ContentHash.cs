using System.Security.Cryptography;
using System.Text;

namespace MarketData.Storage;

/// <summary>SHA-256 over a vendor payload, used to short-circuit unchanged fetches.</summary>
public static class ContentHash
{
    public static string Sha256(string payload)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(payload));

        return "sha256:" + Convert.ToHexStringLower(digest);
    }
}
