/* SheicobAnime — service worker de notificaciones push.
   Solo push: sin cacheo offline (el contenido es dinámico). */

self.addEventListener("install", () => {
  self.skipWaiting();
});

self.addEventListener("activate", (event) => {
  event.waitUntil(self.clients.claim());
});

self.addEventListener("push", (event) => {
  if (!event.data) return;

  let payload;
  try {
    payload = event.data.json();
  } catch {
    payload = { title: "SheicobAnime", body: event.data.text() };
  }

  const { title = "SheicobAnime", body = "", url = "/", icon } = payload;

  event.waitUntil(
    self.registration.showNotification(title, {
      body,
      icon: icon || "/favicon.png",
      badge: "/favicon.png",
      data: { url },
      // Agrupa por URL: varias respuestas al mismo hilo = una notificación
      tag: url,
      renotify: false,
    }),
  );
});

self.addEventListener("notificationclick", (event) => {
  event.notification.close();
  const url = event.notification.data?.url || "/";

  event.waitUntil(
    self.clients.matchAll({ type: "window", includeUncontrolled: true }).then((clients) => {
      // Si ya hay una pestaña del sitio, navegarla; si no, abrir una
      for (const client of clients) {
        if ("focus" in client) {
          client.focus();
          if ("navigate" in client) client.navigate(url);
          return;
        }
      }
      return self.clients.openWindow(url);
    }),
  );
});
