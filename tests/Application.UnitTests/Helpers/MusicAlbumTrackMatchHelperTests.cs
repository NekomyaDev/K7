using K7.Server.Application.Helpers;
using K7.Server.Domain.Entities.Medias;

namespace K7.Server.Application.UnitTests.Helpers;

[TestFixture]
public class MusicAlbumTrackMatchHelperTests
{
    [Test]
    public void FindOnAlbum_ShouldPreferDiscAndTrack_OverSameTitleOnOtherDisc()
    {
        var disc1 = Track("Goodbye Stranger", disc: 1, track: 3);
        var disc2 = Track("Goodbye Stranger", disc: 2, track: 2);
        var albumTracks = new[] { disc1, disc2 };

        var hit = MusicAlbumTrackMatchHelper.FindOnAlbum(
            albumTracks,
            title: "Goodbye Stranger",
            trackNumber: 3,
            discNumber: 1);

        hit.Should().BeSameAs(disc1);
    }

    [Test]
    public void FindOnAlbum_ShouldMatchTitleOnlyOnSameDisc_WhenTrackNumberMissing()
    {
        var disc1 = Track("Goodbye Stranger", disc: 1, track: 3);
        var disc2 = Track("Goodbye Stranger", disc: 2, track: 2);

        var hit = MusicAlbumTrackMatchHelper.FindOnAlbum(
            [disc1, disc2],
            title: "Goodbye Stranger",
            trackNumber: null,
            discNumber: 2);

        hit.Should().BeSameAs(disc2);
    }

    [Test]
    public void FindOnAlbum_ShouldNotMatchTitleAcrossDiscs()
    {
        var disc1 = Track("The Logical Song", disc: 1, track: 2);
        var disc2 = Track("Oh Darling", disc: 2, track: 4);

        var hit = MusicAlbumTrackMatchHelper.FindOnAlbum(
            [disc1, disc2],
            title: "The Logical Song",
            trackNumber: null,
            discNumber: 2);

        hit.Should().BeNull();
    }

    [Test]
    public void FindMetadataTrack_ShouldNotStampDisc1RecordingOntoDisc2Title()
    {
        var meta = new[]
        {
            new Meta(1, 3, "Goodbye Stranger", "rec-studio"),
            new Meta(2, 2, "Goodbye Stranger", "rec-live"),
        };

        var hit = MusicAlbumTrackMatchHelper.FindMetadataTrack(
            meta,
            discNumber: 2,
            trackNumber: 2,
            title: "Goodbye Stranger",
            getDisc: m => m.Disc,
            getTrack: m => m.Track,
            getTitle: m => m.Title);

        hit.Should().NotBeNull();
        hit!.RecordingId.Should().Be("rec-live");
    }

    [Test]
    public void ShouldSplitFusedFormerTrack_ShouldBeTrue_WhenOtherFileOwnsFormerAndSlotDiffers()
    {
        MusicAlbumTrackMatchHelper.ShouldSplitFusedFormerTrack(
                hasOtherSlotMatch: false,
                otherFilesOnFormer: true,
                formerSlotMatchesResolved: false,
                hasTrackNumber: true)
            .Should().BeTrue();
    }

    [Test]
    public void ShouldSplitFusedFormerTrack_ShouldBeFalse_WhenFormerSlotStillMatches()
    {
        MusicAlbumTrackMatchHelper.ShouldSplitFusedFormerTrack(
                hasOtherSlotMatch: false,
                otherFilesOnFormer: true,
                formerSlotMatchesResolved: true,
                hasTrackNumber: true)
            .Should().BeFalse();
    }

    [Test]
    public void CreateMissingTrackShells_ShouldAddDisc2Slots_WhenOnlyDisc1Exists()
    {
        var albumId = Guid.NewGuid();
        var existing = new[]
        {
            Track("Gone Hollywood", disc: 1, track: 1),
            Track("Goodbye Stranger", disc: 1, track: 3),
        };

        var shells = MusicAlbumTrackMatchHelper.CreateMissingTrackShells(
            albumId,
            existing,
            [
                (1, 1, "Gone Hollywood", null),
                (1, 3, "Goodbye Stranger", null),
                (2, 2, "Goodbye Stranger", null),
                (2, 12, "Child of Vision", null),
            ]);

        shells.Should().HaveCount(2);
        shells.Should().Contain(t => t.DiscNumber == 2 && t.TrackNumber == 2 && t.Title == "Goodbye Stranger");
        shells.Should().Contain(t => t.DiscNumber == 2 && t.TrackNumber == 12);
        shells.Should().OnlyContain(t => t.AlbumId == albumId);
    }

    private static MusicTrack Track(string title, int disc, int track) =>
        new()
        {
            Title = title,
            DiscNumber = disc,
            TrackNumber = track,
            AlbumId = Guid.NewGuid(),
            IndexedFiles = []
        };

    private sealed record Meta(int Disc, int Track, string Title, string RecordingId);
}
