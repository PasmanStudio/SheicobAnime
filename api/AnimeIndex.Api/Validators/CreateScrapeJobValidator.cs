using FluentValidation;
using AnimeIndex.Api.DTOs.Admin;

namespace AnimeIndex.Api.Validators;

public class CreateScrapeJobValidator : AbstractValidator<CreateScrapeJobRequest>
{
    public CreateScrapeJobValidator()
    {
        RuleFor(x => x.SourceUrl)
            .NotEmpty().WithMessage("Source URL is required.")
            .Must(uri => Uri.TryCreate(uri, UriKind.Absolute, out var result)
                         && (result.Scheme == Uri.UriSchemeHttp || result.Scheme == Uri.UriSchemeHttps))
            .WithMessage("Source URL must be a valid HTTP(S) URL.");
    }
}
