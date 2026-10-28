using System.Security.Claims;
using K7.Clients.Shared.Helpers;

namespace K7.Clients.ComponentTests.Helpers;

[TestFixture]
public class StoredSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    [Test]
    public void ParseExpiry_ShouldRoundTrip_WhenFormatted()
    {
        var expiresAt = Now.AddHours(1);

        StoredSession.ParseExpiry(StoredSession.FormatExpiry(expiresAt)).Should().Be(expiresAt);
    }

    [Test]
    public void ParseExpiry_ShouldBeNull_WhenMissingOrInvalid()
    {
        StoredSession.ParseExpiry(null).Should().BeNull();
        StoredSession.ParseExpiry("").Should().BeNull();
        StoredSession.ParseExpiry("not-a-date").Should().BeNull();
    }

    [Test]
    public void IsAccessTokenUsable_ShouldBeFalse_WhenExpiryUnknown()
    {
        StoredSession.IsAccessTokenUsable(null, Now).Should().BeFalse();
    }

    [Test]
    public void IsAccessTokenUsable_ShouldBeFalse_WhenExpired()
    {
        StoredSession.IsAccessTokenUsable(Now.AddMinutes(-1), Now).Should().BeFalse();
    }

    [Test]
    public void IsAccessTokenUsable_ShouldBeFalse_WhenInsideReuseSkew()
    {
        StoredSession.IsAccessTokenUsable(Now.AddSeconds(30), Now).Should().BeFalse();
    }

    [Test]
    public void IsAccessTokenUsable_ShouldBeTrue_WhenBeyondReuseSkew()
    {
        StoredSession.IsAccessTokenUsable(Now.AddMinutes(2), Now).Should().BeTrue();
    }

    [Test]
    public void IsAccessTokenNearExpiry_ShouldBeTrue_WhenExpiryUnknown()
    {
        StoredSession.IsAccessTokenNearExpiry(null, Now, TimeSpan.FromMinutes(10)).Should().BeTrue();
    }

    [Test]
    public void IsAccessTokenNearExpiry_ShouldBeTrue_WhenInsideSkew()
    {
        StoredSession.IsAccessTokenNearExpiry(Now.AddMinutes(5), Now, TimeSpan.FromMinutes(10)).Should().BeTrue();
    }

    [Test]
    public void IsAccessTokenNearExpiry_ShouldBeFalse_WhenBeyondSkew()
    {
        StoredSession.IsAccessTokenNearExpiry(Now.AddHours(1), Now, TimeSpan.FromMinutes(10)).Should().BeFalse();
    }

    [Test]
    public void DeserializeClaims_ShouldRoundTrip_WhenSerialized()
    {
        var claims = new[]
        {
            new Claim("sub", "user-1"),
            new Claim("name", "Alice"),
            new Claim("role", "Admin"),
            new Claim("role", "User")
        };

        var restored = StoredSession.DeserializeClaims(StoredSession.SerializeClaims(claims));

        restored.Select(c => (c.Type, c.Value))
            .Should().Equal(claims.Select(c => (c.Type, c.Value)));
    }

    [Test]
    public void DeserializeClaims_ShouldBeEmpty_WhenMissingOrCorrupt()
    {
        StoredSession.DeserializeClaims(null).Should().BeEmpty();
        StoredSession.DeserializeClaims("").Should().BeEmpty();
        StoredSession.DeserializeClaims("{not json").Should().BeEmpty();
        StoredSession.DeserializeClaims("[{\"value\":\"x\"}]").Should().BeEmpty();
    }
}
