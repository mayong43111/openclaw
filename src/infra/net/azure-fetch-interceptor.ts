/**
 * Global fetch interceptor for Azure OpenAI endpoints.
 *
 * Azure OpenAI requires `?api-version=<version>` on every request but the
 * OpenAI JS SDK (used by pi-ai's `openai-completions` provider) never sets
 * `defaultQuery` for non-`AzureOpenAI` clients.  This interceptor transparently
 * appends the query param so custom providers onboarded against Azure work at
 * runtime — not just during the onboard verification probe.
 */

const AZURE_API_VERSION = "2024-10-21";

let installed = false;

function isAzureOpenAiHost(hostname: string): boolean {
  const h = hostname.toLowerCase();
  return h.endsWith(".openai.azure.com") || h.endsWith(".services.ai.azure.com");
}

/**
 * Idempotently patches `globalThis.fetch` so that requests to Azure OpenAI
 * endpoints automatically include `?api-version=2024-10-21` when the caller
 * did not already provide one.
 */
export function ensureAzureOpenAiFetchInterceptor(): void {
  if (installed) return;
  installed = true;

  const originalFetch = globalThis.fetch;

  globalThis.fetch = function azurePatchedFetch(
    input: RequestInfo | URL,
    init?: RequestInit,
  ): Promise<Response> {
    try {
      const rawUrl =
        typeof input === "string" ? input
        : input instanceof URL ? input.href
        : (input as Request).url;

      if (rawUrl) {
        const url = new URL(rawUrl);
        if (isAzureOpenAiHost(url.hostname) && !url.searchParams.has("api-version")) {
          url.searchParams.set("api-version", AZURE_API_VERSION);

          // Preserve the original Request properties when input is a Request object
          if (typeof input !== "string" && !(input instanceof URL)) {
            return originalFetch.call(globalThis, new Request(url, input), init);
          }
          return originalFetch.call(globalThis, url, init);
        }
      }
    } catch {
      // If URL parsing fails, fall through to the original fetch
    }

    return originalFetch.call(globalThis, input, init);
  };
}
