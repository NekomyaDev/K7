using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Common.Models;
using K7.Server.Application.Features.BackgroundTasks.Commands.CreateBackgroundTask;
using K7.Server.Application.Features.MetadataPictures.Commands.GenerateMetadataPictureVariants;
using K7.Server.Application.Features.MetadataPictures.Queries.GetMetadataPicture;
using K7.Server.Application.Features.MetadataPictures.Services;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MockQueryable.NSubstitute;

namespace K7.Server.Application.UnitTests.Features.MetadataPictures.Queries;

[TestFixture]
public class GetMetadataPictureQueryHandlerTests
{
    private IApplicationDbContext _context = null!;
    private ISender _sender = null!;
    private GetMetadataPictureQueryHandler _handler = null!;
    private List<MetadataPicture> _pictures = null!;
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "k7-picture-query-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDirectory);

        _context = Substitute.For<IApplicationDbContext>();
        _sender = Substitute.For<ISender>();
        _pictures = [];
        var pictures = _pictures.BuildMockDbSet();
        _context.MetadataPictures.Returns(pictures);

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        httpContextAccessor.HttpContext.Returns((HttpContext?)null);

        _handler = new GetMetadataPictureQueryHandler(
            _context,
            httpContextAccessor,
            _sender,
            new MetadataPictureVariantBackfillThrottle(),
            Substitute.For<ILogger<GetMetadataPictureQueryHandler>>());
    }

    [TearDown]
    public void TearDown()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Test]
    public async Task Handle_ShouldEnqueueVariantGeneration_WhenRequestedSizeHasNoVariant()
    {
        var picture = AddPicture(MetadataPictureType.UserAvatar, originalWidth: 1024);

        var result = await _handler.Handle(new GetMetadataPictureQuery(picture.Id, MetadataPictureSize.Small), CancellationToken.None);

        result.Should().BeOfType<StreamHttpContentResult>();
        await _sender.Received(1).Send(
            Arg.Is<CreateBackgroundTaskCommand>(command =>
                command.Request is GenerateMetadataPictureVariantsCommand
                && ((GenerateMetadataPictureVariantsCommand)command.Request).MetadataPictureId == picture.Id
                && command.TargetEntityId == picture.Id
                && command.Lane == BackgroundTaskLane.ImageProcessing),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldEnqueueVariantGeneration_WhenOriginalDimensionsAreUnknown()
    {
        var picture = AddPicture(MetadataPictureType.SharedProfileAvatar, originalWidth: null);

        await _handler.Handle(new GetMetadataPictureQuery(picture.Id, MetadataPictureSize.Small), CancellationToken.None);

        await _sender.Received(1).Send(Arg.Any<CreateBackgroundTaskCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldEnqueueOnce_WhenTheSamePictureIsRequestedRepeatedly()
    {
        var picture = AddPicture(MetadataPictureType.UserAvatar, originalWidth: 1024);

        await _handler.Handle(new GetMetadataPictureQuery(picture.Id, MetadataPictureSize.Small), CancellationToken.None);
        await _handler.Handle(new GetMetadataPictureQuery(picture.Id, MetadataPictureSize.Small), CancellationToken.None);
        await _handler.Handle(new GetMetadataPictureQuery(picture.Id, MetadataPictureSize.Medium), CancellationToken.None);

        await _sender.Received(1).Send(Arg.Any<CreateBackgroundTaskCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldNotEnqueue_WhenTheVariantExists()
    {
        var picture = AddPicture(MetadataPictureType.UserAvatar, originalWidth: 1024);
        var variantPath = WriteFile("variant.webp");
        picture.Variants.Add(new MetadataPictureVariant
        {
            Id = Guid.NewGuid(),
            MetadataPictureId = picture.Id,
            Size = MetadataPictureSize.Small,
            LocalPath = variantPath,
            Width = 160,
        });

        var result = await _handler.Handle(new GetMetadataPictureQuery(picture.Id, MetadataPictureSize.Small), CancellationToken.None);

        result.Should().BeOfType<StreamHttpContentResult>();
        await _sender.DidNotReceive().Send(Arg.Any<CreateBackgroundTaskCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldNotEnqueue_WhenServingTheOriginalIsPermanent()
    {
        // 120 px original for a 160 px Small rule: no variant will ever exist.
        var picture = AddPicture(MetadataPictureType.UserAvatar, originalWidth: 120);

        await _handler.Handle(new GetMetadataPictureQuery(picture.Id, MetadataPictureSize.Small), CancellationToken.None);

        await _sender.DidNotReceive().Send(Arg.Any<CreateBackgroundTaskCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldNotEnqueue_WhenNoSizeIsRequested()
    {
        var picture = AddPicture(MetadataPictureType.UserAvatar, originalWidth: 1024);

        await _handler.Handle(new GetMetadataPictureQuery(picture.Id), CancellationToken.None);

        await _sender.DidNotReceive().Send(Arg.Any<CreateBackgroundTaskCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldNotEnqueue_WhenTheTypeHasNoRuleForTheSize()
    {
        var picture = AddPicture(MetadataPictureType.Logo, originalWidth: 2000);

        await _handler.Handle(new GetMetadataPictureQuery(picture.Id, MetadataPictureSize.Small), CancellationToken.None);

        await _sender.DidNotReceive().Send(Arg.Any<CreateBackgroundTaskCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ShouldStillServeTheOriginal_WhenEnqueueFails()
    {
        var picture = AddPicture(MetadataPictureType.UserAvatar, originalWidth: 1024);
        _sender.Send(Arg.Any<CreateBackgroundTaskCommand>(), Arg.Any<CancellationToken>())
            .Returns<Task<Guid>>(_ => throw new InvalidOperationException("queue down"));

        var result = await _handler.Handle(new GetMetadataPictureQuery(picture.Id, MetadataPictureSize.Small), CancellationToken.None);

        result.Should().BeOfType<StreamHttpContentResult>();
    }

    private MetadataPicture AddPicture(MetadataPictureType type, int? originalWidth)
    {
        var picture = new MetadataPicture
        {
            Id = Guid.NewGuid(),
            Type = type,
            LocalPath = WriteFile($"{Guid.NewGuid()}.jpg"),
            OriginalWidth = originalWidth,
            OriginalHeight = originalWidth,
        };

        _pictures.Add(picture);
        return picture;
    }

    private string WriteFile(string name)
    {
        var path = Path.Combine(_tempDirectory, name);
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF]);
        return path;
    }
}
