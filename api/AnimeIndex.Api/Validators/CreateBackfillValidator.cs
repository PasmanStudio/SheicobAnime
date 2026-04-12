using FluentValidation;
using AnimeIndex.Api.DTOs.Admin;

namespace AnimeIndex.Api.Validators;

public class CreateBackfillValidator : AbstractValidator<CreateBackfillRequest>
{
    private static readonly HashSet<string> ValidSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "source1", "source2"
    };

    public CreateBackfillValidator()
    {
        RuleFor(x => x.Source)
            .Must(s => ValidSources.Contains(s))
            .WithMessage("Source must be one of: source1, source2.");

        RuleFor(x => x.MaxPages)
            .InclusiveBetween(1, 500)
            .WithMessage("MaxPages must be between 1 and 500.");
    }
}
