# Playbook de Instagram — SheicobAnime

**Última actualización:** 10-sep-2026
**Base de datos del análisis:** 785 piezas publicadas (301 reels + 484 feed), 16-may → 9-sep-2026, exportadas de la Graph API de Meta.

Este documento existe para poder retomar el tema en otra conversación sin repetir la investigación. Tiene los números medidos, qué hacer con ellos, y qué todavía no sabemos.

---

## 0. Cómo regenerar los datos

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

### 🔴 A. Dejar de gastar el 62 % del output en feed

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
- Acortar la duración total: un reel de 8 s con 6 s de watch time retiene 75 %; uno de 30 s con 6 s retiene 20 %, y el algoritmo lo trata distinto.

**`reels_skip_rate` ya está agregado al export** — la próxima corrida va a decir exactamente cuántos pasan de largo, que es la contracara directa de esto.

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

## 3. Lo que todavía no sabemos

| Pregunta | Cómo responderla |
|---|---|
| **¿Los reels convierten a seguidores?** | `follows` y `profile_visits` ya se agregaron a `ReelMetrics`. La doc de Meta no los lista para REELS, así que el export ahora los pide y **descubre solo** si la API los soporta (degrada métrica por métrica). Falta correr el export con un token válido. |
| ¿Cuánto del efecto "video real" es el video y cuánto la noticia? | Requiere un experimento: forzar slideshow en noticias que SÍ tienen tráiler, al azar, durante 2 semanas. Caro pero es la única forma de separarlo. |
| ¿La duración del reel afecta la retención? | El export no trae duración. Se puede sacar de `ig_reels_video_view_total_time / views` vs `ig_reels_avg_watch_time`, o agregarla al pipeline al generar el video. |
| ¿Por qué agosto tuvo 7× shares? | Vale mirar qué se publicó en agosto que no se publicó en julio/septiembre. Los 5 picos históricos son todos del 30-jul al 30-ago. |
| ¿El horario de 14h es causal? | Un A/B real: alternar el mismo tipo de noticia entre franjas durante un mes. |

---

## 4. Trampas técnicas ya conocidas

Cosas que costaron corridas y no deberían repetirse:

- **`dotnet run --project` usa el directorio del PROYECTO como working dir**, no la raíz del repo. Un output con ruta relativa cae en `scraper/AnimeIndex.Scraper/`. Ya está resuelto con ruta absoluta + `if-no-files-found: error`, porque un artifact faltante era solo un *warning* y el job daba verde sin entregar nada.
- **Truncar captions con `s[..max]` parte emojis al medio** (son pares de surrogates UTF-16) y explota recién al codificar el archivo, después de las ~800 llamadas a la API. Usar `TruncateText`.
- **Pedir una métrica no soportada rompe TODA la llamada**, no solo esa métrica. Por eso el export ahora degrada a pedirlas de a una y cachea cuáles no van.
- **La doc de Meta diverge de la realidad**: documenta `error_subcode 33` para falta de permisos y en producción devuelve `code 10`. Verificar contra la API, no contra la doc.
- **`String.Replace`/`Contains` con `OrdinalIgnoreCase` no pliega acentos.** La `ñ` no matchea la `n`. Esto mató la escalera de búsqueda de tráilers durante ~2 semanas (ver `StripSpanishSuffix`).
- El CSV se escribe en **UTF-8 con BOM** para que Excel no rompa los acentos.

---

## 5. Resumen de una línea

> El feed no sirve, el video real duplica todo, el watch time explica el 76 % de la varianza, y el 16 % de los reels produce el 62 % del alcance. La palanca es retención en los primeros segundos; la estrategia es producir más candidatos a explotar, no subir la mediana.
