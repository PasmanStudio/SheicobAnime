// No-op stand-in for @sentry/nextjs on Cloudflare Workers builds.
// The real SDK adds ~4 MiB to the server bundle, blowing the 3 MiB
// Workers free-plan limit. Aliased in next.config.mjs when CLOUDFLARE_BUILD=1.
export function init(_options?: unknown): void {}
export function captureException(_error: unknown): string {
  return "";
}
export function captureMessage(_message: string): string {
  return "";
}
export function captureRequestError(..._args: unknown[]): void {}
export function withScope(callback: (scope: unknown) => void): void {
  callback({});
}
export function setUser(_user: unknown): void {}
export function setTag(_key: string, _value: string): void {}
export function addBreadcrumb(_breadcrumb: unknown): void {}
export function flush(_timeout?: number): Promise<boolean> {
  return Promise.resolve(true);
}
