using K7.Server.Application.Common.Configuration;
using K7.Server.Application.Features.Diagnostics.Services;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Enums;
using K7.Server.Infrastructure.Database.Context.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace K7.Server.Application.UnitTests.Features.Diagnostics.Services;

[TestFixture]
public class DiagnosticIssueEntityResolverTests
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private DiagnosticIssueEntityResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new ApplicationDbContext(options);
        _context.Database.EnsureCreated();
        _resolver = new DiagnosticIssueEntityResolver(
            _context,
            Options.Create(new PathsConfiguration { Metadatas = Path.GetTempPath() }));
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task ResolveEntityIdsAsync_ShouldIncludeArtistsMissingFormationDate()
    {
        var libraryId = SeedMusicLibrary();
        var missingId = Guid.NewGuid();
        var datedId = Guid.NewGuid();
        var unlinkedId = Guid.NewGuid();
        var missingAlbumId = Guid.NewGuid();
        var datedAlbumId = Guid.NewGuid();

        _context.Medias.AddRange(
            new MusicArtist { Id = missingId, Title = "No Date" },
            new MusicArtist { Id = datedId, Title = "Dated", ReleaseDate = new DateOnly(1990, 5, 1) },
            new MusicArtist { Id = unlinkedId, Title = "No File" },
            new MusicAlbum { Id = missingAlbumId, Title = "A", ArtistId = missingId },
            new MusicAlbum { Id = datedAlbumId, Title = "B", ArtistId = datedId });
        AddMusicFile(libraryId, missingAlbumId, "/music/a/track.flac", 1);
        AddMusicFile(libraryId, datedAlbumId, "/music/b/track.flac", 2);
        await _context.SaveChangesAsync();

        var ids = await _resolver.ResolveEntityIdsAsync(
            DiagnosticIssue.MissingMetadata, libraryId: null, CancellationToken.None);

        ids.Should().BeEquivalentTo([missingId]);
    }

    [Test]
    public async Task ResolveEntityIdsAsync_ShouldLimitArtistsMissingFormationDate_ToTheLibrary()
    {
        var libraryId = SeedMusicLibrary();
        var otherLibraryId = SeedMusicLibrary();
        var inLibraryId = Guid.NewGuid();
        var otherLibraryArtistId = Guid.NewGuid();
        var inLibraryAlbumId = Guid.NewGuid();
        var otherAlbumId = Guid.NewGuid();

        _context.Medias.AddRange(
            new MusicArtist { Id = inLibraryId, Title = "In Library" },
            new MusicArtist { Id = otherLibraryArtistId, Title = "Other Library" },
            new MusicAlbum { Id = inLibraryAlbumId, Title = "A", ArtistId = inLibraryId },
            new MusicAlbum { Id = otherAlbumId, Title = "B", ArtistId = otherLibraryArtistId });
        AddMusicFile(libraryId, inLibraryAlbumId, "/music/a/track.flac", 1);
        AddMusicFile(otherLibraryId, otherAlbumId, "/music/b/track.flac", 2);
        await _context.SaveChangesAsync();

        var ids = await _resolver.ResolveEntityIdsAsync(
            DiagnosticIssue.MissingMetadata, libraryId, CancellationToken.None);

        ids.Should().BeEquivalentTo([inLibraryId]);
    }

    private Guid SeedMusicLibrary()
    {
        var groupId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        _context.LibraryGroups.Add(new LibraryGroup
        {
            Id = groupId,
            Title = "Music",
            MediaType = LibraryMediaType.Music
        });
        _context.Libraries.Add(new Library
        {
            Id = libraryId,
            LibraryGroupId = groupId,
            Title = "Music",
            MediaType = LibraryMediaType.Music,
            RootPath = "/music",
            MetadataProviderName = "musicbrainz",
            MetadataLanguage = "fr",
            MetadataFallbackLanguage = "en"
        });
        _context.SaveChanges();
        return libraryId;
    }

    private void AddMusicFile(Guid libraryId, Guid albumId, string path, uint hash)
    {
        _context.IndexedFiles.Add(new IndexedFile
        {
            Id = Guid.NewGuid(),
            LibraryId = libraryId,
            MediaId = albumId,
            Name = Path.GetFileNameWithoutExtension(path),
            Extension = Path.GetExtension(path),
            Path = path,
            ParentDirectory = Path.GetDirectoryName(path),
            Hash = hash,
            Size = 1
        });
    }
}
