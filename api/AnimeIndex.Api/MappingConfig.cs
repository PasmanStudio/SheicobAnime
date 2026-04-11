using AnimeIndex.Api.Data.Entities;
using AnimeIndex.Api.DTOs;
using Mapster;

namespace AnimeIndex.Api;

public static class MappingConfig
{
    public static void RegisterMappings()
    {
        TypeAdapterConfig<Series, SeriesDto>.NewConfig()
            .Map(d => d.Genres,
                 s => s.SeriesGenres.Select(sg => new GenreDto(sg.Genre.Id, sg.Genre.Name)).ToArray());

        TypeAdapterConfig<Episode, EpisodeDto>.NewConfig()
            .Map(d => d.Series,
                 s => s.Series != null
                    ? new SeriesStubDto(s.Series.Id, s.Series.Slug, s.Series.Title, s.Series.CoverUrl)
                    : null)
            .Map(d => d.Mirrors,
                 s => s.Mirrors != null
                    ? s.Mirrors.Where(m => m.IsActive).Select(m => m.Adapt<MirrorDto>()).ToArray()
                    : null);
    }
}
