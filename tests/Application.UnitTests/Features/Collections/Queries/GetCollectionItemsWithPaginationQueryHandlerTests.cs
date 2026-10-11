using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Common.Models;
using K7.Server.Application.Features.Collections.Queries.GetCollectionItems;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Collections;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Users;
using K7.Server.Domain.Enums;
using K7.Server.Infrastructure.Database.Context.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace K7.Server.Application.UnitTests.Features.Collections.Queries;

[TestFixture]
public class GetCollectionItemsWithPaginationQueryHandlerTests
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private IUser _currentUser = null!;
    private GetCollectionItemsWithPaginationQueryHandler _handler = null!;
    private Guid _userId;
    private Guid _libraryId;
    private Guid _collectionId;
    private Guid _availableMediaId;
    private Guid _unavailableMediaId;

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

        _userId = Guid.NewGuid();
        _libraryId = Guid.NewGuid();
        _collectionId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        _context.Users.Add(new User { Id = _userId, DisplayName = "owner" });
        _context.LibraryGroups.Add(new LibraryGroup
        {
            Id = groupId,
            Title = "Movies",
            MediaType = LibraryMediaType.Movie
        });
        _context.Libraries.Add(new Library
        {
            Id = _libraryId,
            LibraryGroupId = groupId,
            Title = "Lib",
            MediaType = LibraryMediaType.Movie,
            RootPath = "/media",
            MetadataProviderName = "tmdb",
            MetadataLanguage = "fr",
            MetadataFallbackLanguage = "en"
        });

        _availableMediaId = SeedMovie("Available", withFile: true).Id;
        _unavailableMediaId = SeedMovie("Missing", withFile: false).Id;

        _context.Collections.Add(new Collection
        {
            Id = _collectionId,
            Title = "Manual",
            UserId = _userId,
            MediaType = MediaType.Movie
        });
        _context.CollectionItems.AddRange(
            new CollectionItem
            {
                Id = Guid.NewGuid(),
                CollectionId = _collectionId,
                MediaId = _availableMediaId,
                Order = 0
            },
            new CollectionItem
            {
                Id = Guid.NewGuid(),
                CollectionId = _collectionId,
                MediaId = _unavailableMediaId,
                Order = 1
            });
        _context.SaveChanges();

        _currentUser = Substitute.For<IUser>();
        _currentUser.Id.Returns(_userId);
        _handler = new GetCollectionItemsWithPaginationQueryHandler(_context, _currentUser);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task Handle_ShouldHideUnavailable_WhenIncludeUnavailableIsFalse()
    {
        var result = await _handler.Handle(new GetCollectionItemsWithPaginationQuery
        {
            CollectionId = _collectionId,
            PageNumber = 1,
            PageSize = 50,
            IncludeUnavailable = false
        }, CancellationToken.None);

        result.Items.Should().ContainSingle().Which.MediaId.Should().Be(_availableMediaId);
    }

    [Test]
    public async Task Handle_ShouldIncludeUnavailable_WhenIncludeUnavailableIsTrue()
    {
        var result = await _handler.Handle(new GetCollectionItemsWithPaginationQuery
        {
            CollectionId = _collectionId,
            PageNumber = 1,
            PageSize = 50,
            IncludeUnavailable = true
        }, CancellationToken.None);

        result.Items.Select(i => i.MediaId).Should().BeEquivalentTo(
            [_availableMediaId, _unavailableMediaId]);
    }

    [Test]
    public async Task Handle_ShouldAttachCurrentUserWatchState()
    {
        var otherUserId = Guid.NewGuid();
        _context.Users.Add(new User { Id = otherUserId, DisplayName = "other" });
        _context.UserMediaStates.AddRange(
            new UserMediaState
            {
                UserId = _userId,
                MediaId = _availableMediaId,
                IsCompleted = true,
                PlayCount = 2
            },
            new UserMediaState
            {
                UserId = otherUserId,
                MediaId = _availableMediaId,
                IsCompleted = false
            });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetCollectionItemsWithPaginationQuery
        {
            CollectionId = _collectionId,
            PageNumber = 1,
            PageSize = 50,
            IncludeUnavailable = false
        }, CancellationToken.None);

        var state = result.Items.Should().ContainSingle().Which.Media.UserMediaStates.Should().ContainSingle().Subject;
        state.UserId.Should().Be(_userId);
        state.IsCompleted.Should().BeTrue();
    }

    [Test]
    public async Task Handle_ShouldMarkSerieAndSeasonWatched_WhenAllEpisodesAreCompleted()
    {
        var (collectionId, serieId, seasonId) = SeedSerieCollection(episodesCompleted: 2, serieMarkedCompleted: false);

        var result = await LoadCollectionAsync(collectionId);

        Watched(result, serieId).Should().BeTrue();
        Watched(result, seasonId).Should().BeTrue();
    }

    [Test]
    public async Task Handle_ShouldNotMarkSerieWatched_WhenAnEpisodeRemains()
    {
        var (collectionId, serieId, _) = SeedSerieCollection(episodesCompleted: 1, serieMarkedCompleted: true);

        var result = await LoadCollectionAsync(collectionId);

        Watched(result, serieId).Should().BeFalse();
    }

    [Test]
    public async Task Handle_ShouldIncludeSerieAndSeason_WhenLibraryAvailabilityExists()
    {
        var serieId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var missingSerieId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();

        _context.Medias.AddRange(
            new Serie { Id = serieId, Title = "Show", SortTitle = "Show" },
            new SerieSeason
            {
                Id = seasonId,
                SerieId = serieId,
                SeasonNumber = 1,
                Title = "S1",
                SortTitle = "S1"
            },
            new Serie { Id = missingSerieId, Title = "Missing", SortTitle = "Missing" });
        _context.MediaLibraryAvailabilities.AddRange(
            new MediaLibraryAvailability { LibraryId = _libraryId, MediaId = serieId },
            new MediaLibraryAvailability { LibraryId = _libraryId, MediaId = seasonId });
        _context.Collections.Add(new Collection
        {
            Id = collectionId,
            Title = "Shows",
            UserId = _userId,
            MediaType = MediaType.Serie
        });
        _context.CollectionItems.AddRange(
            new CollectionItem { Id = Guid.NewGuid(), CollectionId = collectionId, MediaId = serieId, Order = 0 },
            new CollectionItem { Id = Guid.NewGuid(), CollectionId = collectionId, MediaId = seasonId, Order = 1 },
            new CollectionItem { Id = Guid.NewGuid(), CollectionId = collectionId, MediaId = missingSerieId, Order = 2 });
        await _context.SaveChangesAsync();

        var result = await _handler.Handle(new GetCollectionItemsWithPaginationQuery
        {
            CollectionId = collectionId,
            PageNumber = 1,
            PageSize = 50,
            IncludeUnavailable = false
        }, CancellationToken.None);

        result.Items.Select(i => i.MediaId).Should().BeEquivalentTo([serieId, seasonId]);
    }

    private (Guid CollectionId, Guid SerieId, Guid SeasonId) SeedSerieCollection(
        int episodesCompleted,
        bool serieMarkedCompleted)
    {
        var serieId = Guid.NewGuid();
        var seasonId = Guid.NewGuid();
        var collectionId = Guid.NewGuid();
        var serie = new Serie { Id = serieId, Title = "Show", SortTitle = "Show" };
        var season = new SerieSeason
        {
            Id = seasonId,
            SerieId = serieId,
            Serie = serie,
            SeasonNumber = 1,
            Title = "S1",
            SortTitle = "S1"
        };
        serie.Seasons.Add(season);

        var episodes = new List<SerieEpisode>();
        for (var number = 1; number <= 2; number++)
        {
            var episode = new SerieEpisode
            {
                Id = Guid.NewGuid(),
                SerieId = serieId,
                Serie = serie,
                SeasonId = seasonId,
                Season = season,
                EpisodeNumber = number,
                Title = $"E{number}",
                SortTitle = $"E{number}"
            };
            season.Episodes.Add(episode);
            episodes.Add(episode);
        }

        _context.Medias.AddRange(serie, season);
        _context.Medias.AddRange(episodes);
        _context.Collections.Add(new Collection
        {
            Id = collectionId,
            Title = "Shows",
            UserId = _userId,
            MediaType = MediaType.Serie
        });
        _context.CollectionItems.AddRange(
            new CollectionItem
            {
                Id = Guid.NewGuid(),
                CollectionId = collectionId,
                MediaId = serieId,
                Order = 0
            },
            new CollectionItem
            {
                Id = Guid.NewGuid(),
                CollectionId = collectionId,
                MediaId = seasonId,
                Order = 1
            });

        if (serieMarkedCompleted)
        {
            _context.UserMediaStates.Add(new UserMediaState
            {
                UserId = _userId,
                MediaId = serieId,
                IsCompleted = true
            });
        }

        foreach (var episode in episodes.Take(episodesCompleted))
        {
            _context.UserMediaStates.Add(new UserMediaState
            {
                UserId = _userId,
                MediaId = episode.Id,
                IsCompleted = true
            });
        }

        _context.SaveChanges();
        return (collectionId, serieId, seasonId);
    }

    private Task<PaginatedList<CollectionItem>> LoadCollectionAsync(Guid collectionId) =>
        _handler.Handle(new GetCollectionItemsWithPaginationQuery
        {
            CollectionId = collectionId,
            PageNumber = 1,
            PageSize = 50,
            IncludeUnavailable = true
        }, CancellationToken.None);

    private static bool Watched(PaginatedList<CollectionItem> result, Guid mediaId) =>
        result.Items.Single(item => item.MediaId == mediaId).Media.UserMediaStates.Single().IsCompleted;

    private Movie SeedMovie(string title, bool withFile)
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Title = title
        };
        if (withFile)
        {
            movie.IndexedFiles.Add(new IndexedFile
            {
                Id = Guid.NewGuid(),
                LibraryId = _libraryId,
                MediaId = movie.Id,
                Name = title,
                Extension = ".mkv",
                Path = $"/media/{title}.mkv",
                Hash = 1,
                Size = 1
            });
            _context.MediaLibraryAvailabilities.Add(new MediaLibraryAvailability
            {
                LibraryId = _libraryId,
                MediaId = movie.Id
            });
        }

        _context.Medias.Add(movie);
        return movie;
    }
}
