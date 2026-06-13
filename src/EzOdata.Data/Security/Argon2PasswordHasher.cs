using System.Security.Cryptography;
using System.Text;
using EzOdata.Core.Security;
using Konscious.Security.Cryptography;

namespace EzOdata.Data.Security;

/// <summary>Argon2id parameters; spec 08 §3.2 defines the production floor.</summary>
public sealed record Argon2Parameters(int MemoryKib, int Iterations, int Parallelism)
{
    public static Argon2Parameters Default { get; } = new(MemoryKib: 64 * 1024, Iterations: 3, Parallelism: 4);
}

/// <summary>
/// Argon2id password hashing in PHC string format:
/// $argon2id$v=19$m=...,t=...,p=...$base64(salt)$base64(hash)
/// </summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly Argon2Parameters _parameters;

    public Argon2PasswordHasher(Argon2Parameters? parameters = null)
        => _parameters = parameters ?? Argon2Parameters.Default;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, _parameters);
        return $"$argon2id$v=19$m={_parameters.MemoryKib},t={_parameters.Iterations},p={_parameters.Parallelism}" +
               $"${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public PasswordVerification Verify(string password, string storedHash)
    {
        if (!TryParse(storedHash, out var stored, out var salt, out var expected))
        {
            return PasswordVerification.Failed;
        }

        var actual = Compute(password, salt, stored);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            return PasswordVerification.Failed;
        }

        var outdated = stored.MemoryKib < _parameters.MemoryKib
                       || stored.Iterations < _parameters.Iterations
                       || stored.Parallelism < _parameters.Parallelism;
        return outdated ? PasswordVerification.SuccessRehashNeeded : PasswordVerification.Success;
    }

    private static byte[] Compute(string password, byte[] salt, Argon2Parameters p)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = p.MemoryKib,
            Iterations = p.Iterations,
            DegreeOfParallelism = p.Parallelism,
        };
        return argon2.GetBytes(HashBytes);
    }

    private static bool TryParse(string stored, out Argon2Parameters parameters, out byte[] salt, out byte[] hash)
    {
        parameters = null!;
        salt = hash = [];
        if (string.IsNullOrEmpty(stored)) return false;

        // $argon2id$v=19$m=65536,t=3,p=4$<salt>$<hash>
        var parts = stored.Split('$', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not ["argon2id", "v=19", var paramPart, var saltPart, var hashPart]) return false;

        int memory = 0, iterations = 0, parallelism = 0;
        foreach (var kv in paramPart.Split(','))
        {
            var pieces = kv.Split('=');
            if (pieces.Length != 2 || !int.TryParse(pieces[1], out var value)) return false;
            switch (pieces[0])
            {
                case "m": memory = value; break;
                case "t": iterations = value; break;
                case "p": parallelism = value; break;
                default: return false;
            }
        }

        if (memory <= 0 || iterations <= 0 || parallelism <= 0) return false;

        try
        {
            salt = Convert.FromBase64String(saltPart);
            hash = Convert.FromBase64String(hashPart);
        }
        catch (FormatException)
        {
            return false;
        }

        parameters = new Argon2Parameters(memory, iterations, parallelism);
        return true;
    }
}
