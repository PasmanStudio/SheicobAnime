# Guía del canal — lo que aprendimos revisando videos

Este archivo se manda a Gemini cuando escribe el guion y el storyboard de cada
video (`--guide`). Cada revisión del usuario que deja una regla general se
agrega acá, para que valga en todos los videos siguientes, no solo en el que se
corrigió. Reglas cortas, con un ejemplo real.

## 1. Lo que se nombra, se ve

Cuando la narración nombra algo concreto y reconocible, la toma de ese momento
muestra ESO, no una escena cualquiera del anime. Es lo que hace que el video se
sienta explicado y no narrado encima de un clip.

| Si la narración dice… | La toma muestra… | Ejemplo (Cyberpunk: Edgerunners, revisión del 26-nov-2026) |
|---|---|---|
| la serie, su estreno, "la primera temporada" | el póster oficial / portada de la serie | "esta miniserie de diez episodios, estrenada en septiembre de 2022" → póster de Netflix |
| un estudio, una empresa, una plataforma | sus logos | "una colaboración entre Studio Trigger y CD Projekt Red" → logos de ambos, juntos |
| un premio, un récord, un número | una placa con el dato (premio + año + obra) | "ganó Anime del Año en los Crunchyroll Anime Awards 2023" → placa del premio con la portada |
| un personaje con nombre | su retrato oficial | "David Martínez" → retrato de David |
| una fecha o anuncio ("se confirmó…") | el anuncio, el póster nuevo o una placa con la fecha | "Cyberpunk: Edgerunners 2 estaba en desarrollo" → póster o placa del anuncio |

En el storyboard, para esas tomas usa en `visualQueries` una referencia explícita
con prefijo, en vez de una descripción de escena:

- `poster: <serie>` — póster o portada oficial
- `logo: <empresa>` (una por empresa; si son dos, dos entradas seguidas)
- `card: <texto corto del dato>` — placa generada con el dato (premio, cifra, fecha)
- el nombre del personaje solo, como hasta ahora

## 2. El dato tiene que poder mostrarse

Al escribir el guion, preferí datos concretos que se puedan ilustrar (nombre del
premio y año, cantidad de episodios, fecha exacta, nombre del estudio) antes que
frases generales ("fue un éxito enorme"). Cada dato concreto es una toma con
referencia.
