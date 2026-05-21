// Placeholder redirect until #129 (personal lists/playlists) is implemented.
// For now, /listas → /guardado so the nav link doesn't 404.
import { redirect } from "next/navigation";

export default function ListasPage() {
  redirect("/guardado");
}
