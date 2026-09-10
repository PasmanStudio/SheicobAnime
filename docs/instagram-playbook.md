# Playbook de Instagram — SheicobAnime

**Última actualización:** 10-sep-2026 (Sprint 1 implementado — ver §7)
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
> *loop* (que IG cuenta como reproducción nueva). El objetivo correcto es **~30 s**
> (`TrailerClipSeconds` 45 → 25-28 y 1 sola info slide), no 8.
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
| ¿La duración del reel afecta la retención? | El export no trae duración. Se puede derivar de `ig_reels_video_view_total_time / views` contra `ig_reels_avg_watch_time`, o agregarla al pipeline al generar el video. |
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

## 8. Resumen

> El feed no sirve (484 posts = 1 seguidor). El video real duplica todo. El watch
> time es la variable que manda (rho 0,76). El 16 % de los reels produce el 62 %
> del alcance. Y el alcance **sí** compone: ~825 de alcance = 1 seguidor nuevo.
>
> **La palanca es la retención en los primeros segundos. La estrategia es producir
> más candidatos a explotar, no subir la mediana.**

### El plan por sprints

**Sprint 1 — hecho (§7).** Zona segura del texto · primer frame sin negro ·
caption invertido hacia el share · los 7 crons a reel (y de paso la franja 13-15).

**Sprint 2 — formato del video.** Todo se juzga contra `reels_skip_rate`, no
contra views (demasiado ruidoso con una cola así):
1. **Full-bleed con fondo desenfocado.** Hoy el tráiler es una banda de 1080×900
   sobre 1920 — **el 47 % de la pantalla** — flotando en un panel abismo vacío.
   Un `split` + `scale`/`crop`/`gblur` del propio clip detrás lo convierte de "una
   tarjeta con un video adentro" en "un video".
2. **Campo `hook` en el rewrite**: 3-6 palabras enormes desde el frame 0, sin
   fade. El titular tiene ~80 caracteres y se rompe en 3-5 líneas chicas; eso no
   es un gancho.
3. **Duración a ~30 s**: `TrailerClipSeconds` 45 → 25-28 y 1 sola info slide.
4. **Skip inteligente del arranque**: `TrailerStartSkip` es la constante 1,5 s,
   pero los tráilers abren con logos de distribuidora 3-6 s. `min(6, max(1.5,
   dur×0.12))`, o `blackdetect`.
5. **Cierre en loop**: hoy termina con `afade` de 1,8 s sobre una tarjeta estática.

**Sprint 3 — la cola y el loop de medición.**
6. **Prompt de selección con criterio de audiencia externa.** Hoy pide "la más
   relevante/viral" — o sea relevancia *dentro del nicho*. Los picos fueron
   crossovers con audiencias masivas de afuera (Free Fire, Star Wars, LEGO), y ni
   el prompt ni `HeuristicNewsScore` tienen una sola palabra de gaming o IP
   occidental.
7. **Persistir insights y duración renderizada por pieza en la DB.** Hoy medir
   cuesta una corrida manual y un CSV, así que se mide cada varios meses y en el
   medio se decide a ciegas. Con `IgReelMediaId` ya guardado, un job semanal
   convierte cada pregunta futura en una query — y habilita hacer few-shot del
   selector con nuestros propios resultados. La duración **no hace falta derivarla
   de la API** (§5): la conocemos exacta al renderizar.
8. **`reach` con `breakdown=follow_type`** (qué % del alcance es de NO seguidores
   — el predictor directo del crecimiento) y **`profile_views`**, para saber si el
   cuello de los 825 es *alcance → perfil* o *perfil → follow*.

**Techo estructural, para tenerlo presente:** los reels publicados por la Graph
API **no pueden usar audio de la biblioteca de Instagram**, así que nunca aparecen
en la página de un audio en tendencia. El audio original del tráiler es la
decisión correcta dado eso. Un salto grande por esa vía pide un paso semi-manual
para 1-2 reels "apuesta" por semana, no un cambio en el pipeline.

**El número de arriba de todo** sigue siendo alcance de cuenta por día: 4.381 hoy,
~8.800 para duplicar el crecimiento a ~11 seguidores/día.
