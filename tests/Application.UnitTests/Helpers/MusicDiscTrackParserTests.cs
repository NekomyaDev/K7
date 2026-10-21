using K7.Server.Application.Helpers;

namespace K7.Server.Application.UnitTests.Helpers;

[TestFixture]
public class MusicDiscTrackParserTests
{
    [TestCase("0101 - Gone Hollywood.flac", 1, 1, "Gone Hollywood")]
    [TestCase("0201-The Logical Song.mp3", 2, 1, "The Logical Song")]
    [TestCase("0212 - Child of Vision.flac", 2, 12, "Child of Vision")]
    public void TryParseFileName_ShouldParseDdtt(string fileName, int disc, int track, string title)
    {
        var parsed = MusicDiscTrackParser.TryParseFileName(fileName);

        parsed.Should().NotBeNull();
        parsed!.DiscNumber.Should().Be(disc);
        parsed.TrackNumber.Should().Be(track);
        parsed.TitleRemainder.Should().Be(title);
    }

    [TestCase("1-06 - Take the Long Way Home.flac", 1, 6, "Take the Long Way Home")]
    [TestCase("2.01 Title.flac", 2, 1, "Title")]
    [TestCase("1_06-Song.flac", 1, 6, "Song")]
    public void TryParseFileName_ShouldParseDiscDashTrack(string fileName, int disc, int track, string title)
    {
        var parsed = MusicDiscTrackParser.TryParseFileName(fileName);

        parsed.Should().NotBeNull();
        parsed!.DiscNumber.Should().Be(disc);
        parsed.TrackNumber.Should().Be(track);
        parsed.TitleRemainder.Should().Be(title);
    }

    [TestCase("Disc 2 - 03 - Title.flac", 2, 3, "Title")]
    [TestCase("CD2 Track 01 Song.flac", 2, 1, "Song")]
    [TestCase("Disk 1 - Only Title.flac", 1, null, "Only Title")]
    public void TryParseFileName_ShouldParseDiscLabel(string fileName, int disc, int? track, string title)
    {
        var parsed = MusicDiscTrackParser.TryParseFileName(fileName);

        parsed.Should().NotBeNull();
        parsed!.DiscNumber.Should().Be(disc);
        parsed.TrackNumber.Should().Be(track);
        parsed.TitleRemainder.Should().Be(title);
    }

    [TestCase("01 - Gone Hollywood.flac", 1, "Gone Hollywood")]
    [TestCase("12. Child of Vision.flac", 12, "Child of Vision")]
    public void TryParseFileName_ShouldParseTrackOnly(string fileName, int track, string title)
    {
        var parsed = MusicDiscTrackParser.TryParseFileName(fileName);

        parsed.Should().NotBeNull();
        parsed!.DiscNumber.Should().BeNull();
        parsed.TrackNumber.Should().Be(track);
        parsed.TitleRemainder.Should().Be(title);
    }

    [TestCase("Disc 2", 2)]
    [TestCase("CD1", 1)]
    [TestCase("Disk 02", 2)]
    [TestCase("Breakfast in America", null)]
    public void TryParseDiscFromDirectory_ShouldParseDiscFolders(string folder, int? expected)
    {
        MusicDiscTrackParser.TryParseDiscFromDirectory(folder).Should().Be(expected);
    }

    [Test]
    public void Resolve_ShouldPreferCompleteTags_OverFilenameDdtt()
    {
        // Complete embedded tags are authoritative when both disc and track are set.
        var (disc, track) = MusicDiscTrackParser.Resolve(
            tagDisc: 1,
            tagTrack: 3,
            fileName: "0202 - Goodbye Stranger.flac",
            parentDirectory: "Breakfast in America");

        disc.Should().Be(1);
        track.Should().Be(3);
    }

    [Test]
    public void Resolve_ShouldPreferDiscFolder_WhenTagDiscConflicts()
    {
        // Tags often leave deluxe live discs as disc 1. Disc N folder wins.
        var (disc, track) = MusicDiscTrackParser.Resolve(
            tagDisc: 1,
            tagTrack: 3,
            fileName: "02 - Goodbye Stranger.flac",
            parentDirectory: "Disc 2");

        disc.Should().Be(2);
        track.Should().Be(2);
    }

    [Test]
    public void Resolve_ShouldKeepTagTrack_WhenDiscFolderConflictsAndFilenameHasNoTrack()
    {
        var (disc, track) = MusicDiscTrackParser.Resolve(
            tagDisc: 1,
            tagTrack: 5,
            fileName: "Goodbye Stranger.flac",
            parentDirectory: "Disc 2");

        disc.Should().Be(2);
        track.Should().Be(5);
    }

    [Test]
    public void Resolve_ShouldPreferTags_WhenFilenameHasTrackOnly()
    {
        var (disc, track) = MusicDiscTrackParser.Resolve(
            tagDisc: 1,
            tagTrack: 3,
            fileName: "03 - Goodbye Stranger.flac",
            parentDirectory: null);

        disc.Should().Be(1);
        track.Should().Be(3);
    }

    [Test]
    public void Resolve_ShouldUseDiscFolderAndFilenameTrack_WhenTagsIncomplete()
    {
        var (disc, track) = MusicDiscTrackParser.Resolve(
            tagDisc: null,
            tagTrack: null,
            fileName: "03 - Goodbye Stranger.flac",
            parentDirectory: "Disc 1");

        disc.Should().Be(1);
        track.Should().Be(3);
    }

    [Test]
    public void Resolve_ShouldUseFilenameDdtt_WhenTagDiscMissing()
    {
        var (disc, track) = MusicDiscTrackParser.Resolve(
            tagDisc: null,
            tagTrack: 99,
            fileName: "1-05 - Oh Darling.flac",
            parentDirectory: null);

        disc.Should().Be(1);
        track.Should().Be(5);
    }

    [Test]
    public void Resolve_ShouldUseFilename_WhenTagsMissing()
    {
        var (disc, track) = MusicDiscTrackParser.Resolve(
            tagDisc: null,
            tagTrack: null,
            fileName: "0206 - Take the Long Way Home.flac",
            parentDirectory: "Breakfast in America");

        disc.Should().Be(2);
        track.Should().Be(6);
    }

    [Test]
    public void Resolve_ShouldSplitPackedTagTrack_WhenDiscMissing()
    {
        var (disc, track) = MusicDiscTrackParser.Resolve(
            tagDisc: null,
            tagTrack: 201,
            fileName: "whatever.flac",
            parentDirectory: null);

        disc.Should().Be(2);
        track.Should().Be(1);
    }

    [Test]
    public void Resolve_ShouldUseParentDiscFolder_WhenOnlyTrackInName()
    {
        var (disc, track) = MusicDiscTrackParser.Resolve(
            tagDisc: null,
            tagTrack: null,
            fileName: "03 - Song.flac",
            parentDirectory: "Disc 2");

        disc.Should().Be(2);
        track.Should().Be(3);
    }
}
