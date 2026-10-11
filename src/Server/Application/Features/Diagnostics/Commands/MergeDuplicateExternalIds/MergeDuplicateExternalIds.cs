using K7.Server.Application.Common.Interfaces;
using K7.Server.Application.Common.Security;
using K7.Server.Application.Helpers;
using K7.Server.Domain.Constants;
using Microsoft.Extensions.Logging;

namespace K7.Server.Application.Features.Diagnostics.Commands.MergeDuplicateExternalIds;

[Authorize(Roles = Roles.Administrator)]
public record MergeDuplicateExternalIdsCommand : IRequest<int>
{
    public Guid? LibraryId { get; init; }
}

public class MergeDuplicateExternalIdsCommandHandler(
    IApplicationDbContext context,
    ILogger<MergeDuplicateExternalIdsCommandHandler> logger)
    : IRequestHandler<MergeDuplicateExternalIdsCommand, int>
{
    public Task<int> Handle(MergeDuplicateExternalIdsCommand request, CancellationToken cancellationToken) =>
        DuplicateExternalIdMergeHelper.MergeAllAsync(context, request.LibraryId, logger, cancellationToken);
}
