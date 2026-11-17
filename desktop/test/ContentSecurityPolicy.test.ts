import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('renderer content security policy', () => {
  it('allows only local and temporary blob audio playback', () => {
    const html = readFileSync(resolve(process.cwd(), 'index.html'), 'utf8');
    const policy = html.match(/http-equiv="Content-Security-Policy"[\s\S]*?content="([^"]+)"/)?.[1];

    expect(policy).toBeDefined();
    expect(policy).toContain("media-src 'self' blob:");
    expect(policy).not.toMatch(/media-src[^;]*\*/);
    expect(policy).not.toMatch(/media-src[^;]*https?:/);
  });
});
