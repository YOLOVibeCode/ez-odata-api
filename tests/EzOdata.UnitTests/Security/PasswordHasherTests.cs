using EzOdata.Core.Security;
using EzOdata.Data.Security;
using Xunit;

namespace EzOdata.UnitTests.Security;

public class PasswordHasherTests
{
    // Reduced parameters so the suite stays fast; production floor is enforced separately.
    private static Argon2PasswordHasher CreateFast() =>
        new(new Argon2Parameters(MemoryKib: 8 * 1024, Iterations: 1, Parallelism: 1));

    [Fact]
    public void Hash_then_verify_succeeds()
    {
        var hasher = CreateFast();
        var hash = hasher.Hash("correct horse battery staple");

        Assert.Equal(PasswordVerification.Success, hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Wrong_password_fails()
    {
        var hasher = CreateFast();
        var hash = hasher.Hash("password-one");

        Assert.Equal(PasswordVerification.Failed, hasher.Verify("password-two", hash));
    }

    [Fact]
    public void Same_password_produces_different_hashes_due_to_random_salt()
    {
        var hasher = CreateFast();
        Assert.NotEqual(hasher.Hash("p@ssword12345"), hasher.Hash("p@ssword12345"));
    }

    [Fact]
    public void Hash_is_versioned_phc_format()
    {
        var hash = CreateFast().Hash("p@ssword12345");
        Assert.StartsWith("$argon2id$", hash);
    }

    [Fact]
    public void Verify_with_stronger_current_parameters_reports_rehash_needed()
    {
        var weak = CreateFast();
        var hash = weak.Hash("p@ssword12345");

        var strong = new Argon2PasswordHasher(new Argon2Parameters(MemoryKib: 16 * 1024, Iterations: 2, Parallelism: 1));
        Assert.Equal(PasswordVerification.SuccessRehashNeeded, strong.Verify("p@ssword12345", hash));
    }

    [Fact]
    public void Garbage_stored_hash_fails_safely()
    {
        var hasher = CreateFast();
        Assert.Equal(PasswordVerification.Failed, hasher.Verify("anything", "not-a-hash"));
        Assert.Equal(PasswordVerification.Failed, hasher.Verify("anything", ""));
    }

    [Fact]
    public void Production_defaults_meet_spec_floor()
    {
        // Spec 08 §3.2: memory 64 MiB, iterations 3, parallelism 4 — never below.
        var p = Argon2Parameters.Default;
        Assert.True(p.MemoryKib >= 64 * 1024);
        Assert.True(p.Iterations >= 3);
        Assert.True(p.Parallelism >= 4);
    }
}
