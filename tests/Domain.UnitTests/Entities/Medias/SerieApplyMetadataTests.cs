using K7.Server.Domain.Entities;
using K7.Server.Domain.Entities.Medias;
using K7.Server.Domain.Entities.Metadatas.External;
using K7.Server.Domain.Enums;

namespace K7.Server.Domain.UnitTests.Entities.Medias;

public class SerieApplyMetadataTests
{
    [Test]
    public void ApplyMetadata_ShouldKeepDownloadedPoster_WhenRemoteUriUnchanged()
    {
        var remote = new Uri("https://image.tmdb.org/t/p/original/poster.jpg");
        var tempFile = Path.Combine(Path.GetTempPath(), $"k7-poster-{Guid.NewGuid():N}.webp");
        File.WriteAllText(tempFile, "x");
        try
        {
            var serie = new Serie { Title = "Show" };
            var existing = new MetadataPicture
            {
                Type = MetadataPictureType.Poster,
                OriginalRemoteUri = remote,
                LocalPath = tempFile
            };
            serie.Pictures.Add(existing);

            serie.ApplyMetadata(new ExternalSerieMetadata
            {
                Title = "Show",
                Pictures =
                [
                    new MetadataPicture
                    {
                        Type = MetadataPictureType.Poster,
                        OriginalRemoteUri = remote
                    }
                ]
            });

            serie.Pictures.Should().ContainSingle();
            serie.Pictures.Single().Should().BeSameAs(existing);
            serie.Pictures.Single().LocalPath.Should().Be(tempFile);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public void ApplyMetadata_ShouldReplacePoster_WhenLocalPathMissingOnDisk()
    {
        var remote = new Uri("https://image.tmdb.org/t/p/original/poster.jpg");
        var serie = new Serie { Title = "Show" };
        serie.Pictures.Add(new MetadataPicture
        {
            Type = MetadataPictureType.Poster,
            OriginalRemoteUri = remote,
            LocalPath = Path.Combine(Path.GetTempPath(), $"k7-missing-poster-{Guid.NewGuid():N}.webp")
        });

        var incoming = new MetadataPicture
        {
            Type = MetadataPictureType.Poster,
            OriginalRemoteUri = remote
        };

        serie.ApplyMetadata(new ExternalSerieMetadata
        {
            Title = "Show",
            Pictures = [incoming]
        });

        serie.Pictures.Should().ContainSingle();
        serie.Pictures.Single().Should().BeSameAs(incoming);
    }

    [Test]
    public void ApplyMetadata_ShouldReplacePoster_WhenRemoteUriChanges()
    {
        var serie = new Serie { Title = "Show" };
        serie.Pictures.Add(new MetadataPicture
        {
            Type = MetadataPictureType.Poster,
            OriginalRemoteUri = new Uri("https://image.tmdb.org/t/p/original/old.jpg"),
            LocalPath = "/data/medias/old.webp"
        });

        var incoming = new MetadataPicture
        {
            Type = MetadataPictureType.Poster,
            OriginalRemoteUri = new Uri("https://image.tmdb.org/t/p/original/new.jpg")
        };

        serie.ApplyMetadata(new ExternalSerieMetadata
        {
            Title = "Show",
            Pictures = [incoming]
        });

        serie.Pictures.Should().ContainSingle();
        serie.Pictures.Single().Should().BeSameAs(incoming);
    }

    [Test]
    public void ApplyMetadata_ShouldRespectLocksAndPreserveFederationIds()
    {
        var serie = new Serie
        {
            Title = "Locked",
            Status = "Ended"
        };
        serie.LockField(nameof(Serie.Title));
        serie.ExternalIds.Add(new ExternalId { ProviderName = "federation", Value = "remote:9" });

        serie.ApplyMetadata(new ExternalSerieMetadata
        {
            Title = "New Title",
            Status = "Returning",
            Overview = "Fresh",
            ExternalIds =
            [
                new ExternalId { ProviderName = "tvdb", Value = "42" }
            ]
        });

        serie.Title.Should().Be("Locked");
        serie.Status.Should().Be("Returning");
        serie.Overview.Should().Be("Fresh");
        serie.ExternalIds.Should().ContainSingle(e => e.ProviderName == "tvdb");
        serie.ExternalIds.Should().ContainSingle(e => e.ProviderName == "federation");
    }
}
