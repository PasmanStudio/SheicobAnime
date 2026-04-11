using FluentValidation;
using AnimeIndex.Api.DTOs.Admin;

namespace AnimeIndex.Api.Validators;

public class CreateBlockedSlugValidator : AbstractValidator<CreateBlockedSlugRequest>
{
    public CreateBlockedSlugValidator()
    {
        RuleFor(x => x.Slug)
            .NotEmpty().WithMessage("Slug is required.")
            .MaximumLength(256).WithMessage("Slug must be 256 characters or less.")
            .Matches(@"^[a-z0-9\-]+$").WithMessage("Slug must contain only lowercase letters, numbers, and hyphens.");
    }
}
