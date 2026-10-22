using K7.Clients.Shared.Helpers;
using K7.Shared.Interfaces;

namespace K7.Clients.ComponentTests.Helpers;

[TestFixture]
public class MediaPictureUrlHelperTests
{
    [Test]
    public void ToDisplayUrl_ShouldPrefixServerBase_WhenRelativeMetadataPicturePath()
    {
        var apiClient = Substitute.For<IK7ServerService>();
        apiClient.GetAbsoluteUri("/api/metadata-pictures/abc?size=Small")
            .Returns(new Uri("https://k7.example/api/metadata-pictures/abc?size=Small"));

        var result = MediaPictureUrlHelper.ToDisplayUrl(apiClient, "/api/metadata-pictures/abc?size=Small");

        result.Should().Be("https://k7.example/api/metadata-pictures/abc?size=Small");
        apiClient.Received(1).GetAbsoluteUri("/api/metadata-pictures/abc?size=Small");
    }

    [Test]
    public void WithPictureSize_ShouldAppendSize_WhenMetadataPictureUrlHasNoQuery()
    {
        MediaPictureUrlHelper.WithPictureSize("/api/metadata-pictures/abc", "Small")
            .Should().Be("/api/metadata-pictures/abc?size=Small");
        MediaPictureUrlHelper.WithPictureSize("https://k7.example/api/metadata-pictures/abc", "Small")
            .Should().Be("https://k7.example/api/metadata-pictures/abc?size=Small");
    }

    [Test]
    public void WithPictureSize_ShouldKeepExistingQuery_AndNotOverrideSize()
    {
        MediaPictureUrlHelper.WithPictureSize("/api/metadata-pictures/abc?v=123", "Small")
            .Should().Be("/api/metadata-pictures/abc?v=123&size=Small");
        MediaPictureUrlHelper.WithPictureSize("/api/metadata-pictures/abc?size=Medium", "Small")
            .Should().Be("/api/metadata-pictures/abc?size=Medium");
        MediaPictureUrlHelper.WithPictureSize("/api/metadata-pictures/abc?v=1&size=Medium", "Small")
            .Should().Be("/api/metadata-pictures/abc?v=1&size=Medium");
    }

    [Test]
    public void WithPictureSize_ShouldLeaveOtherUrlsUnchanged()
    {
        MediaPictureUrlHelper.WithPictureSize("https://image.tmdb.org/t/p/w500/poster.jpg", "Small")
            .Should().Be("https://image.tmdb.org/t/p/w500/poster.jpg");
        MediaPictureUrlHelper.WithPictureSize("_content/K7.Clients.Shared.UI/images/person-placeholder.png", "Small")
            .Should().Be("_content/K7.Clients.Shared.UI/images/person-placeholder.png");
        MediaPictureUrlHelper.WithPictureSize(null, "Small").Should().BeNull();
        MediaPictureUrlHelper.WithPictureSize("", "Small").Should().Be("");
    }

    [Test]
    public void ToDisplayUrl_ShouldLeaveAbsoluteUrlUnchanged_WhenTmdbHttps()
    {
        var apiClient = Substitute.For<IK7ServerService>();
        const string tmdb = "https://image.tmdb.org/t/p/w500/poster.jpg";

        var result = MediaPictureUrlHelper.ToDisplayUrl(apiClient, tmdb);

        result.Should().Be(tmdb);
        apiClient.DidNotReceive().GetAbsoluteUri(Arg.Any<string?>());
    }

    [Test]
    public void ToDisplayUrl_ShouldLeaveStaticContentUnchanged_WhenBlazorAsset()
    {
        var apiClient = Substitute.For<IK7ServerService>();
        const string asset = "_content/K7.Clients.Shared.UI/images/person-placeholder.png";

        var result = MediaPictureUrlHelper.ToDisplayUrl(apiClient, asset);

        result.Should().Be(asset);
        apiClient.DidNotReceive().GetAbsoluteUri(Arg.Any<string?>());
    }

    [Test]
    public void ToDisplayUrl_ShouldLeaveOtherApiPathsUnchanged()
    {
        var apiClient = Substitute.For<IK7ServerService>();
        const string other = "/api/medias/abc/theme";

        var result = MediaPictureUrlHelper.ToDisplayUrl(apiClient, other);

        result.Should().Be(other);
        apiClient.DidNotReceive().GetAbsoluteUri(Arg.Any<string?>());
    }

    [Test]
    public void ToDisplayUrl_ShouldReturnNull_WhenUrlIsNull()
    {
        var apiClient = Substitute.For<IK7ServerService>();

        MediaPictureUrlHelper.ToDisplayUrl(apiClient, null).Should().BeNull();
        apiClient.DidNotReceive().GetAbsoluteUri(Arg.Any<string?>());
    }

    [Test]
    public void WithCacheBuster_ShouldLeaveUrlUnchanged_WhenVersionIsNull()
    {
        MediaPictureUrlHelper.WithCacheBuster("https://k7.local/p.jpg?size=Medium", null)
            .Should().Be("https://k7.local/p.jpg?size=Medium");
    }

    [Test]
    public void WithCacheBuster_ShouldAppendVersionQuery_WhenUrlHasSize()
    {
        var version = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        MediaPictureUrlHelper.WithCacheBuster("https://k7.local/p.jpg?size=Medium", version)
            .Should().Be("https://k7.local/p.jpg?size=Medium&v=1700000000000");
    }
}
