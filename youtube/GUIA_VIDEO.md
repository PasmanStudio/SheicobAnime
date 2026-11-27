# Guía del canal — lo que aprendimos revisando videos

Este archivo se manda a Gemini cuando escribe el guion y el storyboard de cada
video (`--guide`). Cada revisión del usuario que deja una regla general se
agrega acá, para que valga en todos los videos siguientes, no solo en el que se
corrigió. Reglas cortas, con un ejemplo real.

## 1. Lo que se dice, se ve — en el momento en que se dice

La imagen acompaña a la voz palabra por palabra. Un video con "un fondo de
escenas lindas" debajo de la narración se siente narrado encima de un clip; uno
donde aparece el póster cuando se nombra la serie, el logo cuando se nombra el
estudio y la placa del premio cuando se dice el premio se siente EXPLICADO.

Por eso el storyboard no dice "qué se ve en esta escena": dice qué se ve
**desde qué palabras**. Cada toma tiene:

- `at`: las 1 a 4 palabras de la narración donde arranca, copiadas exactas.
  El sistema escucha la voz ya grabada, encuentra esas palabras y corta ahí.
- `show`: qué se muestra, con un prefijo que dice de dónde sale la imagen.

| Si la narración nombra… | `show` | De dónde sale | Ejemplo (Cyberpunk: Edgerunners, revisión del 26-nov-2026) |
|---|---|---|---|
| una serie, temporada, película o videojuego | `poster: <título oficial>` | AniList; si no es anime, Wikipedia | "llegó a Netflix… el videojuego Cyberpunk 2077" → `poster: Cyberpunk 2077` |
| un estudio, empresa o plataforma | `logo: <empresa>` (varias con `+`) | infobox de Wikipedia | "entre Studio Trigger y CD Projekt Red" → `logo: Studio Trigger + CD Projekt Red` |
| un premio, cifra, fecha, récord, anuncio | `card: <dato> \| <contexto> \| <imagen opcional>` | placa generada | "ganó el Anime del Año en los Crunchyroll Anime Awards 2023" → `card: Anime del Año \| Crunchyroll Anime Awards 2023 \| poster: Cyberpunk: Edgerunners` |
| un personaje | `character: <nombre completo>` | AniList (también los de temporadas nuevas) | "la trágica vida de David Martínez" → `character: David Martínez` |
| un lugar, una acción, un ambiente | `scene: <descripción en inglés>` | el plano más parecido del tráiler / video oficial | "nos sumergió en Night City" → `scene: futuristic city skyline at night with neon lights` |
| una idea abstracta sin imagen real | `ai: <prompt en inglés>` | imagen generada (pocas: tardan) | "el paso del tiempo" → `ai: an hourglass with neon sand` |

Además, después del storyboard el sistema relee la narración completa y marca
SOLO lo que se haya escapado (`MentionDetector`): cada nombre propio (clasificado
con AniList y Wikipedia), cada fecha, cada cifra ("diez episodios") y cada cita
entre comillas. Lo que no encuentra en ningún lado va como placa con el nombre.
El detalle queda en `mentions_report.md`. Lo que más aporta del storyboard,
entonces, son las tomas `scene:`: describí bien lo que se ve entre mención y mención.

## 2. Ritmo de las tomas

- Una toma cada 2 a 4 segundos (la voz dice unas 3 palabras por segundo).
- Dos tomas a menos de 4 palabras de distancia no se llegan a ver: si dos cosas
  se nombran juntas, van en una sola toma (`logo: Netflix + CD Projekt Red`).
- Entre una mención y la siguiente, una toma `scene:` que ilustre la frase.
- Una placa, un logo o un póster quedan como mucho ~3,5 s: si la frase sigue,
  el sistema completa con planos del tráiler.

## 3. El dato tiene que poder mostrarse

Al escribir el guion, preferí datos concretos que se puedan ilustrar (nombre del
premio y año, cantidad de episodios, fecha exacta, nombre del estudio) antes que
frases generales ("fue un éxito enorme"). Cada dato concreto es una toma con
referencia. En las placas (`card:`) va SOLO lo que dice la narración, en pocas
palabras, con números en cifras ("10 episodios", "septiembre de 2022").

## 4. El cierre es una idea o una pregunta, nunca "suscribite"

La última frase del video es una idea fuerte sobre el tema o una pregunta
concreta para comentarios ("¿Crees que Weak sabía lo de Talia?"). Nada de "dale
me gusta", "suscribite", "activá la campanita" ni el nombre del canal: los
últimos 15 s del video quedan con imagen limpia para la pantalla final de
YouTube, que hace ese trabajo (se carga a mano en Studio: la API no la expone).
