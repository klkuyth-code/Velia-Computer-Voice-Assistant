export {};

const tokenInput = requireElement<HTMLInputElement>('token');
const portInput = requireElement<HTMLInputElement>('port');
const status = requireElement<HTMLParagraphElement>('status');
const save = requireElement<HTMLButtonElement>('save');

void load();
save.addEventListener('click', () => void persist());

async function load(): Promise<void> {
  const stored = await chrome.storage.local.get(['pairingToken', 'gatewayPort']);
  tokenInput.value = typeof stored.pairingToken === 'string' ? stored.pairingToken : '';
  portInput.value = String(validPort(stored.gatewayPort) ?? 47_839);
  await refreshStatus();
}

async function persist(): Promise<void> {
  const token = tokenInput.value.trim();
  const port = validPort(Number(portInput.value));
  if (token.length < 32 || token.length > 256 || !port) {
    showStatus('令牌或端口格式不正确。', false);
    return;
  }
  await chrome.storage.local.set({ pairingToken: token, gatewayPort: port });
  await chrome.runtime.sendMessage({ type: 'velia.reconnect' });
  showStatus('配置已保存，正在连接…', false);
  await new Promise((resolve) => setTimeout(resolve, 350));
  await refreshStatus();
}

async function refreshStatus(): Promise<void> {
  const result = await chrome.runtime.sendMessage({ type: 'velia.status' }) as { connected?: boolean } | undefined;
  showStatus(result?.connected ? '已连接 Velia 桌面端' : '尚未连接 Velia 桌面端', Boolean(result?.connected));
}

function showStatus(message: string, ready: boolean): void {
  status.textContent = message;
  status.dataset.ready = String(ready);
}

function validPort(value: unknown): number | undefined {
  const port = Number(value);
  return Number.isInteger(port) && port >= 1_024 && port <= 65_535 ? port : undefined;
}

function requireElement<T extends HTMLElement>(id: string): T {
  const element = document.getElementById(id);
  if (!element) throw new Error(`Missing popup element: ${id}`);
  return element as T;
}
