import type { Metadata } from "next";

export const metadata: Metadata = {
  title: "Política de Privacidad",
  description: "Política de privacidad de SheicobAnime.",
};

export default function PrivacyPolicyPage() {
  return (
    <div className="container mx-auto max-w-3xl px-4 py-12">
      <h1 className="text-3xl font-bold mb-8">Política de Privacidad</h1>
      <p className="text-ink-2 text-sm mb-8">
        Última actualización: {new Date().toLocaleDateString("es-AR", { year: "numeric", month: "long", day: "numeric" })}
      </p>

      <div className="space-y-8 text-ink-2 leading-relaxed">
        <section>
          <h2 className="text-xl font-semibold text-white mb-3">1. Información que recopilamos</h2>
          <p>
            SheicobAnime no requiere registro de usuario. Recopilamos la siguiente información de forma automática:
          </p>
          <ul className="list-disc list-inside mt-2 space-y-1 text-ink-2">
            <li>Dirección IP (para rate limiting y seguridad)</li>
            <li>Datos de navegación (páginas visitadas, tiempo de permanencia)</li>
            <li>Información del dispositivo (navegador, sistema operativo)</li>
            <li>Preferencias almacenadas localmente (consentimiento de cookies, búsquedas recientes)</li>
          </ul>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">2. Uso de cookies y almacenamiento local</h2>
          <p>Utilizamos:</p>
          <ul className="list-disc list-inside mt-2 space-y-1 text-ink-2">
            <li><strong>localStorage:</strong> Para guardar tu consentimiento de publicidad y búsquedas recientes. Estos datos nunca salen de tu navegador.</li>
            <li>
              <strong>Cookie <code>sheicob_did</code>:</strong> Un identificador anónimo aleatorio (UUID) generado por nuestro servidor
              y almacenado en tu navegador por hasta 2 años. Se usa únicamente para recordar el punto donde dejaste de ver
              un episodio (función &quot;Continuar viendo&quot;). No se vincula a tu identidad, correo, IP
              persistente ni a servicios de terceros. Los datos de progreso se eliminan automáticamente tras 180 días de
              inactividad. Podés borrar esta cookie desde tu navegador en cualquier momento para reiniciar tu historial anónimo.
            </li>
            <li><strong>Cookies de terceros:</strong> Nuestros socios publicitarios (Adsterra) y de comentarios (Disqus) pueden establecer cookies para personalizar anuncios y funcionalidades. Estas cookies se rigen por las políticas de privacidad de cada proveedor.</li>
          </ul>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">3. Publicidad</h2>
          <p>
            SheicobAnime utiliza redes publicitarias de terceros para mostrar anuncios. Estos servicios pueden recopilar
            información sobre tu actividad de navegación para mostrar anuncios relevantes. Podés controlar la
            personalización de anuncios desde la configuración de tu navegador.
          </p>
          <p className="mt-2">
            Proveedores publicitarios actuales:
          </p>
          <ul className="list-disc list-inside mt-2 space-y-1 text-ink-2">
            <li>Adsterra — <a href="https://adsterra.com/privacy-policy/" className="text-blue-400 hover:underline" target="_blank" rel="noopener noreferrer">Política de privacidad</a></li>
          </ul>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">4. Comentarios</h2>
          <p>
            La sección de comentarios es proporcionada por Disqus. Al comentar, aceptás la{" "}
            <a href="https://disqus.com/privacy-policy/" className="text-blue-400 hover:underline" target="_blank" rel="noopener noreferrer">
              política de privacidad de Disqus
            </a>.
          </p>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">5. Monitoreo de errores</h2>
          <p>
            Utilizamos Sentry para monitorear errores técnicos. Sentry puede recopilar información técnica
            (IP anonimizada, navegador, sistema operativo, stack traces) exclusivamente para diagnóstico de errores.
          </p>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">6. Contenido de terceros</h2>
          <p>
            SheicobAnime es un índice de contenido. Los videos se reproducen desde servidores de terceros
            mediante iframes embebidos. No almacenamos, alojamos ni distribuimos archivos de video.
            Cada proveedor de video tiene sus propias políticas de privacidad.
          </p>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">7. Seguridad</h2>
          <p>
            Implementamos medidas de seguridad estándar: HTTPS, rate limiting, headers de seguridad (HSTS,
            X-Content-Type-Options, X-Frame-Options), y protección WAF mediante Cloudflare.
          </p>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">8. Tus derechos</h2>
          <p>Podés:</p>
          <ul className="list-disc list-inside mt-2 space-y-1 text-ink-2">
            <li>Borrar tu almacenamiento local desde la configuración del navegador</li>
            <li>Bloquear cookies de terceros desde tu navegador</li>
            <li>Usar extensiones de bloqueo de anuncios</li>
            <li>Solicitar la eliminación de comentarios contactándonos</li>
          </ul>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">9. Contacto</h2>
          <p>
            Para consultas sobre privacidad, contactanos en:{" "}
            <a href="mailto:privacy@sheicobanime.com" className="text-blue-400 hover:underline">
              privacy@sheicobanime.com
            </a>
          </p>
        </section>

        <section>
          <h2 className="text-xl font-semibold text-white mb-3">10. Cambios a esta política</h2>
          <p>
            Nos reservamos el derecho de actualizar esta política. Los cambios se publicarán en esta página
            con la fecha de actualización correspondiente.
          </p>
        </section>
      </div>
    </div>
  );
}
