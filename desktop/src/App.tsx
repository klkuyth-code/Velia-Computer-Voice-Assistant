import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { CSSProperties, ReactNode, RefObject } from 'react';
import {
  AudioLines,
  BrainCircuit,
  ChevronRight,
  ExternalLink,
  Globe2,
  History,
  Save,
  ShieldCheck,
  Sparkles,
  X,
} from 'lucide-react';
import type {
  ConfigStatusResult,
  ConversationHistoryDetail,
  HealthResult,
  SecretConfigurationState,
  VoiceShortcutSetting,
} from '@velia/protocol';
import { BrowserPanel } from './components/BrowserPanel';
import { HistoryPanel } from './components/HistoryPanel';
import { LivePanel } from './components/LivePanel';
import { ModelsPanel } from './components/ModelsPanel';
import { VisionPanel } from './components/VisionPanel';
import { VoicePanel } from './components/VoicePanel';
import veliaLifeOrbImage from './assets/velia-life-orb.png';

type Section = 'overview' | 'models' | 'voice' | 'browser';
type ProviderKey = 'llm' | 'vision' | 'asr' | 'web';

const defaultVoiceShortcut: VoiceShortcutSetting = {
  id: 'f8', key: 'F8', label: 'F8', help: '部分笔记本需要同时按住 Fn。',
};

const pageMeta: Record<Section, readonly [string, string]> = {
  overview: ['意识中心', 'Velia 已准备好与你一起工作。'],
  models: ['模型与 API', ''],
  voice: ['语音与唤醒', ''],
  browser: ['浏览器与网页', '管理 Chrome 扩展连接与网页检索服务。'],
};

const navigation = [
  { section: 'overview' as const, label: '意识中心', icon: Sparkles },
  { section: 'models' as const, label: '模型与 API', icon: BrainCircuit },
  { section: 'voice' as const, label: '语音与唤醒', icon: AudioLines },
  { section: 'browser' as const, label: '浏览器与网页', icon: Globe2 },
];

export function App() {
  const shellRef = useRef<HTMLDivElement>(null);
  const pageRef = useRef<HTMLDivElement>(null);
  const [section, setSection] = useState<Section>('overview');
  const [health, setHealth] = useState<HealthResult>();
  const [config, setConfig] = useState<ConfigStatusResult>();
  const [voiceShortcut, setVoiceShortcut] = useState<VoiceShortcutSetting>(defaultVoiceShortcut);
  const [startupError, setStartupError] = useState('');
  const [historyConversation, setHistoryConversation] = useState<ConversationHistoryDetail>();
  const [historyOpen, setHistoryOpen] = useState(false);
  const [liveOpen, setLiveOpen] = useState(false);
  const [geminiOpen, setGeminiOpen] = useState(false);
  const [asrOpen, setAsrOpen] = useState(false);
  const [toast, setToast] = useState('');
  const toastTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useEffect(() => {
    let active = true;
    Promise.all([window.velia.system.health(), window.velia.config.status()])
      .then(([nextHealth, nextConfig]) => {
        if (!active) return;
        setHealth(nextHealth);
        setConfig(nextConfig);
      })
      .catch((error: unknown) => {
        if (active) setStartupError(error instanceof Error ? error.message : 'Velia Core 连接失败。');
      });
    void window.velia.voice.getShortcut().then((shortcut) => {
      if (active) setVoiceShortcut(shortcut);
    }).catch(() => undefined);
    return () => { active = false; };
  }, []);

  useControlMotion(shellRef);

  useLayoutEffect(() => {
    if (!pageRef.current) return;
    pageRef.current.scrollTop = 0;
    pageRef.current.scrollLeft = 0;
    const frame = requestAnimationFrame(() => {
      if (!pageRef.current) return;
      pageRef.current.scrollTop = 0;
      pageRef.current.scrollLeft = 0;
    });
    return () => cancelAnimationFrame(frame);
  }, [section]);

  useEffect(() => () => {
    if (toastTimer.current) clearTimeout(toastTimer.current);
  }, []);

  const showToast = (message: string) => {
    setToast(message);
    if (toastTimer.current) clearTimeout(toastTimer.current);
    toastTimer.current = setTimeout(() => setToast(''), 2_000);
  };

  const updateConfig = (key: ProviderKey, value: SecretConfigurationState) => {
    setConfig((current) => ({
      llm: key === 'llm' ? value : current?.llm ?? 'notConfigured',
      asr: key === 'asr' ? value : current?.asr ?? 'notConfigured',
      vision: key === 'vision' ? value : current?.vision ?? 'notConfigured',
      web: key === 'web' ? value : current?.web ?? 'notConfigured',
    }));
  };

  const configureProvider = (provider: ProviderKey) => {
    if (provider === 'llm') setSection('models');
    if (provider === 'vision') setGeminiOpen(true);
    if (provider === 'asr') setAsrOpen(true);
    if (provider === 'web') setSection('browser');
  };

  const [title, subtitle] = pageMeta[section];

  if (!health && !startupError) {
    return <StartupScreen message="正在连接 Velia…" />;
  }

  return (
    <div className="velia-control" ref={shellRef}>
      <div className="velia-control__ambient" aria-hidden="true"><i /><i /><i /><i /></div>
      <div className="velia-control__stars" aria-hidden="true">
        {Array.from({ length: 120 }, (_, index) => <i key={index} style={shellStarStyle(index)} />)}
      </div>
      <aside className="velia-sidebar">
        <div className="velia-brand"><LifeOrb compact /><strong>Velia</strong></div>

        <nav className="velia-navigation" aria-label="Velia 功能导航">
          {navigation.map(({ section: itemSection, label, icon: Icon }) => (
            <button
              className="velia-nav-button"
              type="button"
              aria-current={section === itemSection ? 'page' : undefined}
              key={itemSection}
              onClick={() => setSection(itemSection)}
            >
              <span className="velia-nav-icon"><Icon size={16} /></span>
              <span>{label}</span>
              {section === itemSection && <ChevronRight className="velia-nav-arrow" size={14} />}
            </button>
          ))}
        </nav>

        <ProviderSummary config={config} />
      </aside>

      <main className="velia-main">
        <header className="velia-main-header">
          <div><h1>{title}</h1>{subtitle && <p>{subtitle}</p>}</div>
          <div className="velia-core-state"><i className={health ? 'is-online' : ''} /><span>{health ? 'Velia Core 正常' : '正在连接 Core'}</span></div>
        </header>

        {startupError && <div className="velia-error-banner" role="alert">{startupError}</div>}

        <div className={`velia-page velia-page--${section}`} ref={pageRef}>
          {section === 'overview' && <OverviewPage />}

          {section === 'models' && (
            <div className="velia-models-page">
              <ApiProviderGrid config={config} onConfigure={configureProvider} />
              <ModelsPanel
                {...(historyConversation ? { initialConversation: historyConversation } : {})}
                onLlmConfigChange={(value) => updateConfig('llm', value)}
              />
            </div>
          )}

          <VoicePanel
            active={section === 'voice'}
            config={config}
            shortcut={voiceShortcut}
            asrOpen={asrOpen}
            onAsrClose={() => setAsrOpen(false)}
            onShortcutChange={setVoiceShortcut}
            onAsrConfigChange={(value) => updateConfig('asr', value)}
          />

          {section === 'browser' && (
            <BrowserPanel
              configured={config?.web === 'configured'}
              onConfigChange={(value) => updateConfig('web', value)}
            />
          )}
        </div>

        <div className="velia-bottom-dock" aria-label="快速入口">
          <button className="velia-dock-button velia-magnetic-button" type="button" onClick={() => setHistoryOpen(true)}><History size={15} /><span>对话历史</span></button>
          <button className="velia-dock-button velia-magnetic-button" type="button" onClick={() => showToast('设置会在修改时自动加密保存。')}><Save size={15} /><span>保存状态</span></button>
          <button
            className={`velia-live-capsule${liveOpen ? ' is-open' : ''}`}
            type="button"
            aria-expanded={liveOpen}
            onClick={() => setLiveOpen(true)}
          >
            <img className="velia-live-orb-image" src={veliaLifeOrbImage} alt="" aria-hidden="true" />
            <span>打开 Live</span>
          </button>
        </div>
      </main>

      <ModalLayer open={historyOpen} label="对话历史" onClose={() => setHistoryOpen(false)} className="velia-history-modal">
        <HistoryPanel
          privacyView={false}
          onContinue={(conversation) => {
            setHistoryConversation(conversation);
            setHistoryOpen(false);
            setSection('models');
          }}
          onStartNew={() => {
            setHistoryConversation(undefined);
            setHistoryOpen(false);
            setSection('models');
          }}
        />
      </ModalLayer>

      <ModalLayer open={geminiOpen} label="Gemini API 配置" onClose={() => setGeminiOpen(false)} className="velia-gemini-modal">
        <VisionPanel
          configured={config?.vision === 'configured'}
          onConfigChange={(value) => updateConfig('vision', value)}
        />
      </ModalLayer>

      <ModalLayer open={liveOpen} label="Live 模式" onClose={() => setLiveOpen(false)} className="velia-live-modal">
        <LivePanel
          active={liveOpen}
          configured={config?.vision === 'configured'}
          onOpenVision={() => {
            setLiveOpen(false);
            setGeminiOpen(true);
          }}
        />
      </ModalLayer>

      <div className={`velia-toast${toast ? ' is-visible' : ''}`} role="status" aria-live="polite">{toast}</div>
    </div>
  );
}

function StartupScreen({ message }: { readonly message: string }) {
  return <main className="velia-startup"><LifeOrb compact /><span>{message}</span></main>;
}

function OverviewPage() {
  return (
    <section className="velia-consciousness">
      <div className="velia-nebula-scene" aria-hidden="true">
        <div className="velia-nebula-base" />
        <div className="velia-nebula-cloud velia-nebula-cloud--left" />
        <div className="velia-nebula-cloud velia-nebula-cloud--right" />
        <div className="velia-nebula-center-glow" />
      </div>
      <div className="velia-particle-field" aria-hidden="true">
        {Array.from({ length: 172 }, (_, index) => (
          <i
            className={`velia-particle velia-particle--layer-${index % 3} velia-particle--tone-${index % 5}`}
            key={index}
            style={nebulaParticleStyle(index)}
          />
        ))}
      </div>
      <div className="velia-consciousness__visual">
        <LifeOrb />
        <div className="velia-consciousness__copy">
          <h2>Velia，一个与你共同成长的 AI 生命</h2>
          <p>聆听你的声音，理解你的世界，创造无限可能</p>
          <span className="velia-consciousness__status"><i />Velia 随时待命</span>
        </div>
      </div>
    </section>
  );
}

function ApiProviderGrid({ config, onConfigure }: {
  readonly config: ConfigStatusResult | undefined;
  readonly onConfigure: (provider: ProviderKey) => void;
}) {
  const providers: ReadonlyArray<{
    readonly key: ProviderKey;
    readonly name: string;
    readonly detail: string;
    readonly portal: 'deepseekApiKeys' | 'geminiApiKeys' | 'volcengineSpeechApiKeys' | 'firecrawlApiKeys';
  }> = [
    { key: 'llm', name: 'DeepSeek', detail: '日常对话与任务规划', portal: 'deepseekApiKeys' },
    { key: 'vision', name: 'Google Gemini', detail: '视觉理解与 Live', portal: 'geminiApiKeys' },
    { key: 'asr', name: '火山 ASR', detail: '普通语音识别', portal: 'volcengineSpeechApiKeys' },
    { key: 'web', name: 'Firecrawl', detail: '公开网页检索与正文提取', portal: 'firecrawlApiKeys' },
  ];

  return (
    <section className="velia-api-overview">
      <header><div><h2>能力连接</h2></div><ShieldCheck size={20} /></header>
      <div className="velia-api-grid">
        {providers.map((provider) => {
          const ready = config?.[provider.key] === 'configured';
          return (
            <article className="velia-api-card" key={provider.key}>
              <div className="velia-api-card__head"><strong>{provider.name}</strong><span className={ready ? 'is-ready' : ''}><i />{ready ? '已连接' : '待配置'}</span></div>
              <p>{provider.detail}</p>
              <div>
                <button className="velia-api-button velia-magnetic-button" type="button" onClick={() => onConfigure(provider.key)}>配置</button>
                <button className="velia-api-link" type="button" aria-label={`打开 ${provider.name} API 获取页面`} onClick={() => void window.velia.system.openExternalResource(provider.portal)}><ExternalLink size={13} /></button>
              </div>
            </article>
          );
        })}
      </div>
    </section>
  );
}

function ProviderSummary({ config }: { readonly config: ConfigStatusResult | undefined }) {
  const items = [
    ['DeepSeek', config?.llm],
    ['Gemini', config?.vision],
    ['火山 ASR', config?.asr],
    ['Firecrawl', config?.web],
  ] as const;
  return (
    <section className="velia-provider-summary" aria-label="服务连接状态">
      <span>服务状态</span>
      {items.map(([name, state]) => <div key={name}><strong>{name}</strong><em className={state === 'configured' ? 'is-ready' : ''}><i />{state === 'configured' ? '在线' : '未配置'}</em></div>)}
    </section>
  );
}

function ModalLayer({ open, label, onClose, className, children }: {
  readonly open: boolean;
  readonly label: string;
  readonly onClose: () => void;
  readonly className: string;
  readonly children: ReactNode;
}) {
  return (
    <div className={`velia-modal-layer ${className}${open ? ' is-open' : ''}`} aria-hidden={!open}>
      <section className="velia-modal-surface" aria-label={label}>
        <header className="velia-modal-header"><div><span>VELIA</span><h2>{label}</h2></div><button type="button" aria-label={`关闭${label}`} onClick={onClose}><X size={17} /></button></header>
        <div className="velia-modal-content">{children}</div>
      </section>
    </div>
  );
}

function LifeOrb({ compact = false }: { readonly compact?: boolean }) {
  return (
    <span className={`velia-life-orb${compact ? ' velia-life-orb--compact' : ''}`} aria-hidden="true">
      <i className="velia-life-orb__halo" />
      <i className="velia-life-orb__ring velia-life-orb__ring--one" />
      <i className="velia-life-orb__ring velia-life-orb__ring--two" />
      <i className="velia-life-orb__surface"><img src={veliaLifeOrbImage} alt="" draggable={false} /></i>
    </span>
  );
}

function nebulaParticleStyle(index: number): CSSProperties {
  let x: number;
  let y: number;
  let size: number;
  let driftScale: number;
  if (index < 92) {
    const seed = index + 701;
    const right = index % 2 === 1;
    const sequence = Math.floor(index / 2);
    const t = ((sequence * 17) % 47) / 46;
    const jitterX = ((seed % 9) - 4) * .82;
    const jitterY = ((seed % 13) - 6) * .64;
    x = right ? 102 - t * 56 + jitterX : -2 + t * 56 + jitterX;
    y = 16 + t * 74 + Math.sin(t * Math.PI * 1.7) * 8 + jitterY;
    size = .9 + (seed % 5) * .46;
    driftScale = .72;
  } else if (index < 130) {
    const orbitIndex = index - 92;
    const seed = orbitIndex + 901;
    const angle = (orbitIndex / 38) * Math.PI * 2 + (seed % 5) * .035;
    x = 50 + Math.cos(angle) * (20 + (seed % 4) * 2.1);
    y = 39 + Math.sin(angle) * (15 + (seed % 5) * 1.35);
    size = 1.2 + (seed % 5) * .55;
    driftScale = .48;
  } else {
    const sparseIndex = index - 130;
    const seed = sparseIndex + 1101;
    x = (seed * 43.71 + (seed % 7) * 8.4) % 100;
    y = (seed * 29.37 + (seed % 11) * 5.8) % 100;
    size = .8 + (seed % 6) * .48;
    driftScale = .58;
  }
  const seed = index + 701;
  const direction = (seed * 2.39996) % (Math.PI * 2);
  const distance = (6 + (seed % 10) * 1.45) * driftScale;
  return {
    '--particle-x': `${x}%`,
    '--particle-y': `${y}%`,
    '--particle-size': `${size.toFixed(2)}px`,
    '--particle-delay': `${-(seed % 19) * .41}s`,
    '--particle-duration': `${4.6 + (seed % 11) * .66}s`,
    '--particle-color-duration': `${5.6 + (seed % 9) * .63}s`,
    '--particle-drift-x': `${(Math.cos(direction) * distance).toFixed(2)}px`,
    '--particle-drift-y': `${(Math.sin(direction) * distance).toFixed(2)}px`,
  } as CSSProperties;
}

function shellStarStyle(index: number): CSSProperties {
  const seed = index + 3;
  return {
    '--shell-star-x': `${((seed * 47.17 + (seed % 7) * 9.2) % 100).toFixed(2)}%`,
    '--shell-star-y': `${((seed * 31.73 + (seed % 11) * 6.7) % 100).toFixed(2)}%`,
    '--shell-star-size': `${(1 + (seed % 4) * .7).toFixed(2)}px`,
    '--shell-star-opacity': `${(.18 + (seed % 6) * .08).toFixed(2)}`,
    '--shell-star-duration': `${(5.2 + (seed % 9) * .72).toFixed(2)}s`,
    '--shell-star-delay': `${(-(seed % 13) * .47).toFixed(2)}s`,
  } as CSSProperties;
}

function useControlMotion(rootRef: RefObject<HTMLDivElement | null>) {
  useEffect(() => {
    const root = rootRef.current;
    if (!root) return;
    const selector = 'button:not(:disabled)';

    const findButton = (event: PointerEvent): HTMLButtonElement | undefined => {
      if (!(event.target instanceof Element)) return undefined;
      const button = event.target.closest<HTMLButtonElement>(selector);
      return button && root.contains(button) ? button : undefined;
    };

    const onPointerMove = (event: PointerEvent) => {
      const button = findButton(event);
      if (!button) return;
      const bounds = button.getBoundingClientRect();
      const px = Math.max(0, Math.min(1, (event.clientX - bounds.left) / Math.max(1, bounds.width)));
      const py = Math.max(0, Math.min(1, (event.clientY - bounds.top) / Math.max(1, bounds.height)));
      button.style.setProperty('--button-x', `${px * 100}%`);
      button.style.setProperty('--button-y', `${py * 100}%`);
      button.style.setProperty('--magnet-x', `${(px - .5) * 8}px`);
      button.style.setProperty('--magnet-y', `${(py - .5) * 6}px`);
    };

    const onPointerOut = (event: PointerEvent) => {
      const button = findButton(event);
      if (!button || (event.relatedTarget instanceof Node && button.contains(event.relatedTarget))) return;
      button.style.setProperty('--button-x', '50%');
      button.style.setProperty('--button-y', '50%');
      button.style.setProperty('--magnet-x', '0px');
      button.style.setProperty('--magnet-y', '0px');
    };

    const onPointerDown = (event: PointerEvent) => {
      const button = findButton(event);
      if (!button || (!button.classList.contains('velia-magnetic-button') && !button.classList.contains('button'))) return;
      button.classList.add('is-pressed');
      const bounds = button.getBoundingClientRect();
      const ripple = document.createElement('i');
      ripple.className = 'velia-button-ripple';
      ripple.style.left = `${event.clientX - bounds.left}px`;
      ripple.style.top = `${event.clientY - bounds.top}px`;
      button.appendChild(ripple);
      window.setTimeout(() => ripple.remove(), 680);
    };

    const onPointerUp = () => root.querySelectorAll('.is-pressed').forEach((element) => element.classList.remove('is-pressed'));
    root.addEventListener('pointermove', onPointerMove);
    root.addEventListener('pointerout', onPointerOut);
    root.addEventListener('pointerdown', onPointerDown);
    root.addEventListener('pointerup', onPointerUp);
    root.addEventListener('pointercancel', onPointerUp);
    return () => {
      root.removeEventListener('pointermove', onPointerMove);
      root.removeEventListener('pointerout', onPointerOut);
      root.removeEventListener('pointerdown', onPointerDown);
      root.removeEventListener('pointerup', onPointerUp);
      root.removeEventListener('pointercancel', onPointerUp);
    };
  }, [rootRef]);
}
