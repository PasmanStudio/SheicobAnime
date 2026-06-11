import { defineCloudflareConfig } from "@opennextjs/cloudflare";

// Default config: no incremental cache binding (ISR/revalidate not used).
// Add an R2 incremental cache here if the app adopts ISR later.
export default defineCloudflareConfig({});
