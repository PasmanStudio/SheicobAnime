# Plan: SeekStreaming Upload Pipeline — Simplificación MVP

> **Status:** Diseño aprobado — pendiente implementación  
> **Fecha:** Mayo 2026  
> **API Key:** En GitHub Secrets como `SEEKSTREAMING_API_KEY` (nunca en código)

---

## 1. Objetivo

Simplificar el pipeline de scraping adoptando un **único mirror propio por episodio** (SeekStreaming), eliminando la complejidad de múltiples mirrors externos, fallback y selector de servidor.

### Qué entra vs. qué sale

| Se elimina (complejidad) | Se agrega (valor) |
|---|---|
| Búsqueda de mirrors externos como fuente principal | Pipeline de upload a SeekStreaming |
| Lógica de fallback automático | Embed URL propio guardado con `priority=0` |
| Selector expandido por default | Selector colapsado (SeekStreaming primero) |
| Dependencia de terceros para ingresos | Ingresos por ads propios en SeekStreaming |

### Scope

- **Solo episodios nuevos** — los ~12K mirrors externos existentes en Supabase quedan intactos.
- Backward compat total: si SeekStreaming no está disponible para un episodio, el player cae al comportamiento actual.

---

## 2. Decisiones de diseño

| Decisión | Valor |
|---|---|
| Método de upload | Remote URL (`GET /api/upload/url?key=KEY&url=VIDEO_URL`) — SeekStreaming descarga por su cuenta |
| Fuente del video | Resolvers existentes (StreamWish, VidHide, Mp4Upload, OkRu, **VOE**) → mp4/m3u8 URL |
- **Prioridad de resolución** | VOE > Mp4Upload > OkRu > StreamWish > VidHide > otros (todos los primeros tres devuelven mp4 directo) |
| Polling de completion | No necesario — filecode disponible inmediatamente |
| Fallback | Si todos los resolvers fallan → episodio queda con mirrors externos normales |
| Hosting del scraper | GitHub Actions cron (`scraper-cron.yml`, 02:00 UTC diario) |

---

## 3. Arquitectura del pipeline

```
JKAnime scraper encuentra mirrors externos por episodio
           ↓
Source2Strategy / BackfillJob upserta mirrors externos (comportamiento actual)
           ↓
SeekStreamingUploadService.TryUploadEpisodeAsync(episodeId, embedUrls)
           ↓
  ┌── Para cada embed URL (orden prioridad) ──┐
  │  ResolverRegistry.ResolveAsync(mirror)     │
  │     → mp4/m3u8 URL directo                 │
  └───────────────────────────────────────────┘
           ↓ (primera URL que resuelve exitosamente)
  SeekStreamingClient.UploadFromUrlAsync(videoUrl)
     GET https://seekstreaming.com/api/upload/url?key=KEY&url={videoUrl}
     → { "status": 200, "result": { "filecode": "xxx" } }
           ↓
  UpsertMirrorAsync({
    EpisodeId, 
    ProviderName = "seekstreaming",
    EmbedUrl = "https://seekstreaming.com/e/{filecode}",
    Priority = 0   ← siempre primero
  })
           ↓
  EpisodePlayer: renderiza SeekStreaming primero
                 "Ver otros servidores ▼" colapsa los externos
```

---

## 4. Archivos a crear / modificar

### Nuevos archivos

| Archivo | Propósito |
|---|---|
| `scraper/AnimeIndex.Scraper/Infrastructure/SeekStreamingClient.cs` | HTTP client para la API de SeekStreaming |
| `scraper/AnimeIndex.Scraper/Infrastructure/SeekStreamingUploadService.cs` | Orquesta: resolver → upload → upsert |
| `api/AnimeIndex.Api/Infrastructure/Resolvers/VoeResolver.cs` | Resolver para mirrors `voe.sx` (faltante) |

### Archivos modificados

| Archivo | Cambio |
|---|---|
| `scraper/AnimeIndex.Scraper/Program.cs` | Registrar resolvers + `ResolverRegistry` + `SeekStreamingClient` + `SeekStreamingUploadService` |
| `scraper/AnimeIndex.Scraper/appsettings.json` | Sección `"SeekStreaming": { "BaseUrl": "...", "ApiKey": "" }` |
| `scraper/AnimeIndex.Scraper/Jobs/BackfillJob.cs` | Pass 3: upload a SeekStreaming de episodios sin mirror propio |
| `scraper/AnimeIndex.Scraper/Strategies/Source2Strategy.cs` | Llamar `TryUploadEpisodeAsync` tras upsert de mirrors externos |
| `.github/workflows/scraper-cron.yml` | Agregar `SEEKSTREAMING_API_KEY: ${{ secrets.SEEKSTREAMING_API_KEY }}` |
| `web/src/components/player/EpisodePlayer.tsx` | Selector colapsado cuando SeekStreaming está presente |

---

## 5. Especificación detallada

### 5.1 `SeekStreamingClient.cs`

```csharp
// Métodos públicos:
Task<string?> UploadFromUrlAsync(string videoUrl, CancellationToken ct)
// → Llama GET /api/upload/url?key=KEY&url={videoUrl}
// → Devuelve filecode o null si falla
// → 2 reintentos con backoff de 3s

string GetEmbedUrl(string filecode)
// → https://seekstreaming.com/e/{filecode}

// Config keys:
// SeekStreaming:ApiKey  (override por env: SEEKSTREAMING__APIKEY)
// SeekStreaming:BaseUrl (default: https://seekstreaming.com)
```

**Respuesta esperada de la API:**
```json
{
  "status": 200,
  "result": {
    "filecode": "fb5asfuj2snh"
  }
}
```

### 5.2 `SeekStreamingUploadService.cs`

```csharp
Task<bool> TryUploadEpisodeAsync(
    Guid episodeId,
    IReadOnlyList<string> embedUrls,
    CancellationToken ct
)
```

**Flujo:**
  1. Ordenar `embedUrls` por prioridad de resolución: VOE > Mp4Upload > OkRu > StreamWish > VidHide > otros
2. Para cada URL:
   a. Crear `Mirror` temporal con el embed URL
   b. `ResolverRegistry.ResolveAsync(tempMirror, ct)`
   c. Si resuelve → `SeekStreamingClient.UploadFromUrlAsync(resolvedUrl, ct)`
   d. Si hay filecode → `UpsertMirrorAsync` con `provider_name='seekstreaming'`, `priority=0`
   e. `return true` (break loop)
3. Si todos fallan → log warning → `return false`

### 5.3 `VoeResolver.cs`

**Testeado manualmente — patrón confirmado.**

VOE **no usa packed JS ni m3u8**. Expone el mp4 directamente via `var source='URL'`.

**Flujo real (dos HTTP requests):**
1. `GET https://voe.sx/e/{id}` → 751 bytes de HTML con JS redirect: `window.location.href = 'https://maryspecialwatch.com/e/{id}'`
2. `GET https://maryspecialwatch.com/e/{id}` → HTML completo con `var source='https://.../{filename}.mp4'`
3. Extracción: regex `var source='([^']+)'` → URL directa mp4

**Consecuencia positiva:** VOE devuelve mp4 directo, ideal para SeekStreaming remote upload (sin problemas de token HLS expirado).

```csharp
public string Hoster => "voe";

// Paso 1: GET voe.sx/e/{id} → parsear JS redirect
// Regex redirect: window\.location\.href\s*=\s*'(https?://[^']+e/[^']+)'
// (excluir la rama del permanentToken)

// Paso 2: GET {redirectUrl} → parsear source
// Regex source: var source='([^']+)'
// → Devuelve SourceFormat.Mp4, no Referer necesario
```

### 5.4 BackfillJob — Pass 3

**Query para encontrar episodios pendientes:**
```sql
SELECT DISTINCT e.id, 
       array_agg(m.embed_url ORDER BY m.priority) as embed_urls
FROM episodes e
JOIN mirrors m ON m.episode_id = e.id AND m.is_active = true
WHERE NOT EXISTS (
    SELECT 1 FROM mirrors sk 
    WHERE sk.episode_id = e.id AND sk.provider_name = 'seekstreaming'
)
GROUP BY e.id
ORDER BY e.created_at DESC  -- más recientes primero
```

**Rate limiting:**
- Batches de 50 episodios
- Delay de 2s entre uploads (respetar límite API SeekStreaming)
- Heartbeat cada 10 episodios

### 5.5 Frontend — `EpisodePlayer.tsx`

**Estado nuevo:**
```typescript
const [showAllServers, setShowAllServers] = useState(false);
```

**Lógica de render del selector:**
```typescript
const hasSeekStreaming = activeMirrors.some(m => m.providerName === 'seekstreaming');

// Si hay SeekStreaming:
//   - Renderizar SOLO el botón de SeekStreaming activo
//   - Botón "Ver otros servidores ▼" que togglea showAllServers
//   - Si showAllServers === true → renderizar todos los entries
// Si NO hay SeekStreaming:
//   - Comportamiento actual sin cambios (backward compat)
```

---

## 6. Configuración — variables de entorno

### GitHub Secrets (agregar)
```
SEEKSTREAMING_API_KEY = 9db0b40302002d160ce3172e
```

### scraper-cron.yml (env vars del step)
```yaml
SEEKSTREAMING__APIKEY: ${{ secrets.SEEKSTREAMING_API_KEY }}
```

### appsettings.json del scraper
```json
"SeekStreaming": {
  "BaseUrl": "https://seekstreaming.com",
  "ApiKey": ""
}
```

---

## 7. Verificación y testing

### Pre-implementación — confirmar API de SeekStreaming
```powershell
# 1. Verificar account info
Invoke-RestMethod "https://seekstreaming.com/api/account/info?key=9db0b40302002d160ce3172e"

# 2. Test remote upload con un mp4 público pequeño
$res = Invoke-RestMethod "https://seekstreaming.com/api/upload/url?key=9db0b40302002d160ce3172e&url=https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/360/Big_Buck_Bunny_360_10s_1MB.mp4"
$res.result.filecode

# 3. Verificar que el filecode genera un embed válido
# Abrir: https://seekstreaming.com/e/{filecode}
```

### Post-implementación
```powershell
# 1. Build + tests
cd api; dotnet build AnimeIndex.sln; dotnet test AnimeIndex.sln

# 2. Type-check frontend
cd web; npm run type-check; npm run build

# 3. Trigger manual del cron
# GitHub → Actions → "Scraper — Daily cron" → Run workflow

# 4. Verificar en Supabase
$q = "SELECT e.title, m.embed_url, m.priority FROM mirrors m JOIN episodes e ON e.id = m.episode_id WHERE m.provider_name = 'seekstreaming' ORDER BY m.created_at DESC LIMIT 5"

# 5. Abrir un episodio en frontend → SeekStreaming aparece primero → "Ver otros ▼"
```

---

## 8. Riesgos y mitigaciones

| Riesgo | Probabilidad | Mitigación |
|---|---|---|
| SeekStreaming no acepta m3u8 en remote upload | **Resuelto** | VOE devuelve mp4 directo. Mp4Upload/OkRu también devuelven mp4. StreamWish/VidHide devuelven m3u8 pero se usarán como última opción. |
| Rate limiting de SeekStreaming API | Baja | Delay de 2s entre uploads + retry con backoff |
| Resolver falla para todos los mirrors de un episodio | Media | Fallback graceful: episodio queda con mirrors externos, no crash |
| SeekStreaming cae (single point of failure) | Baja (post-MVP) | Agregar segundo mirror como backup en fase post-MVP |
| Token de m3u8 expirado para upload remoto | **Mitigado** | VOE/Mp4Upload/OkRu devuelven mp4 directo (sin token). StreamWish/VidHide devuelven m3u8 con token — usar siempre inmediatamente tras resolver, nunca cachear. |
| GitHub Actions timeout (6h) para backfill masivo | Media | Pass 3 en BackfillJob con resume support (igual que Pass 1/2) |

---

## 9. Fases de implementación

1. **Spike** — Probar VOE extraction + SeekStreaming API manualmente (ver sección 7)
2. **VoeResolver** — Agregar resolver para completar cobertura de mirrors en DB
3. **SeekStreamingClient** + **SeekStreamingUploadService** — Core del pipeline
4. **Integración scraper** — `Source2Strategy` + `BackfillJob` Pass 3
5. **Frontend** — Selector simplificado en `EpisodePlayer`
6. **CI/CD** — Secret en GH Actions, env var en cron workflow
7. **Verificación** — Deploy, logs, Supabase check, smoke test

---

## 10. Notas de implementación importantes

- **Nunca** commitear la API key. Siempre desde `IConfiguration` (`SeekStreaming:ApiKey`).
- El `SeekStreamingUploadService` se inyecta en el scraper, **no en el API**. No exponer el upload directamente al exterior.
- El mirror de SeekStreaming usa `priority=0` para que siempre sea el primero en el sort del player.
- El `provider_name` debe ser literalmente `"seekstreaming"` (lowercase, sin guiones) para que el frontend lo detecte.
- Si SeekStreaming cambia su API: solo hay que tocar `SeekStreamingClient.cs`, nada más.
- El `VoeResolver` es **bloqueante** para este plan — VOE está entre los providers más frecuentes en la DB actual.
