using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AnimeIndex.Api.Migrations
{
    /// <summary>
    /// Guarda las métricas del reel en anime_news_items, para cerrar el loop
    /// entre publicar y medir.
    ///
    /// Hasta ahora medir costaba una corrida manual de 8 minutos y un CSV que
    /// alguien tenía que analizar a mano, así que se medía cada varios meses y
    /// en el medio se publicaba a ciegas — 7 piezas por día sin nada de vuelta.
    /// Con estas columnas cada pregunta futura es una query, y el selector de la
    /// noticia del día puede hacer few-shot con nuestros propios resultados.
    ///
    /// ig_reel_duration_seconds NO viene de la API: la duración la sabemos
    /// exacta al renderizar el video, y es lo que faltaba para poder calcular
    /// retención (watch time ÷ duración) en vez de watch time a secas.
    ///
    /// Como el resto de las migraciones recientes del proyecto, es SQL escrito a
    /// mano e idempotente.
    /// </summary>
    public partial class AddNewsReelInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_reel_views bigint;
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_reel_reach bigint;
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_reel_shares bigint;
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_reel_saved bigint;
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_reel_comments bigint;
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_reel_total_interactions bigint;
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_reel_avg_watch_seconds double precision;
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_reel_skip_rate double precision;
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_reel_duration_seconds double precision;
ALTER TABLE anime_news_items ADD COLUMN IF NOT EXISTS ig_insights_at timestamp with time zone;

-- El sync recorre los reels publicados en los últimos N días; sin esto es un
-- seq scan de toda la tabla en cada corrida.
CREATE INDEX IF NOT EXISTS idx_anime_news_items_reel_posted
    ON anime_news_items (ig_posted_at DESC)
    WHERE ig_reel_media_id IS NOT NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP INDEX IF EXISTS idx_anime_news_items_reel_posted;
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_reel_views;
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_reel_reach;
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_reel_shares;
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_reel_saved;
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_reel_comments;
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_reel_total_interactions;
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_reel_avg_watch_seconds;
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_reel_skip_rate;
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_reel_duration_seconds;
ALTER TABLE anime_news_items DROP COLUMN IF EXISTS ig_insights_at;
");
        }
    }
}
