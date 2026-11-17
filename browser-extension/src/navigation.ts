export function matchesNavigationTarget(actualValue: string | undefined, targetValue: string): boolean {
  try {
    const actual = new URL(String(actualValue ?? ''));
    const target = new URL(targetValue);
    const actualHost = actual.hostname.toLowerCase().replace(/^www\./u, '');
    const targetHost = target.hostname.toLowerCase().replace(/^www\./u, '');
    if (actualHost !== targetHost) return false;
    return target.pathname === '/' || actual.pathname.startsWith(target.pathname);
  } catch { return false; }
}
