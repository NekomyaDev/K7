using K7.Server.Application.Helpers;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Metadatas;
using K7.Server.Domain.Entities.Metadatas.PersonRoles;
using K7.Server.Domain.Enums;

namespace K7.Server.Application.UnitTests.Helpers;

[TestFixture]
public class PersonRolePortraitReuseHelperTests
{
    [Test]
    public void PreserveDownloadedPortraits_ShouldReuseLocalPortrait_WhenUriMatches()
    {
        var remote = new Uri("https://image.tmdb.org/t/p/original/abc.jpg");
        var tempFile = Path.Combine(Path.GetTempPath(), $"k7-portrait-{Guid.NewGuid():N}.webp");
        File.WriteAllText(tempFile, "x");
        try
        {
            var existingPicture = new MetadataPicture
            {
                Id = Guid.NewGuid(),
                Type = MetadataPictureType.Portrait,
                OriginalRemoteUri = remote,
                LocalPath = tempFile
            };

            var existingRole = new Actor
            {
                CharacterName = "Hero",
                PortraitPicture = existingPicture,
                Person = new Person
                {
                    Name = "Actor",
                    ExternalIds = [new ExternalId { ProviderName = "tmdb", Value = "42" }]
                }
            };

            var incomingRole = new Actor
            {
                CharacterName = "Hero",
                PortraitPicture = new MetadataPicture
                {
                    Type = MetadataPictureType.Portrait,
                    OriginalRemoteUri = remote
                },
                Person = new Person
                {
                    Name = "Actor",
                    ExternalIds = [new ExternalId { ProviderName = "tmdb", Value = "42" }]
                }
            };

            PersonRolePortraitReuseHelper.PreserveDownloadedPortraits([existingRole], [incomingRole]);

            incomingRole.PortraitPicture.Should().BeSameAs(existingPicture);
            existingRole.PortraitPicture.Should().BeNull();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public void PreserveDownloadedPortraits_ShouldNotReuse_WhenLocalPathMissing()
    {
        var remote = new Uri("https://image.tmdb.org/t/p/original/abc.jpg");
        var existingRole = new Actor
        {
            CharacterName = "Hero",
            PortraitPicture = new MetadataPicture
            {
                Type = MetadataPictureType.Portrait,
                OriginalRemoteUri = remote
            },
            Person = new Person { Name = "Actor" }
        };

        var incomingPortrait = new MetadataPicture
        {
            Type = MetadataPictureType.Portrait,
            OriginalRemoteUri = remote
        };
        var incomingRole = new Actor
        {
            CharacterName = "Hero",
            PortraitPicture = incomingPortrait,
            Person = new Person { Name = "Actor" }
        };

        PersonRolePortraitReuseHelper.PreserveDownloadedPortraits([existingRole], [incomingRole]);

        incomingRole.PortraitPicture.Should().BeSameAs(incomingPortrait);
        existingRole.PortraitPicture.Should().NotBeNull();
    }
}
