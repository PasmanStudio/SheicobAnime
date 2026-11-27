# Metraje del video largo: de dónde sale cada plano

Cada tramo de la narración muestra planos del video oficial que corresponde a lo que se está
contando. Si se habla del videoclip, planos del videoclip; si se habla de la temporada 2, de los
tráileres y teasers oficiales de la temporada 2. Dentro de esa fuente, CLIP
(`Scripts/frame_match.py`) elige el plano que se parece a lo que dice la toma (`scene: …`).

## El manifiesto: `footage` en plan.json

```json
"footage": [
  { "id": "s1-trailer", "kind": "trailer", "season": 1,
    "url": "https://www.youtube.com/watch?v=…", "label": "Tráiler oficial T1 (Netflix)",
    "keywords": ["primer Cyberpunk Edgerunners", "primera temporada"] },
  { "id": "s2-trailer", "kind": "trailer", "season": 2, "url": "…", "keywords": ["Edgerunners Dos"] },
  { "id": "mv-watson", "kind": "mv", "season": 2, "url": "…", "keywords": ["Watson", "Petite Meller"] }
]
```

- `kind`: `trailer`, `teaser`, `pv`, `mv`, `opening`, `ending` o `clip`. A un videoclip (`mv`,
  `opening`, `ending`) no se le recortan el principio y el final como a un tráiler.
- `url`: solo de canales oficiales (la serie, el estudio, la plataforma; con verificación).
  Para comprobar el canal antes de agregarlo:
  `yt-dlp --skip-download --print "%(channel)s | %(channel_is_verified)s | %(title)s" <url>`.
- **Sin subtítulos quemados.** Los tráileres de Netflix con diálogo traen los subtítulos en
  inglés metidos en la imagen, a veces arriba y a veces abajo, y se pisan con los nuestros. Recortar
  no alcanza. Antes de agregar un video, mirar algunos fotogramas; en Edgerunners se usó el teaser
  musical de la T1 en lugar del tráiler de Studio Trigger.
- La primera vez se baja a `footage/<id>.mp4` con yt-dlp. **Se baja en local**: YouTube bloquea
  a los runners de GitHub. Después se reutiliza el archivo, así que un `--plan` no vuelve a bajar nada.
- Sin `footage`, el video usa el tráiler de AniList (o `--clip`), como antes.

## Cómo elige la fuente cada escena (`FootageRouter`, sin IA)

1. `"footage": ["s1-trailer"]` en la escena manda. Sirve para corregir a mano, por ejemplo
   cuando "no habría una segunda temporada" habla en realidad de la primera.
2. Si no, gana la fuente con más pistas en la narración:
   - cada palabra clave de la fuente suma 2;
   - "videoclip", "video musical" o "clip musical" suman 3 a los videoclips;
   - "segunda temporada" o "temporada 2" suman 1 a las fuentes de esa temporada.

   Si hay empate, las fuentes empatadas se usan juntas (el tráiler y el teaser de la T2).
3. Una escena sin pistas sigue el tema de la anterior.
4. Una primera escena sin pistas va a la primera fuente del manifiesto.

Si la fuente de la escena se queda sin planos nuevos, se usa un plano nuevo de otra fuente. Un
plano solo se repite cuando ya no queda ninguno sin usar en todo el video. El teaser y el tráiler
comparten planos: si uno se usa, cuenta como usado en las dos fuentes.

## Presentaciones: cada cosa se muestra una sola vez (`IntroductionLedger`)

Un personaje, póster, logo, persona o tarjeta se muestra cuando se lo presenta, y nunca más.
Cuando la voz lo vuelve a nombrar, esa toma pasa a metraje de la fuente de la escena.

- **Logos y personajes con `+`:** se muestran solo los que todavía no aparecieron.
- **Tarjetas:** una tarjeta igual (mismo texto y misma imagen) no se repite. Si la imagen que la
  acompaña ya se mostró, la tarjeta va sola, sin esa imagen.

Se aplica al renderizar, sin tocar plan.json.

## Para revisar sin ver el video

El render deja `director.md` con tres secciones:

- la fuente de cada escena y por qué se eligió;
- cada mención ya presentada y qué se mostró en su lugar;
- una línea por toma con la fuente, el segundo y si el plano lo eligió CLIP.

## Re-render sin IA

```
dotnet run --project scraper/AnimeIndex.Scraper -c Release -- --youtube --plan <dir>/plan.json --anime "<serie>" --no-mentions
```

Con la voz clonada ya generada (`scene_NN.wav` en la carpeta), también hacen falta `--voice-ref`
y `--voice-ref-text`. Con todas las escenas ya generadas no se vuelve a llamar a Qwen. Un plan
con tomas ancladas no consulta la wiki de la serie.
