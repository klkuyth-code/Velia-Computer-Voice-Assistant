import { describe, expect, it } from 'vitest';
import { presentProviderState, shortDeviceSuffix } from './presentation';

describe('presentation helpers', () => {
  it('uses honest labels for provider states', () => {
    expect(presentProviderState('online')).toEqual({ label: '已就绪', tone: 'positive' });
    expect(presentProviderState('configured')).toEqual({ label: '已配置', tone: 'warning' });
    expect(presentProviderState('notConfigured')).toEqual({ label: '待配置', tone: 'neutral' });
    expect(presentProviderState('notReady')).toEqual({ label: '施工中', tone: 'neutral' });
    expect(presentProviderState('error')).toEqual({ label: '需要检查', tone: 'negative' });
  });

  it('formats only validated device suffixes', () => {
    expect(shortDeviceSuffix('a1b2c3d4')).toBe('A1B2C3D4');
    expect(() => shortDeviceSuffix('../secret')).toThrow('Device suffix is invalid.');
  });
});
