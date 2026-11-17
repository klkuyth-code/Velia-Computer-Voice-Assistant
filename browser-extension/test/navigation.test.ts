import { describe, expect, it } from 'vitest';
import { matchesNavigationTarget } from '../src/navigation';

describe('fast browser navigation verification', () => {
  it('accepts the requested search path while the page is still loading', () => {
    expect(matchesNavigationTarget(
      'https://www.douyin.com/search/%E7%BD%97%E6%B0%B8%E6%B5%A9?aid=6383',
      'https://www.douyin.com/search/%E7%BD%97%E6%B0%B8%E6%B5%A9',
    )).toBe(true);
  });

  it('rejects a login redirect on the same site', () => {
    expect(matchesNavigationTarget(
      'https://www.douyin.com/passport/login',
      'https://www.douyin.com/search/%E7%BD%97%E6%B0%B8%E6%B5%A9',
    )).toBe(false);
  });

  it('rejects another host and malformed URLs', () => {
    expect(matchesNavigationTarget('https://example.com/search/Velia', 'https://www.douyin.com/search/Velia')).toBe(false);
    expect(matchesNavigationTarget(undefined, 'https://www.douyin.com/search/Velia')).toBe(false);
  });
});
