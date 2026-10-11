namespace K7.Server.Application.Features.Diagnostics.Commands.MergeDuplicateExternalIds;

public class MergeDuplicateExternalIdsCommandValidator : AbstractValidator<MergeDuplicateExternalIdsCommand>
{
    public MergeDuplicateExternalIdsCommandValidator()
    {
        RuleFor(x => x.LibraryId).NotEqual(Guid.Empty).When(x => x.LibraryId.HasValue);
    }
}
