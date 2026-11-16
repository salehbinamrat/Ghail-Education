using System.Security.Cryptography;

namespace SchoolObservatory.Core;

public static class PasswordService
{
    private const int Iterations = 350_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Algorithm = "PBKDF2-SHA256";

    public static void AssertStrong(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12 ||
            password.Length > 128 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new ArgumentException("كلمة المرور يجب أن تتكون من 12 حرفا على الأقل وتحتوي أحرفا وأرقاما.");
    }

    public static string Hash(string password)
    {
        AssertStrong(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != Algorithm || !int.TryParse(parts[1], out int count) ||
                count < 100_000 || count > 1_000_000) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var stored = Convert.FromBase64String(parts[3]);
            if (salt.Length != SaltSize || stored.Length != HashSize) return false;
            byte[] candidate = Rfc2898DeriveBytes.Pbkdf2(password, salt, count, HashAlgorithmName.SHA256, HashSize);
            return CryptographicOperations.FixedTimeEquals(candidate, stored);
        }
        catch (FormatException) { return false; }
    }
}
