import { describe, expect, it } from 'vitest';
import {
  resolveTrustedExternalResource,
  trustedExternalResources,
} from './desktopSecurity';

describe('desktop security boundaries', () => {
  it('resolves every fixed API portal to HTTPS', () => {
    for (const id of Object.keys(trustedExternalResources)) {
      expect(resolveTrustedExternalResource(id)).toMatch(/^https:\/\//u);
    }
  });

  it('rejects arbitrary external URLs and inherited property names', () => {
    expect(() => resolveTrustedExternalResource('https://example.com')).toThrow(/allowlisted/u);
    expect(() => resolveTrustedExternalResource('toString')).toThrow(/allowlisted/u);
  });

  it('uses the official Firecrawl API key portal', () => {
    expect(resolveTrustedExternalResource('firecrawlApiKeys')).toBe('https://firecrawl.dev/app/api-keys');
  });

});
