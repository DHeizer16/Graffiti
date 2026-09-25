using System.Security.Cryptography;

namespace GlobalGraffitiWall.API;

public static class PasswordSecurity
{
    private const int SaltSize = 16; // 128-bit salt
    private const int KeySize = 32;   // 256-bit subkey
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    /// <summary>
    /// Hashes a password with a newly generated cryptographically secure random salt.
    /// Returns the (Hash, Salt) pair as base64 strings.
    /// </summary>
    public static (string Hash, string Salt) HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            Algorithm,
            KeySize);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    /// <summary>
    /// Verifies that a plain-text password matches a stored base64 hash and salt
    /// using constant-time comparison to prevent timing attacks.
    /// </summary>
    public static bool VerifyPassword(string password, string storedHashBase64, string storedSaltBase64)
    {
        try
        {
            byte[] salt = Convert.FromBase64String(storedSaltBase64);
            byte[] expectedHash = Convert.FromBase64String(storedHashBase64);

            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                Algorithm,
                KeySize);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }
}
