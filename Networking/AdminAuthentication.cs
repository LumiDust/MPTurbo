using System.Security.Cryptography;
using System.Text;

namespace TurboTick.Networking;

internal sealed record AdminChallenge(byte[] Salt, byte[] Nonce, int Iterations);
internal sealed record AdminProof(byte[] Nonce, byte[] Proof);

/// <summary>The password stays local. A connection gets one fresh challenge and exactly one proof attempt.</summary>
internal static class AdminAuthentication
{
    internal const int Iterations = 210_000;
    internal const int SaltLength = 32;
    internal const int NonceLength = 32;
    internal const int KeyLength = 32;
    private static readonly byte[] Domain = "TurboTick.Admin.v4\0"u8.ToArray();

    internal static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 4096)
            throw new ArgumentException("The management password must contain 1 to 4096 characters.", nameof(password));
        var bytes = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, HashAlgorithmName.SHA256, KeyLength); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal static byte[] CreateProof(byte[] key, AdminChallenge challenge)
    {
        var transcript = new byte[Domain.Length + SaltLength + NonceLength];
        Domain.CopyTo(transcript, 0);
        challenge.Salt.CopyTo(transcript, Domain.Length);
        challenge.Nonce.CopyTo(transcript, Domain.Length + SaltLength);
        return HMACSHA256.HashData(key, transcript);
    }

    internal static void ValidateChallenge(AdminChallenge? challenge)
    {
        if (challenge is null || challenge.Salt is not { Length: SaltLength } || challenge.Nonce is not { Length: NonceLength } ||
            challenge.Iterations is < 100_000 or > 1_000_000)
            throw new NetworkException("InvalidAdminChallenge", "The server management challenge is invalid.");
    }
}
