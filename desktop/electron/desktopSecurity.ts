export const trustedExternalResources = Object.freeze({
  deepseekApiKeys: 'https://platform.deepseek.com/api_keys',
  volcengineSpeechApiKeys: 'https://console.volcengine.com/speech/new/setting/apikeys?projectName=default',
  geminiApiKeys: 'https://aistudio.google.com/app/apikey',
  firecrawlApiKeys: 'https://firecrawl.dev/app/api-keys',
} as const);

export type TrustedExternalResourceId = keyof typeof trustedExternalResources;

export function resolveTrustedExternalResource(resourceId: unknown): string {
  if (typeof resourceId !== 'string' || !Object.hasOwn(trustedExternalResources, resourceId)) {
    throw new TypeError('External resource is not allowlisted.');
  }
  return trustedExternalResources[resourceId as TrustedExternalResourceId];
}
