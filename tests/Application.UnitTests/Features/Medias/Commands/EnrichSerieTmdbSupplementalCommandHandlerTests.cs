using K7.Server.Application.Common.Services;
using K7.Server.Application.Features.Medias.Commands.EnrichSerieTmdbSupplemental;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Metadatas;
using K7.Server.Domain.Entities.Metadatas.External;
using K7.Server.Domain.Entities.Metadatas.PersonRoles;
using K7.Server.Domain.Entities.Ratings;
using K7.Server.Domain.Enums;
using K7.Server.Domain.Interfaces;
using K7.Server.Infrastructure.Database.Context.Data;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace K7.Server.Application.UnitTests.Features.Medias.Commands;

[TestFixture]
public class EnrichSerieTmdbSupplementalCommandHandlerTests
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private ServiceProvider _serviceProviderRoot = null!;
    private ISerieMetadataProvider _tmdbProvider = null!;
    private EnrichSerieTmdbSupplementalCommandHandler _handler = null!;

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

        _tmdbProvider = Substitute.For<ISerieMetadataProvider>();
        _tmdbProvider.ProviderName.Returns("tmdb");

        var services = new ServiceCollection();
        services.AddKeyedSingleton("tmdb", _tmdbProvider);
        _serviceProviderRoot = services.BuildServiceProvider();

        var castService = new SerieSupplementalCastEnrichmentService(
            _context,
            Substitute.For<ISender>(),
            Substitute.For<ITvdbPersonLinkProvider>());

        _handler = new EnrichSerieTmdbSupplementalCommandHandler(
            _context,
            _serviceProviderRoot,
            Substitute.For<ISender>(),
            castService);
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProviderRoot.Dispose();
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task Handle_ShouldApplyEpisodePersonRoles_WhenEpisodeHasNoCast()
    {
        var serie = new Serie { Title = "Cool Show" };
        serie.ExternalIds.Add(new ExternalId { ProviderName = "tmdb", Value = "1396" });

        var season = new SerieSeason { Serie = serie, SeasonNumber = 1, Title = "Season 1" };
        var episode = new SerieEpisode
        {
            Serie = serie,
            Season = season,
            EpisodeNumber = 1,
            Title = "Pilot",
            Overview = "Already enriched"
        };
        episode.ExternalIds.Add(new ExternalId { ProviderName = "tvdb", Value = "ep-1" });
        season.Episodes.Add(episode);
        serie.Seasons.Add(season);

        _context.Medias.Add(serie);
        await _context.SaveChangesAsync();

        _tmdbProvider.FetchSerieMetadataAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>())
            .Returns(new ExternalSerieMetadata { Title = "Cool Show" });

        _tmdbProvider.FetchEpisodeMetadataAsync(
                Arg.Any<string>(),
                1,
                1,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>())
            .Returns(new ExternalEpisodeMetadata
            {
                SeasonNumber = 1,
                EpisodeNumber = 1,
                Title = "Pilot",
                PersonRoles =
                [
                    new Actor
                    {
                        CharacterName = "Walter",
                        Order = 0,
                        Person = new Person
                        {
                            Name = "Bryan Cranston",
                            ExternalIds = [new ExternalId { ProviderName = "tmdb", Value = "17419" }]
                        }
                    }
                ]
            });

        await _handler.Handle(new EnrichSerieTmdbSupplementalCommand
        {
            MediaId = serie.Id,
            Language = "fr",
            FallbackLanguage = "en"
        }, CancellationToken.None);

        var refreshed = await _context.Medias.OfType<SerieEpisode>()
            .Include(e => e.PersonRoles)
                .ThenInclude(r => r.Person)
            .SingleAsync(e => e.Id == episode.Id);

        refreshed.PersonRoles.Should().ContainSingle();
        refreshed.PersonRoles.Single().Person.Name.Should().Be("Bryan Cranston");
    }

    [Test]
    public async Task Handle_ShouldNotReplaceEpisodePersonRoles_WhenCastAlreadyPresent()
    {
        var serie = new Serie { Title = "Cool Show" };
        serie.ExternalIds.Add(new ExternalId { ProviderName = "tmdb", Value = "1396" });

        var season = new SerieSeason { Serie = serie, SeasonNumber = 1, Title = "Season 1" };
        var episode = new SerieEpisode
        {
            Serie = serie,
            Season = season,
            EpisodeNumber = 1,
            Title = "Pilot",
            Overview = "Already enriched"
        };
        episode.ExternalIds.Add(new ExternalId { ProviderName = "tvdb", Value = "ep-1" });
        episode.PersonRoles.Add(new Actor
        {
            CharacterName = "Existing",
            Person = new Person
            {
                Name = "Keep Me",
                ExternalIds = [new ExternalId { ProviderName = "tmdb", Value = "1" }]
            }
        });
        episode.Pictures.Add(new MetadataPicture
        {
            Type = MetadataPictureType.Still,
            OriginalRemoteUri = new Uri("https://img.example/still.jpg"),
            OriginalWidth = 1920,
            OriginalHeight = 1080
        });
        episode.Ratings.Add(new MetadataProviderRating
        {
            MetadataProvider = MetadataProvider.TMDb,
            Value = 8,
            MinimumValue = 0,
            MaximumValue = 10,
            RatingCount = 10
        });
        season.Episodes.Add(episode);
        serie.Seasons.Add(season);

        _context.Medias.Add(serie);
        await _context.SaveChangesAsync();

        _tmdbProvider.FetchSerieMetadataAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>())
            .Returns(new ExternalSerieMetadata { Title = "Cool Show" });

        await _handler.Handle(new EnrichSerieTmdbSupplementalCommand
        {
            MediaId = serie.Id,
            Language = "fr",
            FallbackLanguage = "en"
        }, CancellationToken.None);

        var refreshed = await _context.Medias.OfType<SerieEpisode>()
            .Include(e => e.PersonRoles)
                .ThenInclude(r => r.Person)
            .SingleAsync(e => e.Id == episode.Id);

        refreshed.PersonRoles.Should().ContainSingle();
        refreshed.PersonRoles.Single().Person.Name.Should().Be("Keep Me");

        await _tmdbProvider.DidNotReceive().FetchEpisodeMetadataAsync(
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
    }
}
