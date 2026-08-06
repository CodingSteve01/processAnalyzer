using ProcessAnalyzer.Web.Auth;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// The shape check in front of the stored password hash. Verification itself cannot report a broken hash: it answers
/// "no" to a malformed one exactly as it answers a wrong password, and the installation that hit this saw nothing but
/// a login rejecting the right password.
/// </summary>
public class DashboardAuthTests
{
    [Fact]
    public void Own_hash_is_well_formed()
    {
        Assert.True(DashboardAuth.IsWellFormed(DashboardAuth.Hash("a-password-long-enough")));
    }

    [Theory]
    // What docker compose leaves of a hash when the env file is not escaped: everything from the first dollar on is
    // read as a variable name and substituted away.
    [InlineData("pbkdf2-sha256")]
    [InlineData("pbkdf2-sha256$")]
    [InlineData("pbkdf2-sha256$210000$c2FsdA==")]
    [InlineData("pbkdf2-sha256$not-a-number$c2FsdA==$a2V5")]
    [InlineData("scrypt$210000$c2FsdA==$a2V5")]
    [InlineData("")]
    public void Mangled_hash_is_rejected(string stored)
    {
        Assert.False(DashboardAuth.IsWellFormed(stored));
    }
}
