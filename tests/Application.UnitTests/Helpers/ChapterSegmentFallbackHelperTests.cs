using K7.Server.Application.Helpers;
using K7.Server.Domain.Entities.Metadatas.Files;
using K7.Server.Domain.Enums;

namespace K7.Server.Application.UnitTests.Helpers;

[TestFixture]
public class ChapterSegmentFallbackHelperTests
{
    [Test]
    public void TryCreateSegment_ShouldMapOpeningChapter_WhenInFirstQuarter()
    {
        var chapter = new ChapterMarker
        {
            Title = "Opening",
            StartSeconds = 0,
            EndSeconds = 90
        };

        var ok = ChapterSegmentFallbackHelper.TryCreateSegment(
            chapter, episodeDurationSeconds: 1400, MediaSegmentType.Intro,
            out var start, out var end);

        ok.Should().BeTrue();
        start.Should().Be(0);
        end.Should().Be(90);
    }

    [Test]
    public void TryCreateSegment_ShouldMapEndingChapter_WhenInLastQuarter()
    {
        var chapter = new ChapterMarker
        {
            Title = "Ending",
            StartSeconds = 1300,
            EndSeconds = 1380
        };

        var ok = ChapterSegmentFallbackHelper.TryCreateSegment(
            chapter, episodeDurationSeconds: 1400, MediaSegmentType.Outro,
            out var start, out var end);

        ok.Should().BeTrue();
        start.Should().Be(1300);
        end.Should().Be(1380);
    }

    [Test]
    public void TryCreateSegment_ShouldRejectOpening_WhenTooLateInEpisode()
    {
        var chapter = new ChapterMarker
        {
            Title = "Opening",
            StartSeconds = 800,
            EndSeconds = 890
        };

        var ok = ChapterSegmentFallbackHelper.TryCreateSegment(
            chapter, episodeDurationSeconds: 1400, MediaSegmentType.Intro,
            out _, out _);

        ok.Should().BeFalse();
    }

    [Test]
    public void TryCreateSegment_ShouldAcceptFrenchGeneriqueFin_AsOutro()
    {
        var chapter = new ChapterMarker
        {
            Title = "Générique de fin",
            StartSeconds = 1320,
            EndSeconds = 1390
        };

        var ok = ChapterSegmentFallbackHelper.TryCreateSegment(
            chapter, episodeDurationSeconds: 1400, MediaSegmentType.Outro,
            out _, out _);

        ok.Should().BeTrue();
    }
}
