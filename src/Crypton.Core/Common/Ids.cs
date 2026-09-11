using System.Security.Cryptography;

namespace Crypton.Core.Common;

public static class Ids
{
    /// <summary>Time-ordered UUID (v7) so primary keys stay index-friendly.</summary>
    public static Guid New() => Guid.CreateVersion7();

    /// <summary>Random lowercase alphanumeric string (a-z0-9), suitable for references and codes.</summary>
    public static string RandomToken(int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        return RandomNumberGenerator.GetString(alphabet, length);
    }

    public static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes);
    }
}
