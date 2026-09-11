# Playbook de Instagram — SheicobAnime

**Última actualización:** 10-sep-2026 (Sprints 1, 2 y 3 implementados — ver §7, §8 y §9)
**Base de datos del análisis:** 785 piezas publicadas (301 reels + 484 feed), 16-may → 9-sep-2026, exportadas de la Graph API de Meta.

Este documento existe para poder retomar el tema en otra conversación sin repetir la investigación. Tiene los números medidos, qué hacer con ellos, y qué todavía no sabemos.

---

## 0. Cómo regenerar los datos

**Desde sep-2026 las métricas de los reels se guardan solas en la DB.** El
workflow `insights-sync-cron.yml` corre los lunes y llena `anime_news_items` con
views, reach, shares, watch time, skip rate y la duración renderizada, al lado
del titular que las produjo. O sea que la mayoría de las preguntas ya no piden un
export: son una query.

```sql
-- Retención real (watch time ÷ duración), que es lo que antes no se podía calcular
SELECT title, ig_reel_views, ig_reel_avg_watch_seconds, ig_reel_duration_seconds,
       round((ig_reel_avg_watch_seconds / ig_reel_duration_seconds * 100)::numeric, 1) AS retencion_pct
FROM anime_news_items
WHERE ig_reel_views IS NOT NULL AND ig_reel_duration_seconds > 0
ORDER BY ig_reel_views DESC LIMIT 20;
```

Para forzar una sincronización a mano:

```bash
gh workflow run insights-sync-cron.yml --repo PasmanStudio/SheicobAnime --ref main -f days=90
```

El export a CSV sigue existiendo para el análisis de la cuenta ENTERA (incluye
las piezas de feed y de episodios, que no viven en `anime_news_items`):

```bash
# 1. Verificar que el token sirve (5 segundos, antes de gastar 8 minutos)
Instagram__AccessToken=<token> dotnet run --project scraper/AnimeIndex.Scraper -- --token-scopes

# 2. Export completo → sube el CSV como artifact
gh workflow run insights-export.yml --repo PasmanStudio/SheicobAnime --ref main -f days=120
```

El token necesita el scope **`instagram_manage_insights`** (no `instagram_business_manage_insights`: la cuenta usa la variante de Facebook Login). Publicar usa `instagram_content_publish`, que es otro permiso — tener uno no implica el otro.

**Los tokens del Graph API Explorer duran 1-2 horas.** Para dejarlo en un secret hay que convertirlo a uno de 60 días:

```bash
curl -s "https://graph.facebook.com/oauth/access_token?grant_type=fb_exchange_token&client_id=APP_ID&client_secret=APP_SECRET&fb_exchange_token=TOKEN_CORTO"
```

---

## 1. Los números

### El feed casi no existe

| | Piezas | % del output | Views | % de las views |
|---|---|---|---|---|
| Reels | 301 | 38 % | 349.119 | **97,9 %** |
| Feed (carrusel) | 484 | 62 % | 7.604 | **2,1 %** |

Mediana por pieza: **341 views un reel, 13 una de feed**. Un reel vale **74×** una pieza de feed.

### El video real duplica todo

Cruce de los reels del 2-9 sep contra los logs de sus corridas (con tráiler descargado vs slideshow de fallback):

| | n | Views (med) | Reach (med) | Interacciones (med) | Watch time |
|---|---|---|---|---|---|
| **Con video real** | 17 | **660** | 577 | **56** | **6,9 s** |
| Sin video (slideshow) | 22 | 248 | 227 | 8 | 3,3 s |

2,7× views · 2,5× reach · **7× interacciones**.

> ⚠️ n=39, una sola semana, y es **observacional, no un experimento**. Las noticias que *tienen* tráiler pueden ser intrínsecamente más interesantes que un anuncio de seiyuus. Parte de la brecha es la noticia, no el video. Confianza: media-alta por el tamaño del efecto, no por el rigor del diseño.

### El watch time es la variable que manda

Correlación de Spearman contra views: **rho = 0,76** (n=301). Por cuartil de retención:

| Cuartil | Watch time (med) | Views (med) |
|---|---|---|
| Q1 | 2,8 s | 179 |
| Q2 | 3,8 s | 277 |
| Q3 | 5,3 s | 576 |
| **Q4** | **8,9 s** | **1.734** |

**10× de punta a punta.** Top-20 de reels: 11,0 s de watch time. El resto: 4,2 s.

Es la métrica más accionable que tenemos: es la única que explica tanto y sobre la que se puede trabajar directamente (edición, primeros segundos, duración).

### Los shares son el amplificador

48 reels (16 % del output) tienen ≥10 shares. Esos 48 concentran el **62 % de todas las views**.

| | n | Views (med) |
|---|---|---|
| Reels con ≥10 shares | 48 | **2.255** |
| Reels con <10 shares | 253 | 276 |

Distribución brutal: **el top 10 se lleva el 39 % de todas las views; el #1 solo, el 10 %.** Esto no es un negocio de mejorar la mediana — es de producir más candidatos a explotar.

### Qué había en los que explotaron

| Views | Watch | Tema |
|---|---|---|
| 35.192 | 15,3 s | **Free Fire × anime** (crossover gaming) |
| 27.622 | 13,3 s | **The Ninth Jedi** (Star Wars) |
| 18.125 | 17,8 s | Chainsaw Man The Stage — video musical |
| 12.812 | 20,8 s | Jujutsu Kaisen T4 |
| 11.315 | 13,4 s | Toei Animation 70 años |
| 6.680 | 13,6 s | Sukuna / Jujutsu Kaisen T4 |
| 6.615 | 11,3 s | Ranma 1/2 — fecha de estreno |
| 6.517 | 14,4 s | Tsugumi Project |
| 5.971 | 7,3 s | Rascal Does Not Dream — primer tráiler |

**El patrón: crossovers con audiencias que ya son masivas fuera del anime (Free Fire, Star Wars, Final Fantasy) y anuncios de peso de franquicias top.** No estrenos de nicho.

### Franquicia grande: apuesta de cola, no mejora del piso

| | n | Views (med) | Views (media) |
|---|---|---|---|
| Franquicia grande | 45 | 367 | **3.065** |
| Resto | 256 | 338 | 825 |

La mediana es prácticamente igual — publicar Jujutsu **no garantiza** que rinda. Pero franquicia grande es el **15 % de los reels y el 45 % del top-20**: es donde ocurren los picos.

### Horarios (hora de Argentina)

| Hora | n | Views (med) |
|---|---|---|
| **14** | 32 | **795** |
| 21 | 27 | 412 |
| 16 | 9 | 392 |
| 11 | 18 | 378 |
| 12 | 39 | 312 |
| 20 | 42 | 319 |
| 19 | 28 | 284 |

**14h ART se despega claramente** y tiene volumen suficiente (n=32) para creerle. El resto está entre 280 y 410.

### Evolución mensual

| Mes | n | Watch (med) | Views (med) | Shares (total) |
|---|---|---|---|---|
| Julio | 115 | 4,3 s | 194 | 576 |
| **Agosto** | 142 | 4,5 s | **620** | **3.811** |
| Septiembre | 44 | 4,0 s | 321 | 159 |

El watch time fue plano los tres meses. Lo que se movió fueron los **shares (7× en agosto)**. Agosto no fue mejor contenido: fue mejor distribución. Verificado que la caída de septiembre no es sesgo de "posts jóvenes" (el efecto de antigüedad existe pero es chico).

---

## 2. Qué implementar, por orden de impacto

### ~~🔴 A. Dejar de gastar el 62 % del output en feed~~ ✅ hecho (§7.4)

Los dos crons de carrusel diarios (16:00 y 18:00 UTC en `news-cron.yml`) producen el 2 % del alcance. **Convertirlos en reels o eliminarlos.** Es la única acción de esta lista que no requiere ninguna hipótesis: los números son categóricos.

Riesgo a considerar: el feed podría aportar a la percepción de "cuenta activa" o al SEO interno de Instagram. No lo medimos. Pero 74× de diferencia deja mucho margen aun si eso vale algo.

### 🔴 B. Subir la tasa de reels con video

Hoy: **60 %** (post-PR #169; antes era 37 %). Cada reel que pasa de slideshow a video vale ~2,7× views y ~7× interacciones.

De los que fallan hoy, lo revisado indica que **la mayoría son noticias que genuinamente no tienen tráiler** (giras de conciertos, anuncios de seiyuus, temas musicales). Dos caminos:

1. **Aceptar más el video embebido del artículo** en vez de exigir YouTube. Ya existe el camino (`AnimeNews: video {Kind} embebido del articulo aceptado`) pero se usa poco.
2. **Ser más estricto en qué noticia se convierte en reel.** Si no hay video, quizá esa noticia debería ir a feed (o no ir) y el slot de reel usarse con otra del pool. Hoy el pool tiene ~10 candidatas por corrida — hay de dónde elegir.

La opción 2 es probablemente mejor: no fuerza video donde no hay, y sube la tasa eligiendo mejor.

### 🟠 C. Trabajar los primeros 3 segundos

Con rho=0,76, el watch time es el lever. La distribución muestra **125 de 301 reels en la franja de 2-4 s** — o sea, la mayoría se abandona casi de inmediato.

Ideas para testear (no medidas todavía):
- Sacar cualquier intro/branding del arranque; que el primer frame sea ya el contenido.
- Empezar por el clip de video, no por la placa de titular.

**`reels_skip_rate` ya está agregado al export** — la próxima corrida va a decir exactamente cuántos pasan de largo, que es la contracara directa de esto.

> ⚠️ **Corrección (10-sep-2026): B y C son en buena parte el MISMO hallazgo, y
> "acortar a 8 s" era un error.**
>
> `ig_reels_avg_watch_time` está **acotado por la duración del video**, y las dos
> duraciones que produce el pipeline son fijas y conocidas:
>
> | Formato | Duración renderizada | Watch (med) | Retención |
> |---|---|---|---|
> | Slideshow (5 slides) | `5×4,0 − 4×0,6` = **17,6 s** | 3,3 s | 19 % |
> | Tráiler + 3 info slides | `45 + 3×3,5` = **55,5 s** | 6,9 s | 12 % |
>
> Un slideshow de 17,6 s no puede dar 11 s de watch time ni viéndolo entero. O sea
> que el cuartil Q4 (8,9 s) es, casi por construcción, "los reels con tráiler", y
> el rho=0,76 mide en gran medida *tráiler vs slideshow* otra vez. **No son dos
> palancas independientes.**
>
> Y acortar a 8 s pondría un techo duro sobre la métrica que correlaciona con
> views: los 9 reels que explotaron tienen watch times de 15,3 · 13,3 · 17,8 ·
> 20,8 · 13,4 · 13,6 · 11,3 · 14,4 · 7,3 s. **Ocho de nueve pasan los 11 s** — un
> reel de 8 s no puede producir ninguno de esos resultados.
>
> Lo que sí sobra son los **10,5 s de slides estáticas al final**: con 12 % de
> retención el espectador mediano abandona en el segundo 7 de 55, así que esas
> slides no las ve casi nadie y en cambio destruyen la finalización y matan el
> *loop* (que IG cuenta como reproducción nueva). Eso se resolvió dejando 1 sola
> info slide.
>
> **Corrección posterior (10-sep-2026): acortar el TRÁILER también era un error**
> — ver §8.3. El argumento de arriba llevado hasta el final dice que el tráiler
> tiene que ir entero, no recortado a 26 s.
>
> **La palanca limpia e independiente de la duración es `reels_skip_rate`** — se
> mide en los primeros segundos. Mediana 55 %, y 7,6× entre el mejor y el peor
> cuartil. Todo el trabajo de retención debería juzgarse contra esa métrica.

### 🟠 D. Mover reels al horario de 14h ART

14h tiene mediana 795 contra ~320-400 del resto. Los crons actuales de reel son 10:00, 12:00, 14:00, 18:00 y 20:00 ART. **El de 14h ya existe y es el mejor.** Vale la pena mover uno de los flojos (17h tiene mediana 160 con n=5) a la franja 13-15h.

Cuidado: correlación, no causalidad. Puede que a las 14h se hayan publicado por azar las noticias más fuertes. Con n=32 es sugestivo, no concluyente.

### 🟡 E. Priorizar la fuente por tasa de video

De los logs de la semana del 2-9 sep:

| Fuente | Reels | Con video |
|---|---|---|
| Crunchyroll | 34 | 38 % |
| Kudasai | 8 | **75 %** |

Kudasai duplica la tasa. Muestra chica (n=8) y posiblemente confundido (Kudasai puede cubrir más noticias de tipo tráiler). Pero priorizarlo en el pool de candidatas para los slots de reel es gratis de implementar y fácil de revertir.

Nota aparte: **el 81 % de los reels sale de una sola fuente (Crunchyroll)**. Diversificar el pool probablemente sube la varianza — y en un negocio de cola, más varianza es bueno.

### 🟡 F. Sesgar hacia crossovers y franquicias masivas

Los picos son crossovers con audiencias que exceden el anime (Free Fire, Star Wars, Final Fantasy, LEGO) y anuncios de peso de franquicias top. La mediana no mejora, pero la cola sí.

Implementable: en el prompt de selección de "noticia del día para el reel", agregar preferencia explícita por crossovers y por un listado de franquicias masivas cuando haya empate de relevancia.

---

---

## 3. ¿El alcance se convierte en seguidores? Sí — 825 de alcance por seguidor

**Meta no permite medirlo por pieza en reels.** Textual, contra la API real:

```
(#100) The Media Insights API does not support the follows metric
       for this media product type.
(#100) ... does not support the profile_visits metric ...
```

En feed sí existen esas métricas — y el resultado es demoledor: **484 publicaciones de feed produjeron 1 (un) seguidor** y 16 visitas al perfil. Confirma la sección 2.A desde otro ángulo.

La única vía es a nivel cuenta (`instagram-insights-cuenta-diario.csv`, 29 días, 11-ago → 8-sep):

| | |
|---|---|
| Seguidores ganados | **+154** (5,3/día) |
| Alcance de cuenta | 127.059 |
| **Alcance necesario por seguidor** | **~825** |
| Correlación alcance ↔ seguidores nuevos | **rho = +0,67** |
| Correlación views de reels ↔ seguidores | rho = +0,39 |

Los días de más alcance son los de más seguidores, consistentemente:

| Fecha | Alcance | Seguidores |
|---|---|---|
| 30-ago | 12.760 | **19** |
| 23-ago | 10.580 | 12 |
| 29-ago | 8.033 | 10 |
| 21-ago | 1.925 | **0** |
| 2-sep | 1.623 | 2 |

**Conclusión: el alcance sí compone.** Un reel que hace 30.000 de alcance vale ~36 seguidores. Perseguir picos no es vanidad — es el mecanismo de crecimiento.

Ojo con el límite: `follower_count` **solo admite los últimos 30 días, excluyendo hoy**. No se puede reconstruir historia más atrás. Conviene correr el export una vez por mes para ir acumulando la serie.

---

## 4. Las métricas nuevas (segundo export)

### `reels_skip_rate` — confirma la tesis de retención desde el otro lado

Solo está disponible en **37 de 302 reels** (Meta lo reporta de forma despareja, no depende de la fecha). Pero en esos 37 el patrón es contundente:

| Cuartil | Skip rate (med) | Views (med) | Watch time |
|---|---|---|---|
| Q1 (menos skip) | 42 % | **1.365** | 9,4 s |
| Q2 | 49 % | 1.298 | 5,8 s |
| Q3 | 57 % | 312 | 4,0 s |
| Q4 (más skip) | 68 % | **180** | 3,5 s |

**7,6× entre el mejor y el peor cuartil.** La mediana global de skip rate es **55 %**: más de la mitad de la gente pasa de largo. Ese es el número a atacar.

### `reposts` — la señal más limpia de todas

| | n | Views (med) |
|---|---|---|
| Reels con ≥1 repost | 112 | **1.632** |
| Reels con 0 reposts | 190 | 238 |

**6,9×.** 1.031 reposts en total. Junto con shares, confirma que la distribución la hace la gente compartiendo, no el alcance inicial.

### Un canal que estamos dejando sin usar

`crossposted_views` y `facebook_views` fallan con:

> *"El error se produce si crossposted_views o facebook_views se usan en reels que no se incluyeron en publicaciones cruzadas en Facebook."*

O sea: **ningún reel se está publicando también en Facebook.** Es una casilla en la configuración de la cuenta. Alcance adicional gratis, sin producir nada nuevo. Vale la pena probarlo aunque sea un mes.

---

## 5. Lo que todavía no sabemos

| Pregunta | Cómo responderla |
|---|---|
| ¿Cuánto del efecto "video real" es el video y cuánto la noticia? | Requiere un experimento: forzar slideshow en noticias que SÍ tienen tráiler, al azar, durante 2 semanas. Caro, pero es la única forma de separarlo. |
| ~~¿La duración del reel afecta la retención?~~ ✅ **Ya se puede medir** (§9.1): la duración se guarda al renderizar, así que retención = `ig_reel_avg_watch_seconds / ig_reel_duration_seconds`. No hacía falta derivarla de la API. Falta acumular piezas nuevas para responderla. |
| ¿Por qué agosto tuvo 7× shares? | Los 5 picos históricos son todos del 30-jul al 30-ago. Vale mirar qué se publicó ahí que no se publicó antes ni después. |
| ¿El horario de 14h es causal? | Un A/B real: alternar el mismo tipo de noticia entre franjas durante un mes. |
| ¿Por qué `reels_skip_rate` solo aparece en 37 de 302? | Probablemente un umbral mínimo de reproducciones. No documentado. |

---

## 6. Trampas técnicas ya conocidas

Cosas que costaron corridas y no deberían repetirse:

- **`dotnet run --project` usa el directorio del PROYECTO como working dir**, no la raíz del repo. Un output con ruta relativa cae en `scraper/AnimeIndex.Scraper/`. Ya está resuelto con ruta absoluta + `if-no-files-found: error`, porque un artifact faltante era solo un *warning* y el job daba verde sin entregar nada.
- **Truncar captions con `s[..max]` parte emojis al medio** (son pares de surrogates UTF-16) y explota recién al codificar el archivo, después de las ~800 llamadas a la API. Usar `TruncateText`.
- **Pedir una métrica no soportada rompe TODA la llamada**, no solo esa métrica. Por eso el export ahora degrada a pedirlas de a una y cachea cuáles no van.
- **La doc de Meta diverge de la realidad**: documenta `error_subcode 33` para falta de permisos y en producción devuelve `code 10`. Verificar contra la API, no contra la doc.
- **`String.Replace`/`Contains` con `OrdinalIgnoreCase` no pliega acentos.** La `ñ` no matchea la `n`. Esto mató la escalera de búsqueda de tráilers durante ~2 semanas (ver `StripSpanishSuffix`).
- El CSV se escribe en **UTF-8 con BOM** para que Excel no rompa los acentos.
- **No decidir "falta permiso" desde una sola respuesta 400.** La primera versión
  lo hacía y dio un falso positivo: el token TENÍA `instagram_manage_insights`
  (verificado por `--token-scopes` en el paso anterior del mismo job) pero el 400
  de una métrica no soportada matcheó la heurística y el export murió en la
  primera pieza culpando al token. Ahora solo se afirma si **ninguna** métrica
  funciona ni siquiera pedida de a una.
- **`follower_count` solo admite los últimos 30 días excluyendo hoy**, y
  `profile_views` exige `metric_type=total_value` (otra forma de respuesta).
- **La mayoría de las "migraciones" de este repo no son migraciones.** EF solo
  reconoce una clase como migración si tiene el PAR
  `[DbContext(typeof(AppDbContext))]` + `[Migration("<id>")]` — normalmente los
  pone el `.Designer.cs` que genera `dotnet ef migrations add`. Las escritas a
  mano no los tienen, así que `dotnet ef migrations list` devuelve **7 de 28**, y
  ni `MigrateAsync` ni el paso "Run migrations" de `deploy.yml` las aplican
  jamás: son documentación del SQL que alguien corrió a mano contra Supabase.
  Eso alcanza mientras el MODELO no toque las columnas nuevas — pero apenas
  agregás una propiedad a la entidad, EF la emite en el INSERT y la primera
  corrida muere con `42703: column "..." does not exist`. Pasó el 10-sep-2026
  (run 34490734605). Al agregar una columna que el modelo va a usar, poné los dos
  atributos y confirmá con `dotnet ef migrations list` que aparece.

---

## 7. Sprint 1 — implementado (10-sep-2026)

Los cuatro cambios de acá no dependen de ninguna hipótesis: son bugs, o
desperdicio medido. Rama `feat/ig-sprint1-retencion`.

### 7.1 El titular estaba donde Instagram lo tapa 🐛

El hallazgo más caro, y no salió de las métricas sino de leer el renderer.
Instagram dibuja **su propia UI encima del video**: arriba el header con el ícono
de cámara (~250 px de 1920), abajo el caption, el usuario, el ticker de audio y
la botonera derecha (**~420 px**). Y en la grilla del perfil el cover se recorta a
4:5, que se come otros ~285 px arriba y abajo.

`DrawCoverText` anclaba el titular al **7 % del borde inferior — y≈1786 de 1920**.
El bloque titular+lede vivía aproximadamente entre y≈1400 y 1790: **casi entero
debajo de la botonera**, y en la grilla del perfil directamente no aparecía. Lo
mismo en las slides de key point (10 % → y≈1728), el crédito CC de la música
(y≈1894, o sea invisible — y es una atribución obligatoria) y el logo del cover
(y=150, justo bajo el ícono de cámara de IG).

Ahora hay una zona segura explícita para las piezas verticales:

| | Valor | Por qué ese y no el mínimo |
|---|---|---|
| `PortraitSafeTop` | 0,15 (≈288 px) | Manda el **recorte 4:5** (285), que es más exigente que el header del reel (250) |
| `PortraitSafeBottom` | 0,23 (≈442 px) | Los 420 de la UI **+ los descendentes** (g, j, p, y bajan por debajo de la línea de base) |

Las piezas cuadradas (carrusel de feed) no tienen chrome encima y conservan su
margen chico. Cubierto por tests que **renderizan de verdad y cuentan píxeles**
(`InstagramSafeAreaTests`): cero píxeles de texto bajo y=1500, cero sobre y=250,
y el 100 % del texto sobrevive al recorte 4:5.

### 7.2 El primer frame ya no arranca en negro

El reel de tráiler tenía `fade=t=in:st=0:d=0.4` sobre el video y el motion-card
`fade=t=in:st=0:d=0.6` sobre el fondo. O sea que en el instante exacto en que se
decide el skip — mediana 55 % — el usuario veía **negro**. Los dos fuera. De paso
arregla la miniatura: IG toma el primer frame cuando el `cover_url` no sube.

El fade del **texto** (`st=0.5:d=0.8:alpha=1`) se mantiene: es la animación de
marca, no un arranque en negro. Adelantarlo es trabajo del Sprint 2.

### 7.3 El caption abre pidiendo el share, no repitiendo el titular

IG corta el caption a ~125 caracteres: ese renglón es lo único que se lee sin
tocar "más". Ahí iba `📰 {headline}` — **exactamente el texto que ya está quemado
en el cover**, o sea que el espacio más valioso del post se gastaba en repetir lo
que el usuario acababa de leer. Y `🔔 Seguinos para más noticias` estaba al final,
debajo de 3-5 párrafos de cuerpo, donde no lo ve nadie.

Ahora abre con un pedido de compartir (5 variantes, elegidas de forma estable por
titular con `PickShareHook` — nada de `GetHashCode`, que .NET aleatoriza por
proceso). El titular no se pierde: sigue en el cover y en las slides.

Razón: **los 48 reels con ≥10 shares (16 % del output) concentran el 62 % de todas
las views**, y los que tienen al menos un repost hacen 6,9× la mediana del resto.
La distribución la hace la gente compartiendo — y no le estábamos pidiendo nada.

### 7.4 Los 7 crons del día pasan a reel

Los dos que salían como carrusel (13 y 15 ART) ahora son reel. Es la acción de la
§2.A y no requiere hipótesis: 484 publicaciones de feed produjeron **1 (un)
seguidor** en cuatro meses. De regalo caen en la franja **13-15 ART**, que es la
mejor medida (mediana 795 a las 14 contra 280-410 del resto), o sea que también
cubre la §2.D sin mover nada más.

El formato `post` sigue disponible a mano por `workflow_dispatch`, y el carrusel
sigue funcionando como **respaldo** cuando el reel falla (`if (reelMediaId is null)`).

### 7.5 Lo que NO se hizo

- **Crossposting a Facebook** — decisión del usuario, queda para más adelante.
- **Bio, campo Nombre y reels fijados** — es trabajo manual en la app, no hay
  código que tocar. Sigue siendo probablemente el 2× más barato disponible: 825
  de alcance por seguidor es el producto de *alcance → visita al perfil* × *visita
  → follow*, y hoy no sabemos cuál de los dos es el cuello (falta `profile_views`
  en el export de cuenta).
- **`InstagramImageService` (renderer de EPISODIOS)** tiene el mismo defecto de
  zona segura: la línea de CTA cae en y≈1747 y la marca de agua en y≈1880. No se
  tocó porque es otra pipeline (stories y carrusel de feed, nunca reels —
  `ReelsEnabled` está en false), el chrome de las stories es bastante más chico
  que el de los reels (~250 px contra 420) y **no hay ni una sola medición de
  stories en todo este documento**. Arreglarlo bien pide re-maquetar el bloque
  entero, no correr dos líneas.

---

## 8. Sprint 2 — implementado (10-sep-2026)

El formato del video. Todo esto se juzga contra `reels_skip_rate`, no contra
views. Antes de tocar nada, así era un reel de tráiler:

| | Antes | Ahora |
|---|---|---|
| Cola muerta al final | 3 slides estáticas (10,5 s) | **1** (solo el CTA, 3,5 s) |
| Tráiler | hasta 45 s | **hasta 90 s** (que se vea entero — ver §8.3) |
| Video en pantalla | banda de 900 px sobre panel abismo = **47 %** | **100 %** (banda nítida + relleno desenfocado) |
| Texto en el frame 0 | ninguno (aparecía a los ~1,4 s) | el **gancho**, completo |
| Salteo del arranque | 1,5 s fijos | 12 % de la duración (1,5–6 s) |

### 8.1 Full-bleed: el tráiler es su propio fondo

`[0:v]` se decodifica una vez y se usa dos: una copia desenfocada llena los
1080×1920 y la banda nítida va centrada encima. El panel abismo con glow que
había de fondo desapareció — con la banda ocupando el 47 % de la pantalla sobre
un fondo muerto, la pieza se leía como "una tarjeta con un video adentro".

El blur se calcula en **270×480 y se amplía**, no a resolución completa: `gblur`
con sigma grande sobre 1080×1920 a 30 fps es carísimo en el runner y el resultado
visible es indistinguible. La banda se capa a 1250 px de alto para que un clip
vertical (los respaldos de X vienen así) pase casi entero.

### 8.2 El gancho, desde el frame 0

`NewsContent` tiene ahora un campo `Hook`: 3-6 palabras que la IA escribe aparte
del titular. El titular no sirve de gancho — ~80 caracteres se rompen en 3-5
líneas chicas, justo lo contrario de lo que frena un scroll.

Va en **una capa propia**, separada del bloque editorial, porque tienen tiempos
distintos: el gancho se compone tal cual desde el frame 0, y el titular sigue
entrando con el slide-up de marca. Sin hook de la IA (heurística, cuota agotada,
modelo que ignoró el campo) se derivan las primeras palabras del titular
**cortando en la cláusula**: "Murió Kentaro Miura, el creador de Berserk" → "Murió
Kentaro Miura", no las primeras N palabras a ciegas.

Las dos capas traen su propio scrim: el fondo dejó de ser el panel abismo
controlado y pasó a ser un frame cualquiera del tráiler.

### 8.3 Duración: el tráiler entero, sin la cola muerta

**Las slides finales pasaron de 3 a 1** (solo el CTA). Los puntos clave vivían en
los últimos 10,5 s de un reel con 12 % de retención, o sea que no los veía nadie
mientras hundían la finalización y el loop. No se pierde contenido — el titular
va quemado sobre el video y el cuerpo entero está en el caption.

**El TRÁILER, en cambio, va entero: `TrailerClipSeconds` = 90.** Este número dio
dos vueltas y la segunda corrige un error propio: primero se bajó de 45 a 26
buscando subir la finalización, y el 10-sep-2026 se subió a 90 por decisión del
usuario. Ganó el mismo argumento que ya habíamos usado para rechazar "acortar a
8 s", solo que llevado hasta el final: `ig_reels_avg_watch_time` está **acotado
por la duración**, así que recortar el tráiler le pone un techo a la única
métrica que correlaciona con views (rho 0,76).

El detalle que decide: **acortar NO recupera watch time.** Quien abandona en el
segundo 7 lo abandona igual dure 26 o 90 — la curva de caída no cambia. Lo único
que cambia es el techo: con 26 s, el espectador enganchado que habría mirado 40 s
mira 26. Y en un negocio de cola larga (el top 10 se lleva el 39 % de las views)
ese espectador enganchado es justo el que comparte. Recortarle el video para
mejorar un promedio es el trade-off equivocado.

Lo que sí se paga: tasa de finalización y loop, que IG cuenta como reproducción
nueva. Costo real y asumido. Lo que hay que vigilar en el próximo export es
`reels_skip_rate` (que no depende de la duración) y el watch time absoluto — si
el watch time sube con el tráiler largo, la decisión fue correcta.

**Y se paga tiempo de render.** Verificado localmente con un tráiler sintético de
60 s (que da un reel de 57,5 s): el render pasó de ~50 s a **166 s**. Con el cap
de 90 s serían ~280 s de ffmpeg, y el job de `news-cron` topea a 12 minutos.
Por eso el preset de x264 bajó de `medium` a **`fast`**: medido, **113 s contra
166 s (−32 %)**, con el mismo tamaño de salida — el bitrate está fijado en 6 Mbps,
así que el preset no mueve el peso, solo la eficiencia de compresión, y a esa tasa
sobre 1080×1920 la diferencia es imperceptible. Deja el peor caso en ~6,5 min de
job contra el cap de 12.

`TrailerStartSkip` era la constante 1,5 s; ahora es el 12 % de la duración con
piso 1,5 y techo 6. Los PV oficiales abren con logos de distribuidora que duran
3-6 s: en un tráiler de 90 s, saltearse solo 1,5 s era regalarle el arranque del
reel a un logo. El fade-out del audio bajó de 1,8 s a 0,9.

### 8.4 Verificado contra ffmpeg de verdad, no solo contra los argumentos

Los tests de este repo arman la línea de comandos y le hacen asertos — sirven,
pero **no prueban que el grafo corra**. Un `filter_complex` mal armado no rompe
la corrida: `PublishReelAsync` lo atrapa, loguea un warning y cae al slideshow.
O sea que un error acá se manifestaría como "todos los reels salen sin video"
sin ninguna señal clara.

Así que el grafo se corrió de punta a punta con ffmpeg local, con un tráiler
sintético 16:9 y otro vertical. Dos defectos que los asertos de string no podían
ver y el render sí:

1. **El gancho quedaba tangente al filo de la banda** (segunda línea terminaba en
   y≈583, banda desde y≈579). Se corrigió bajando el centro de la banda al 50 %,
   achicando el cuerpo del gancho de 132 a 112 y acortando `MaxHookChars` a 26.
   El scrim superior también se reforzó: donde cae el gancho tenía un alpha de
   ~0x45, insuficiente para texto blanco sobre una escena clara.
2. **El crédito CC se encimaba con el titular.** En vertical `SafeBottom` devuelve
   lo mismo para cualquier margen, así que los dos caían en la MISMA línea de
   base. En el reel de tráiler `musicCredit` es siempre null, pero el defecto
   estaba latente y la atribución es obligatoria.

### 8.5 Verificado en producción (10-sep-2026)

Tres corridas reales del pipeline desde la rama, antes de mergear.

| Run | Noticia | Formato | Duración | Salteo |
|---|---|---|---|---|
| 34492040634 | Película de Naruto en la NYCC 2026 | tráiler | **29,5 s** | **3,7 s** |
| 34493207860 | ~~ANÁLISIS – Marvel's Wolverine~~ ⚠️ | slideshow | **17,6 s** (5 slides) | — |
| 34494591345 | Witch on the Holy Night, estreno 2027 | tráiler | **29,5 s** | **6 s** (techo) |

Lo que confirmaron:

- **La duración da exacta en las dos ramas del render.** 29,5 s en el de tráiler
  (26 + 1 slide) y 17,6 s en el slideshow, que coincide al decimal con
  `SlideshowSeconds(5)`. El cableado de `RenderedReel` está bien en ambas.
- **El salteo es proporcional de verdad**: 3,7 s en un tráiler medio y 6 s (el
  techo) en uno de ≥50 s, no el 1,5 fijo de antes.
- **El tiempo de corrida despeja la duda del grafo nuevo**: 2,7 min el paso del
  pipeline (job completo 4,1) contra un cap de 12. Desenfocar en 270×480 y
  ampliar lo mantiene barato aunque el tráiler se decodifique dos veces.
- **La escalera de candidatos funciona**: en el run 3 el primer tráiler no bajó y
  pasó al siguiente en vez de rendirse al slideshow.

⚠️ **El run 2 destapó un falso positivo del propio §9.2.** Publicó *"ANÁLISIS –
Marvel's Wolverine"*, una reseña de VIDEOJUEGO del feed de Crunchyroll: la marca
"marvel" sola se llevaba los 8 puntos de crossover. Se había anticipado el
problema para "netflix" y "disney" (aparecen como distribuidor) pero no para las
franquicias occidentales. Corregido con el gate `MentionsAnimeWorld` + una
penalización para reseñas; el run 3 ya eligió bien.

Ojo con la fuerza de esa evidencia: el run 3 tenía otro pool (la nota de Marvel
ya estaba consumida), así que **no es un A/B controlado del fix**. Lo que
verifica el fix es el test de regresión con el titular real; el run 3 solo
confirma que nada más se rompió.

⚠️ **Las duraciones de 29,5 s de la tabla son de ANTES de subir
`TrailerClipSeconds` a 90** (§8.3). Con el valor actual, un tráiler de 60 s sale
como un reel de ~63,5 s. Las corridas siguen sirviendo para lo que verificaron
—que la duración calculada coincide con la renderizada, que el salteo es
proporcional y que el tiempo de corrida está lejos del cap— pero el número
absoluto ya no aplica.

### 8.5.1 El caption que salió pobre, y por qué

El reel de *Witch on the Holy Night* se terminó borrando a mano: el caption tenía
dos renglones y cerraba con *"Te lo contamos completo en SheicobAnime"* sin nada
detrás. Causa, del log:

```
Gemini blocked the prompt: PROHIBITED_CONTENT
→ AiRewrite: rewrite failed — using heuristic
```

El filtro de seguridad de Gemini dio un **falso positivo** sobre una noticia de
anime perfectamente normal, así que el caption lo escribió `BuildHeuristic`. Y
ese fallback tenía dos defectos que venían de antes y recién ahí se vieron:

1. **Arrancaba a leer el artículo desde el párrafo 3**, para "no repetir las
   slides". Con un artículo de 3 párrafos, el caption quedaba siendo SOLO el
   lede. Peor todavía desde el sprint 2: el reel de tráiler ya no lleva slides de
   puntos clave, así que esos párrafos no se muestran en ningún lado — saltearlos
   era tirar la única información que teníamos. Ahora recorre todo el artículo y
   corta por presupuesto de caracteres, no por índice.
2. **Prometía una nota que no existe.** SheicobAnime es un índice de series, no
   un medio: no hay artículo que leer, y el original es de la fuente, que por
   regla no acreditamos. Quien tocaba "link en la bio" buscando la nota completa
   no encontraba nada. El cierre ahora invita a comentar, que es lo que sí
   podemos cumplir — y de paso es señal de ranking.

Y la causa raíz: un bloqueo de seguridad ahora **reintenta en el modelo de
respaldo** (Gemma tiene otros umbrales), igual que ya se hacía con el 429 de
cuota. La diferencia entre que el modelo conteste o no es un caption editorial
completo contra el heurístico pelado.

### 8.7 Cuatro cambios que salieron de mirar a la competencia (10-sep-2026)

Relevados contra @isekai.feed (§10), que hace lo mismo con 11× los seguidores:

1. **Línea de cuándo y dónde** — `1 DE JULIO 2026 · CRUNCHYROLL`, en cian. Es la
   información más práctica que puede dar un reel de noticias y la estábamos
   tirando: venía en el artículo y no se renderizaba en ningún lado. La extrae la
   IA (`cuando` / `donde` en `NewsContent`), **solo del material de referencia**:
   el prompt insiste dos veces en que un dato inventado queda quemado sobre el
   video, así que ante la duda va null. Ojo: el grounding con `google_search` da
   429 el 100 % de las veces, o sea que la IA **no busca** la fecha, la extrae.
2. **Marca de agua `SEGUINOS EN @handle`** al pie de la zona segura. Viaja con el
   video: un reel reposteado no decía en ningún lado quién lo hizo, y los reels
   con al menos un repost hacen 6,9× la mediana del resto — era alcance sin
   atribuir. Va en la capa editorial y no en la del gancho porque el scrim
   inferior (alpha 0xFC) se la comía entera (verificado renderizando).
3. **Acento de color en el gancho**: la última línea en cian cuando ocupa más de
   una. Sin eso el gancho es blanco plano y no hay jerarquía *dentro* del texto.
   Con una sola línea se deja blanca — pintarlo entero lo vuelve un cartel.
4. **Chip de categoría (`NOTICIAS`) en el reel de tráiler.** El cover ya lo tenía
   y el reel no: se entraba sin saber si esto es una noticia, un tráiler o una
   opinión.

Y dos que cambian la jerarquía, no solo suman elementos:

5. **El pie usa el LEDE, no el titular.** Antes ponía el titular completo a 72 px
   en 3 líneas, que pesaba más que el propio gancho y encima **repetía lo que el
   gancho ya decía** (el gancho se deriva del titular): salía
   *"CLOVERWORKS PREPARA"* arriba y *"CLOVERWORKS PREPARA GRANDES ANUNCIOS…"*
   abajo. El lede existe justamente para ampliar el titular, así que suma.
6. **La portada vertical muestra el GANCHO, no el titular de 5 líneas.** Era el
   problema visual más grande de la cuenta: llenaba la tarjeta de mayúsculas,
   tapaba la foto y hacía que la grilla entera se leyera como un muro de texto.
   Peor, el auto-fit lo achicaba para que entrara — cuanto más larga la noticia,
   más denso el resultado. El carrusel cuadrado se queda con el titular: ahí no
   hay video que acompañe y la tarjeta ES la noticia.

### 8.6 Lo que quedó pendiente

**El cierre no loopea.** El reel termina en la tarjeta estática de CTA, así que
el corte al reinicio se nota. Un loop limpio pide **sacar esa última slide** y
terminar sobre el video — pero eso elimina el "link en la bio" del video (sigue
en el caption). Es una decisión de contenido, no técnica: el cambio es poner
`maxKeyPoints: 0` → lista vacía de `infoSlides` en `PublishReelAsync`.

---

## 9. Sprint 3 — implementado (10-sep-2026)

La cola y el loop de medición. Los sprints 1 y 2 mejoran cada pieza; este cambia
**qué se publica** y **si podemos saber si algo funcionó**.

### 9.1 El loop de medición estaba abierto

Hasta acá medir costaba una corrida manual de 8 minutos y un CSV que alguien
tenía que analizar. Peor: el CSV baja las ~800 piezas de la cuenta pero **no sabe
qué noticia era cada una**, así que "¿qué tipo de titular funciona?" pedía
cruzarlo contra los logs a mano. Resultado: se medía cada varios meses y en el
medio se publicaban 7 piezas por día a ciegas.

Ahora `--insights-sync` (workflow semanal) guarda las métricas del reel en
`anime_news_items`, al lado del titular. Semanal y no diario a propósito: las
métricas de un reel siguen subiendo durante días, así que sincronizar 60 días
para atrás una vez por semana captura la maduración sin gastar llamadas de más.

**Y guarda la duración renderizada**, que la §5 listaba como pregunta abierta
proponiendo derivarla de la API. No hace falta: la sabemos exacta al generar el
video, y ahora viaja con el MP4 (`RenderedReel`). Es el dato que faltaba para
calcular **retención real** (watch time ÷ duración) en vez de watch time a secas
— justo la confusión que hacía parecer dos hallazgos distintos a lo que en buena
medida era el mismo (§2.C).

También se normaliza `ig_reels_avg_watch_time`, que Meta devuelve en
**milisegundos**: guardarlo crudo dejaba "6900" donde el playbook habla de 6,9 s.

### 9.2 El selector no sabía dónde estaban los picos

El prompt pedía "la noticia más relevante/viral" — que un editor de anime lee
como relevancia **dentro del nicho**. Pero los picos medidos fueron otra cosa:
Free Fire × anime (35.192), The Ninth Jedi de Star Wars (27.622), Chainsaw Man en
teatro (18.125), los 70 años de Toei (11.315). Todos cruces con audiencias que ya
son masivas **afuera** del anime.

El prompt ahora ordena por **techo de audiencia** ("¿esto le interesa a alguien
que NO sigue anime?"), y `HeuristicNewsScore` —que es lo que corre cada vez que
Gemini devuelve 429, o sea seguido— suma una lista de crossovers que **no existía**:
la heurística les daba 0 puntos a los dos mejores posts de la historia de la cuenta.

Dos detalles que salieron de escribir los tests:

- El crossover pesa **8 y no 5**, porque tiene que ganarle a un anuncio completo
  de una obra de nicho (estreno + adaptación = 6). Con 5, *"Star Wars: The Ninth
  Jedi presenta su serie anime"* perdía 5 a 6 contra *"una novela ligera poco
  conocida confirma adaptación al anime"* — exactamente la inversión que el
  cambio venía a arreglar.
- **"Netflix" y "Disney" NO están en la lista.** En noticias de anime aparecen
  casi siempre como distribuidor ("llega a Netflix"), que no es un cruce de
  audiencias sino dónde se ve. Incluirlas le daría 8 puntos a cualquier noticia
  rutinaria de licencias.
- **La marca sola no alcanza: tiene que haber contexto de anime.** Esto NO estaba
  en la primera versión y costó un post publicado (§8.5): *"ANÁLISIS – Marvel's
  Wolverine"*, una reseña de videojuego, se llevó los 8 puntos por nombrar a
  Marvel. El gate `MentionsAnimeWorld` exige que el titular además diga
  anime/manga, nombre una franquicia conocida, o traiga un verbo de cruce — los
  tres, porque *"Free Fire anuncia una colaboración con Attack on Titan"* no dice
  "anime" en ningún lado y obviamente sí es el cruce que buscamos. Va con una
  penalización de −6 para reseñas y análisis, que en el feed de Crunchyroll son
  casi siempre de videojuegos.

**Few-shot con datos propios:** cuando ya hay ≥10 reels medidos, el prompt
incluye los 5 titulares que más y los 5 que menos alcance hicieron *en esta
cuenta*. Ancla la intuición genérica del modelo en lo que realmente funcionó.
Sin datos todavía, devuelve string vacío y el prompt sigue igual — nunca puede
tumbar la selección del día.

### 9.3 Las dos métricas que dicen dónde está el cuello

Los ~825 de alcance por seguidor son el **producto** de dos tasas: alcance →
visita al perfil, y visita → follow. Sin separarlas no se sabe si el problema es
que no llegamos a la gente o que llegamos y el perfil no convierte — que son dos
trabajos completamente distintos.

El export de cuenta ahora trae:

- **`profile_views`** — el segundo multiplicador.
- **`reach` partido por `follow_type`** — cuánto del alcance es de NO seguidores.
  Es el predictor directo del crecimiento: alcance sobre gente que ya nos sigue
  no puede traer seguidores nuevos.

Las dos exigen `metric_type=total_value`, que **no devuelve serie diaria**: con
un rango since/until da un número para todo el período. Así que se piden día por
día — ~60 llamadas para 30 días, contra las ~800 del export de piezas.

### 9.4 Lo que hay que mirar en la primera corrida

`profile_views` y el `reach` por `follow_type` están escritos contra la forma
documentada de la respuesta (`total_value.breakdowns[].results[]`), pero **no se
pudieron verificar contra la API real** desde acá — hace falta el token. Los dos
son best-effort: si Meta responde otra cosa, se loguea en Debug y las columnas
quedan vacías, sin romper el export que ya funciona. Vale mirar el primer CSV
para confirmar que las columnas `profile_views` y `reach_follower` /
`reach_non_follower` traen números.

---

## 10. Benchmark: la competencia directa (10-sep-2026)

Relevado mirando las cuentas en vivo. El par real **no** son Kudasai (129K, medio
establecido) ni Crunchyroll LA (1.4M, marca oficial): es **@isekai.feed**, que
hace exactamente lo mismo —noticias y tráilers de anime en español, con portadas
de póster editorial— y publica hasta las mismas notas el mismo día.

| | @sheicobanime | @isekai.feed |
|---|---|---|
| Seguidores | 350 | **3.817** |
| Siguiendo | 154 | 25 |
| Campo Nombre | `SheicobAnime` | `Isekai Feed \| Anime News & Trailers` |
| Bio | `#anime #animelover #animefans` | 4 líneas: qué es · qué recibís · diferencial · CTA |
| Destacadas | **0** | **10** |
| Reels fijados | 0 | 3 |

### Lo que dicen sus vistas

La pestaña de reels muestra los contadores. Sus cuatro primeros:

| Reel | Vistas |
|---|---|
| 📌 estreno confirmado (+ fecha + plataforma) | **140K** |
| 📌 tráiler, estreno anunciado | **175K** |
| 📌 Dragon Ball, confirmado para 2027 | **157K** |
| reseña del videojuego de Wolverine | **188** |

Tres lecturas:

1. **Nuestra mediana (341) le gana a su cuarto reel (188).** Su ventaja no es
   rendir parejo: son **tres hits**. Es validación externa de la tesis de §1 —
   esto no es un negocio de subir la mediana.
2. **El techo está mal calibrado en este documento.** Tratamos los 35.192 views
   del Free Fire como "el pico". El pico real del nicho, mismo formato y mismo
   idioma, es **175K: 5× más**.
3. **La reseña de videojuego hizo 188.** Es la misma clase de nota que nuestro
   selector eligió el 10-sep y que arregló el gate `MentionsAnimeWorld` (§9.2).
   Confirmación externa, a tres órdenes de magnitud.

⚠️ Son **4 contadores**, no su distribución completa. El patrón es sugestivo, no
concluyente.

### Su fórmula de reel, y qué nos faltaba

```
[TRÁILER]                      ← chip de categoría
The Eminence in Shadow         ← obra, chica
ESTRENO ANUNCIADO              ← el hecho, ENORME, con acento de color
1 DE JULIO 2026 · CRUNCHYROLL  ← cuándo y dónde
     [ video ]
Síguenos en ✦ @isekai.feed     ← marca de agua fija
```

De ahí salieron los cuatro cambios de §11. Lo que **no** copiamos: ellos enmarcan
el video con un borde y nosotros vamos full-bleed con fondo desenfocado. No hay
evidencia de que el marco rinda más y el full-bleed es de esta semana — que lo
decida el skip rate.

**Lo que sigue siendo de ellos y no nuestro: la capa de conversión.** Nombre
buscable, bio con propuesta, 10 destacadas y 3 reels fijados. Nada de eso es
código: es media hora en la app, y es el factor que separa "alcance" de
"seguidores" en la ecuación de los ~825 (§3).

---

## 11. Resumen

> El feed no sirve (484 posts = 1 seguidor). El video real duplica todo. El watch
> time es la variable que manda (rho 0,76). El 16 % de los reels produce el 62 %
> del alcance. Y el alcance **sí** compone: ~825 de alcance = 1 seguidor nuevo.
>
> **La palanca es la retención en los primeros segundos. La estrategia es producir
> más candidatos a explotar, no subir la mediana.**

### El plan por sprints

**Sprint 1 — hecho (§7).** Zona segura del texto · primer frame sin negro ·
caption invertido hacia el share · los 7 crons a reel (y de paso la franja 13-15).

**Sprint 2 — hecho (§8).** Full-bleed con fondo desenfocado · gancho desde el
frame 0 · tráiler entero sin cola muerta · salteo proporcional del arranque. Pendiente ahí: el
cierre en loop, que es una decisión de contenido (§8.6).

**Sprint 3 — hecho (§9).** Selección por techo de audiencia + crossovers en la
heurística · métricas y duración persistidas por pieza (`--insights-sync`
semanal) · few-shot del selector con datos propios · `profile_views` y `reach`
por `follow_type`.

**Lo que sigue, en orden:**
1. **Mirar los números.** En ~2 semanas, `reels_skip_rate` (mediana 55 % → ¿45 %?)
   y retención real, que recién ahora se puede calcular. Los sprints 1 y 2 se
   juzgan con eso, no con views.
2. **Bio, campo Nombre y reels fijados** — manual, y probablemente el 2× más
   barato que queda. Con `profile_views` ya se va a poder ver si el cuello está
   ahí.
3. **Cierre en loop del reel** (§8.6): decisión de contenido.
4. **Crossposting a Facebook**: pospuesto por decisión del usuario.
5. **El experimento que separa video de noticia** (§5): forzar slideshow al azar
   en noticias que SÍ tienen tráiler. Caro, pero es lo único que responde cuánto
   del efecto "video real" es el video.

**Techo estructural, para tenerlo presente:** los reels publicados por la Graph
API **no pueden usar audio de la biblioteca de Instagram**, así que nunca aparecen
en la página de un audio en tendencia. El audio original del tráiler es la
decisión correcta dado eso. Un salto grande por esa vía pide un paso semi-manual
para 1-2 reels "apuesta" por semana, no un cambio en el pipeline.

**El número de arriba de todo** sigue siendo alcance de cuenta por día: 4.381 hoy,
~8.800 para duplicar el crecimiento a ~11 seguidores/día.
