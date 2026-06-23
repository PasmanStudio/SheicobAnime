import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "DMCA / Takedown",
  description: "DMCA takedown request process for SheicobAnime.",
};

export default function DmcaPage() {
  return (
    <div className="container mx-auto max-w-3xl px-4 py-12">
      <h1 className="text-3xl font-bold mb-8">DMCA / Solicitud de Eliminación</h1>

      <div className="space-y-8 text-ink-2 leading-relaxed">
        <section>
          <h2 className="text-xl font-semibold text-white mb-3">Sobre SheicobAnime</h2>
          <p>
            SheicobAnime es un <strong>índice de contenido</strong>. No alojamos, almacenamos ni
            distribuimos archivos de video. Todos los videos se reproducen desde servidores de terceros
            mediante enlaces embebidos (iframes) públicamente disponibles.
          </p>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">Proceso de eliminación</h2>
          <p>
            Si sos titular de derechos de autor y creés que tu contenido está siendo indexado
            sin autorización, puedes solicitar la eliminación contactándonos:
          </p>
          <ul className="list-disc list-inside mt-4 space-y-2 text-ink-2">
            <li>Identificación de la obra protegida</li>
            <li>URL(s) específica(s) en nuestro sitio que deseas que se eliminen</li>
            <li>Tu información de contacto (nombre, email)</li>
            <li>Declaración de buena fe de que el uso no está autorizado</li>
          </ul>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">Contacto</h2>
          <p>
            Enviá tu solicitud a:{" "}
            <a href="mailto:dmca@sheicobanime.com" className="text-blue-400 hover:underline">
              dmca@sheicobanime.com
            </a>
          </p>
          <p className="mt-2 text-ink-3 text-sm">
            Las solicitudes se procesan dentro de las 48 horas hábiles. El contenido será
            bloqueado de nuestro índice mientras se resuelve la solicitud.
          </p>
        </section>
      </div>
    </div>
  );
}
