using K7.Server.Application.Helpers;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Metadatas;
using K7.Server.Domain.Entities.Metadatas.PersonRoles;
using K7.Server.Domain.Enums;
using K7.Server.Infrastructure.Database.Context.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace K7.Server.Application.UnitTests.Helpers;

[TestFixture]
public class PersonRolePersonResolveHelperTests
{
    private SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;

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
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task ResolveAsync_ShouldReuseSameStub_WhenSameExternalIdAppearsManyTimesInBatch()
    {
        var roles = Enumerable.Range(0, 5)
            .Select(_ => new Actor
            {
                CharacterName = "Guest",
                Person = new Person
                {
                    Name = "Jane Doe",
                    ExternalIds =
                    [
                        new ExternalId { ProviderName = "tmdb", Value = "42" }
                    ]
                }
            })
            .Cast<BasePersonRole>()
            .ToList();

        await PersonRolePersonResolveHelper.ResolveAsync(_context, roles);

        roles.Select(role => role.Person).Distinct().Should().ContainSingle();
        roles[0].Person.ExternalIds.Should().ContainSingle(e => e.ProviderName == "tmdb" && e.Value == "42");
    }

    [Test]
    public async Task ResolveAsync_ShouldAttachToExistingDbPerson_WhenExternalIdMatches()
    {
        var existing = new Person
        {
            Id = Guid.NewGuid(),
            Name = "Jane Doe",
            Biography = "Known"
        };
        existing.ExternalIds.Add(new ExternalId { ProviderName = "tmdb", Value = "42", PersonId = existing.Id });
        _context.Persons.Add(existing);
        await _context.SaveChangesAsync();

        var role = new Actor
        {
            CharacterName = "Guest",
            Person = new Person
            {
                Name = "Jane Doe",
                ExternalIds = [new ExternalId { ProviderName = "tmdb", Value = "42" }]
            }
        };

        await PersonRolePersonResolveHelper.ResolveAsync(_context, [role]);

        role.Person.Should().BeSameAs(existing);
        role.Person.Biography.Should().Be("Known");
    }

    [Test]
    public async Task ResolveAsync_ShouldMatchByName_WhenNoExternalIdHit()
    {
        var existing = new Person { Id = Guid.NewGuid(), Name = "Only Name" };
        _context.Persons.Add(existing);
        await _context.SaveChangesAsync();

        var role = new Actor
        {
            CharacterName = "X",
            Person = new Person { Name = "Only Name" }
        };

        await PersonRolePersonResolveHelper.ResolveAsync(_context, [role]);

        role.Person.Should().BeSameAs(existing);
    }

    [Test]
    public void PreferPersonLevelPortraits_ShouldMoveRolePortrait_WhenPersonHasNone()
    {
        var personId = Guid.NewGuid();
        var portrait = new MetadataPicture
        {
            Id = Guid.NewGuid(),
            Type = MetadataPictureType.Portrait,
            OriginalRemoteUri = new Uri("https://image.tmdb.org/t/p/original/a.jpg")
        };
        var role = new Actor
        {
            CharacterName = "Hero",
            PortraitPicture = portrait,
            Person = new Person { Id = personId, Name = "Actor" }
        };

        PersonRolePersonResolveHelper.PreferPersonLevelPortraits([role]);

        role.PortraitPicture.Should().BeNull();
        role.Person.PortraitPicture.Should().BeSameAs(portrait);
        portrait.PersonId.Should().Be(personId);
    }

    [Test]
    public void PreferPersonLevelPortraits_ShouldDropRolePortrait_WhenPersonAlreadyHasOne()
    {
        var personPortrait = new MetadataPicture
        {
            Id = Guid.NewGuid(),
            Type = MetadataPictureType.Portrait,
            OriginalRemoteUri = new Uri("https://image.tmdb.org/t/p/original/person.jpg")
        };
        var rolePortrait = new MetadataPicture
        {
            Id = Guid.NewGuid(),
            Type = MetadataPictureType.Portrait,
            OriginalRemoteUri = new Uri("https://image.tmdb.org/t/p/original/role.jpg")
        };
        var role = new Actor
        {
            CharacterName = "Hero",
            PortraitPicture = rolePortrait,
            Person = new Person
            {
                Id = Guid.NewGuid(),
                Name = "Actor",
                PortraitPicture = personPortrait
            }
        };

        PersonRolePersonResolveHelper.PreferPersonLevelPortraits([role]);

        role.PortraitPicture.Should().BeNull();
        role.Person.PortraitPicture.Should().BeSameAs(personPortrait);
    }

    [Test]
    public void PreferPersonLevelPortraits_ShouldKeepFirstPortrait_WhenSamePersonOnManyRoles()
    {
        var person = new Person { Id = Guid.NewGuid(), Name = "Guest" };
        var first = new MetadataPicture
        {
            Id = Guid.NewGuid(),
            Type = MetadataPictureType.Portrait,
            OriginalRemoteUri = new Uri("https://image.tmdb.org/t/p/original/1.jpg")
        };
        var second = new MetadataPicture
        {
            Id = Guid.NewGuid(),
            Type = MetadataPictureType.Portrait,
            OriginalRemoteUri = new Uri("https://image.tmdb.org/t/p/original/2.jpg")
        };
        var roles = new BasePersonRole[]
        {
            new Actor { CharacterName = "A", PortraitPicture = first, Person = person },
            new Actor { CharacterName = "B", PortraitPicture = second, Person = person }
        };

        PersonRolePersonResolveHelper.PreferPersonLevelPortraits(roles);

        roles[0].PortraitPicture.Should().BeNull();
        roles[1].PortraitPicture.Should().BeNull();
        person.PortraitPicture.Should().BeSameAs(first);
        first.PersonId.Should().Be(person.Id);
    }
}
