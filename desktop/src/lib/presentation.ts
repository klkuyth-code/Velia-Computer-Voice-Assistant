export type ProviderState = 'online' | 'configured' | 'notConfigured' | 'notReady' | 'testing' | 'error';

export interface ProviderPresentation {
  readonly label: string;
  readonly tone: 'positive' | 'neutral' | 'warning' | 'negative';
}

export function presentProviderState(state: ProviderState): ProviderPresentation {
  switch (state) {
    case 'online':
      return { label: '已就绪', tone: 'positive' };
    case 'configured':
      return { label: '已配置', tone: 'warning' };
    case 'testing':
      return { label: '检测中', tone: 'warning' };
    case 'error':
      return { label: '需要检查', tone: 'negative' };
    case 'notConfigured':
      return { label: '待配置', tone: 'neutral' };
    case 'notReady':
      return { label: '施工中', tone: 'neutral' };
  }
}

export function shortDeviceSuffix(value: string): string {
  if (!/^[a-f0-9]{8}$/u.test(value)) {
    throw new Error('Device suffix is invalid.');
  }
  return value.toUpperCase();
}
