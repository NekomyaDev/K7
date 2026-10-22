using K7.Server.Application.Common.Helpers;
using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Common.Models;
using K7.Server.Application.Features.BackgroundTasks.Commands.CreateBackgroundTask;
using K7.Server.Application.Features.MetadataPictures.Commands.GenerateMetadataPictureVariants;
using K7.Server.Application.Features.MetadataPictures.Services;
using K7.Server.Application.Helpers;
using K7.Server.Domain.Entities;
using K7.Server.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace K7.Server.Application.Features.MetadataPictures.Queries.GetMetadataPicture;

//[Authorize]
public record GetMetadataPictureQuery(Guid Id, MetadataPictureSize? Size = null) : IRequest<HttpContentResult>;

public class GetMetadataPictureQueryHandler : IRequestHandler<GetMetadataPictureQuery, HttpContentResult>
{
    private readonly IApplicationDbContext _context;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ISender _sender;
    private readonly MetadataPictureVariantBackfillThrottle _backfillThrottle;
    private readonly ILogger<GetMetadataPictureQueryHandler> _logger;

    public GetMetadataPictureQueryHandler(
        IApplicationDbContext context,
        IHttpContextAccessor httpContextAccessor,
        ISender sender,
        MetadataPictureVariantBackfillThrottle backfillThrottle,
        ILogger<GetMetadataPictureQueryHandler> logger)
    {
        _context = context;
        _httpContextAccessor = httpContextAccessor;
        _sender = sender;
        _backfillThrottle = backfillThrottle;
        _logger = logger;
    }

    public async Task<HttpContentResult> Handle(GetMetadataPictureQuery query, CancellationToken cancellationToken)
    {
        var entity = await _context.MetadataPictures
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == query.Id, cancellationToken);

        if (entity is null)
        {
            return new EmptyHttpContentResult(404);
        }

        // Resolve the file path: use variant if requested, fallback to original
        string? localPath = entity.LocalPath;
        DateTimeOffset lastModified = entity.LastModified;
        Guid entityId = entity.Id;
        var isFallback = false;
        MetadataPictureSize? requestedSize = query.Size;

        if (requestedSize is { } size)
        {
            var variant = entity.Variants.FirstOrDefault(v => v.Size == size);
            if (variant is not null)
            {
                localPath = variant.LocalPath;
                lastModified = variant.LastModified;
                entityId = variant.Id;
            }
            else
            {
                isFallback = true;
            }
        }

        if (localPath is null)
        {
            return new EmptyHttpContentResult(404);
        }

        var file = new FileInfo(localPath);
        if (!file.Exists)
        {
            return new EmptyHttpContentResult(404);
        }

        if (isFallback && requestedSize is { } missingSize)
        {
            await RequestVariantBackfillAsync(entity, missingSize, cancellationToken);
        }

        var contentType = MimeTypeHelper.GetImageContentType(file.Extension);

        var httpContext = _httpContextAccessor.HttpContext;

        if (httpContext is null)
        {
            return new StreamHttpContentResult(file.OpenRead, contentType);
        }

        var eTag = new EntityTagHeaderValue($"\"{entityId}-{lastModified.ToUnixTimeSeconds()}\"");

        var responseHeaders = httpContext.Response.GetTypedHeaders();
        responseHeaders.LastModified = lastModified;
        responseHeaders.ETag = eTag;

        var allowLongTermCache = !isFallback
            || (requestedSize is { } fallbackSize
                && MetadataPictureVariantRules.IsPermanentVariantFallback(
                    entity.Type,
                    fallbackSize,
                    entity.OriginalWidth));

        if (!allowLongTermCache)
        {
            responseHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
        }
        else
        {
            responseHeaders.CacheControl = new CacheControlHeaderValue
            {
                Public = true,
                MaxAge = TimeSpan.FromDays(30),
                Extensions = { new NameValueHeaderValue("immutable") }
            };
        }

        var requestHeaders = httpContext.Request.GetTypedHeaders();

        if (requestHeaders.IfNoneMatch is { Count: > 0 })
        {
            if (requestHeaders.IfNoneMatch.Any(tag => tag.Tag.Equals(eTag.Tag, StringComparison.Ordinal)))
            {
                return new EmptyHttpContentResult(StatusCodes.Status304NotModified);
            }
        }
        else if (requestHeaders.IfModifiedSince is { } ifModifiedSince
                 && lastModified <= ifModifiedSince)
        {
            return new EmptyHttpContentResult(StatusCodes.Status304NotModified);
        }

        return new StreamHttpContentResult(file.OpenRead, contentType);
    }

    /// <summary>
    /// A requested size without its variant is served from the original (no-store, see above)
    /// and gets its variants generated in the background, so pictures that predate a size rule
    /// (avatars, for instance) catch up on first use instead of through an admin backfill. The
    /// response is never delayed by the resize; the next fetch gets the variant.
    /// </summary>
    private async Task RequestVariantBackfillAsync(MetadataPicture picture, MetadataPictureSize size, CancellationToken cancellationToken)
    {
        if (picture.Type == MetadataPictureType.Thumbnail
            || MetadataPictureVariantRules.IsPermanentVariantFallback(picture.Type, size, picture.OriginalWidth)
            || !_backfillThrottle.TryBegin(picture.Id))
        {
            return;
        }

        try
        {
            await _sender.Send(new CreateBackgroundTaskCommand
            {
                Request = new GenerateMetadataPictureVariantsCommand { MetadataPictureId = picture.Id },
                TargetEntityId = picture.Id,
                TargetEntityTypeName = nameof(MetadataPicture),
                Lane = BackgroundTaskLane.ImageProcessing,
                WorkClass = BackgroundTaskWorkClass.Polish,
                TriggeredBy = BackgroundTaskTriggeredBy.User,
                MaxAttempts = 3,
            }, cancellationToken);

            _logger.LogInformation(
                "Enqueued variant generation for MetadataPicture {PictureId} ({Type}) after a {Size} request without variant",
                picture.Id, picture.Type, size);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The picture is served from its original either way.
            _logger.LogWarning(ex, "Could not enqueue variant generation for MetadataPicture {PictureId}", picture.Id);
        }
    }
}
