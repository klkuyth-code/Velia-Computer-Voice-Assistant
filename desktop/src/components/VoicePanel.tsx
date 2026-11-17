import { useEffect, useRef, useState } from 'react';
import { AudioLines, Ear, ExternalLink, KeyRound, Mic, Play, Radio, Square, Trash2, Volume2, Wifi, X } from 'lucide-react';
import type { AssistantOverlaySnapshot, AsrCredentialMode, ConfigStatusResult, CoreEvent, EdgeVoiceId, InputEvent, SecretConfigurationState, TtsSetting, VoicePerformanceMetrics, VoiceShortcutId, VoiceShortcutSetting, WakeWordRuntimeState, WakeWordStatus } from '@velia/protocol';
import { bytesToBase64, encodePcm16Wav } from '../voice/audio';
import { MicrophoneRecorder, type RecordingResult } from '../voice/MicrophoneRecorder';
import { liveSessionHandoff } from '../voice/LiveSessionHandoff';
import { BargeInDetector } from '../voice/BargeInDetector';
import { isSpeechCancellationError } from '../voice/speech';
import { edgeVoices, loadTtsSetting, ttsStorageKey } from '../voice/edgeVoices';
import { IncrementalSpeechTextBuffer, splitSpeechText } from '../voice/speechText';
import { coreFailureSpeechText } from '../voice/coreFailureSpeech';
import { synthesizeInOrder } from '../voice/OrderedSynthesisPipeline';
import { EncodedSpeechPlaybackQueue } from '../voice/EncodedSpeechPlaybackQueue';
import { WakeWordDetector, type WakeWordRuntimeSnapshot } from '../voice/WakeWordDetector';
import { isLikelyPlaybackEcho } from '../voice/playbackEcho';
import { isWakeConversationExit, wakeConversationExpired } from '../voice/wakeConversation';
import { shouldResumeOrdinaryLongConversation } from '../voice/ordinaryLongConversation';
import { isVoiceConversationStop } from '../voice/voiceConversationControl';

type VoiceState = 'idle' | 'starting' | 'recording' | 'transcribing' | 'thinking' | 'responding' | 'speaking' | 'error';
type SpeechBargeInMode = 'single' | 'long' | 'wake';
interface VoiceMessage { readonly role: 'user' | 'assistant'; readonly text: string; }
interface CachedWakeAcknowledgement {
  readonly schema: 'velia-wake-ack/v1';
  readonly voiceId: EdgeVoiceId;
  readonly rate: number;
  readonly audioBase64: string;
  readonly mimeType: 'audio/mpeg';
}

interface IncrementalSpeechTurn {
  readonly operationId: string;
  readonly session: number;
  readonly buffer: IncrementalSpeechTextBuffer;
  readonly queue: EncodedSpeechPlaybackQueue;
  synthesisTail: Promise<void>;
  readonly playbackResults: Promise<boolean>[];
  playbackStarted: boolean;
  failed: boolean;
}

const wakeAcknowledgementStorageKey = 'velia.wake-ack.v1';

export function VoicePanel({
  active,
  config,
  shortcut,
  asrOpen,
  onAsrClose,
  onAsrConfigChange,
  onShortcutChange,
}: {
  readonly active: boolean;
  readonly config: ConfigStatusResult | undefined;
  readonly shortcut: VoiceShortcutSetting;
  readonly asrOpen: boolean;
  readonly onAsrClose: () => void;
  readonly onAsrConfigChange: (state: SecretConfigurationState) => void;
  readonly onShortcutChange: (shortcut: VoiceShortcutSetting) => void;
}) {
  const [mode, setMode] = useState<AsrCredentialMode>('apiKey');
  const [apiKey, setApiKey] = useState('');
  const [appId, setAppId] = useState('');
  const [accessToken, setAccessToken] = useState('');
  const [providerBusy, setProviderBusy] = useState(false);
  const [providerMessage, setProviderMessage] = useState('');
  const [shortcutBusy, setShortcutBusy] = useState(false);
  const [shortcutMessage, setShortcutMessage] = useState('');
  const [voiceState, setVoiceState] = useState<VoiceState>('idle');
  const [status, setStatus] = useState('按住 F8 说话');
  const [messages, setMessages] = useState<readonly VoiceMessage[]>([]);
  const [operationId, setOperationId] = useState<string>();
  const [ttsSetting, setTtsSetting] = useState<TtsSetting>(loadTtsSetting);
  const [ttsMessage, setTtsMessage] = useState('');
  const [longConversation, setLongConversation] = useState(false);
  const [wakeStatus, setWakeStatus] = useState<WakeWordStatus>();
  const [wakeRuntime, setWakeRuntime] = useState<WakeWordRuntimeSnapshot>({ state: 'disabled', detail: '唤醒词已关闭' });
  const [wakeBusy, setWakeBusy] = useState(false);
  const [wakeConversation, setWakeConversation] = useState(false);
  const recorder = useRef<MicrophoneRecorder>();
  const bargeInDetector = useRef<BargeInDetector>();
  const bargeTurnSession = useRef(0);
  const bargePlaybackText = useRef('');
  const bargeConversationMode = useRef<SpeechBargeInMode>('single');
  const wakeDetector = useRef<WakeWordDetector>();
  const wakeAcknowledgement = useRef<CachedWakeAcknowledgement>();
  const wakeAcknowledgementRequest = useRef<{ readonly key: string; readonly promise: Promise<CachedWakeAcknowledgement | undefined> }>();
  const wakeConversationRef = useRef(false);
  const wakeConversationStartedAt = useRef<number>();
  const wakeRestartTimer = useRef<number>();
  const wakeActivationSession = useRef(0);
  const operationRef = useRef<string>();
  const waitingForOperation = useRef(false);
  const answer = useRef('');
  const stateRef = useRef<VoiceState>('idle');
  const speechSession = useRef(0);
  const playback = useRef<HTMLAudioElement>();
  const playbackUrl = useRef<string>();
  const playbackCompletion = useRef<() => void>();
  const onlineSpeechQueue = useRef<EncodedSpeechPlaybackQueue>();
  const incrementalSpeechTurn = useRef<IncrementalSpeechTurn>();
  const transcript = useRef('');
  const metricsRef = useRef<VoicePerformanceMetrics>({});
  const turnStartedAt = useRef<number>();
  const releasedAt = useRef<number>();
  const firstTokenSeen = useRef(false);
  const releasePending = useRef(false);
  const turnSession = useRef(0);
  const longConversationRef = useRef(false);
  const longConversationStartedAt = useRef<number>();
  const longRestartTimer = useRef<number>();
  const terminalOperations = useRef(new Set<string>());
  const interruptingOperation = useRef<string>();

  const configured = config?.asr === 'configured';
  const canRecord = configured;
  useEffect(() => { stateRef.current = voiceState; }, [voiceState]);
  useEffect(() => {
    if (stateRef.current === 'idle') setStatus(`按住 ${shortcut.label} 说话`);
  }, [shortcut.label]);
  useEffect(() => {
    operationRef.current = operationId;
  }, [operationId]);

  useEffect(() => {
    const unsubscribePushToTalk = window.velia.voice.onPushToTalk(handlePushToTalk);
    const unsubscribeLongConversation = window.velia.voice.onLongConversation((event) => {
      if (event.edge === 'pressed') void toggleLongConversation();
    });
    const unsubscribeShortcut = window.velia.voice.onShortcut(() => void toggleRecording());
    const unsubscribeCommand = window.velia.assistant.onCommand(handleAssistantCommand);
    const unsubscribeConversation = window.velia.conversation.onEvent(handleCoreEvent);
    window.velia.voice.inputReady(true);
    return () => {
      window.velia.voice.inputReady(false);
      unsubscribePushToTalk();
      unsubscribeLongConversation();
      unsubscribeShortcut();
      unsubscribeCommand();
      unsubscribeConversation();
      recorder.current?.cancel();
      speechSession.current += 1;
      stopPlayback();
      const queue = onlineSpeechQueue.current;
      onlineSpeechQueue.current = undefined;
      void queue?.close().catch(() => undefined);
      clearLongRestartTimer();
      clearWakeRestartTimer();
    };
  }, [canRecord, configured, shortcut.label, ttsSetting]);

  useEffect(() => {
    let mounted = true;
    const detector = new WakeWordDetector();
    wakeDetector.current = detector;
    const unsubscribePrivacy = window.velia.wake.onPrivacy((event) => {
      detector.setPrivacyPaused(event.paused, privacyReason(event.reason));
    });
    void window.velia.wake.status().then((next) => {
      if (!mounted) return;
      setWakeStatus(next);
      if (next.enabled && next.available) detector.start(handleWakeDetected, setWakeRuntime);
      else setWakeRuntime({
        state: next.available ? 'disabled' : 'unavailable',
        detail: next.available ? '唤醒词已关闭' : next.unavailableReason ?? '本地唤醒组件不可用',
      });
    }).catch((error: unknown) => {
      if (mounted) setWakeRuntime({ state: 'error', detail: readError(error, '无法读取唤醒设置。') });
    });
    return () => {
      mounted = false;
      unsubscribePrivacy();
      detector.dispose();
      if (wakeDetector.current === detector) wakeDetector.current = undefined;
    };
  }, [canRecord, shortcut.label]);

  useEffect(() => {
    if (wakeStatus?.enabled && wakeStatus.available) void prepareWakeAcknowledgement();
  }, [ttsSetting, wakeStatus?.available, wakeStatus?.enabled]);

  useEffect(() => {
    const paused = !canRecord || voiceState !== 'idle' || longConversation || wakeConversation;
    const reason = !configured
        ? '语音识别尚未配置，唤醒麦克风已暂停'
        : longConversation
          ? '长对话正在使用麦克风'
          : wakeConversation
            ? '唤醒对话正在进行'
          : voiceState !== 'idle'
            ? '当前语音任务进行中'
            : '';
    wakeDetector.current?.setApplicationPaused(paused, reason);
  }, [canRecord, configured, longConversation, voiceState, wakeConversation, wakeStatus?.enabled]);

  useEffect(() => {
    if (liveSessionHandoff.isActive()) return;
    const snapshot: AssistantOverlaySnapshot = {
      phase: voiceState,
      status,
      transcript: transcript.current,
      answer: answer.current,
      canStop: longConversation || wakeConversation || ['recording', 'transcribing', 'thinking', 'responding', 'speaking'].includes(voiceState),
      shortcutLabel: longConversation ? 'Ctrl + Alt + L' : shortcut.label,
      wakeWordActive: wakeRuntime.state === 'listening',
      metrics: metricsRef.current,
      updatedAtUtc: new Date().toISOString(),
    };
    void window.velia.assistant.publish(snapshot).catch(() => undefined);
  }, [voiceState, status, messages, shortcut.label, longConversation, wakeConversation, wakeRuntime.state]);

  async function handleWakeDetected(): Promise<void> {
    if (!canRecord || longConversationRef.current || wakeConversationRef.current || !['idle', 'error'].includes(stateRef.current)) return;
    wakeConversationRef.current = true;
    wakeConversationStartedAt.current = Date.now();
    setWakeConversation(true);
    setMessages([]);
    const activation = wakeActivationSession.current + 1;
    wakeActivationSession.current = activation;
    transition('starting', '已唤醒，Velia 正在回应…');
    await playWakeAcknowledgement(activation);
    if (wakeActivationSession.current !== activation || !wakeConversationRef.current) return;
    transition('idle', '我在听，请继续说');
    await startRecording('wakeCommand');
  }

  function handlePushToTalk(event: InputEvent): void {
    if (event.edge === 'pressed') {
      releasePending.current = false;
      if (wakeConversationRef.current) {
        stopWakeConversation('唤醒对话已切换为快捷键录音');
        void startRecording();
      } else if (longConversationRef.current && operationRef.current && ['thinking', 'responding', 'speaking'].includes(stateRef.current)) {
        void startTaskCorrectionRecording(operationRef.current);
      } else if (stateRef.current === 'idle' || stateRef.current === 'error') void startRecording();
      else stopCurrentWork();
    } else if (event.edge === 'released' && ['starting', 'recording'].includes(stateRef.current)) {
      releasePending.current = true;
      releasedAt.current = performance.now();
      if (stateRef.current === 'recording') void finishRecording();
    }
  }

  async function startTaskCorrectionRecording(operation: string): Promise<void> {
    speechSession.current += 1;
    stopPlayback();
    transcript.current = '';
    answer.current = '';
    metricsRef.current = {};
    turnStartedAt.current = performance.now();
    releasedAt.current = undefined;
    firstTokenSeen.current = false;
    const session = turnSession.current + 1;
    turnSession.current = session;
    interruptingOperation.current = operation;
    const nextRecorder = new MicrophoneRecorder('pushToTalk');
    recorder.current = nextRecorder;
    transition('starting', '正在聆听你的任务纠正…');
    try {
      await nextRecorder.start(() => void finishRecording());
      if (turnSession.current !== session || recorder.current !== nextRecorder) {
        nextRecorder.cancel();
        return;
      }
      transition('recording', `正在聆听纠正；松开 ${shortcut.label} 自动发送`);
      if (releasePending.current) await finishRecording();
    } catch (error: unknown) {
      if (turnSession.current !== session) return;
      recorder.current = undefined;
      interruptingOperation.current = undefined;
      longConversationRef.current = false;
      setLongConversation(false);
      transition('error', readMicrophoneError(error));
    }
  }

  function handleAssistantCommand(command: 'toggleRecording' | 'stop' | 'collapse' | 'expand'): void {
    if (command === 'toggleRecording') void toggleRecording();
    else if (command === 'stop') stopCurrentWork();
  }

  function stopCurrentWork(): void {
    if (wakeConversationRef.current) {
      stopWakeConversation('唤醒对话已结束，继续等待“嘿 Velia”');
      return;
    }
    if (longConversationRef.current) {
      stopLongConversation('长对话已停止；按 Ctrl + Alt + L 可再次开启');
      return;
    }
    turnSession.current += 1;
    releasePending.current = false;
    if (stateRef.current === 'recording' || stateRef.current === 'starting') {
      recorder.current?.cancel(); recorder.current = undefined;
      transition('idle', `录音已取消；按住 ${shortcut.label} 重新说话`);
      return;
    }
    speechSession.current += 1;
    stopPlayback();
    const operation = operationRef.current;
    if (operation) void window.velia.conversation.cancel(operation).catch(() => undefined);
    else { transition('idle', `已停止；按住 ${shortcut.label} 继续`); }
  }

  async function toggleLongConversation(): Promise<void> {
    if (wakeConversationRef.current) {
      stopWakeConversation('唤醒对话已结束');
    }
    if (longConversationRef.current) {
      stopLongConversation('长对话已停止；按 Ctrl + Alt + L 可再次开启');
      return;
    }
    if (!canRecord || !['idle', 'error'].includes(stateRef.current) || operationRef.current) {
      setStatus(!configured ? '请先配置火山语音识别' : '请先停止当前语音或任务');
      return;
    }
    longConversationRef.current = true;
    longConversationStartedAt.current = Date.now();
    setLongConversation(true);
    setMessages([]);
    scheduleLongListening(0);
  }

  function stopLongConversation(nextStatus: string): void {
    longConversationRef.current = false;
    longConversationStartedAt.current = undefined;
    setLongConversation(false);
    clearLongRestartTimer();
    turnSession.current += 1;
    releasePending.current = false;
    recorder.current?.cancel();
    recorder.current = undefined;
    speechSession.current += 1;
    stopPlayback();
    const operation = operationRef.current;
    if (operation) void window.velia.conversation.cancel(operation).catch(() => undefined);
    operationRef.current = undefined;
    interruptingOperation.current = undefined;
    setOperationId(undefined);
    transition('idle', nextStatus);
  }

  function scheduleLongListening(delayMs = 350): void {
    clearLongRestartTimer();
    if (!longConversationRef.current) return;
    const startedAt = longConversationStartedAt.current;
    if (startedAt !== undefined && Date.now() - startedAt >= 30 * 60 * 1_000) {
      stopLongConversation('长对话已达到 30 分钟安全上限，已自动结束');
      return;
    }
    transition('idle', '长对话待命，正在准备继续聆听…');
    longRestartTimer.current = window.setTimeout(() => {
      longRestartTimer.current = undefined;
      if (longConversationRef.current) void startRecording('longConversation');
    }, delayMs);
  }

  function clearLongRestartTimer(): void {
    if (longRestartTimer.current === undefined) return;
    window.clearTimeout(longRestartTimer.current);
    longRestartTimer.current = undefined;
  }

  function stopWakeConversation(nextStatus: string): void {
    wakeConversationRef.current = false;
    wakeConversationStartedAt.current = undefined;
    setWakeConversation(false);
    wakeActivationSession.current += 1;
    clearWakeRestartTimer();
    turnSession.current += 1;
    releasePending.current = false;
    recorder.current?.cancel();
    recorder.current = undefined;
    speechSession.current += 1;
    stopPlayback();
    const operation = operationRef.current;
    if (operation) void window.velia.conversation.cancel(operation).catch(() => undefined);
    operationRef.current = undefined;
    interruptingOperation.current = undefined;
    waitingForOperation.current = false;
    setOperationId(undefined);
    transition('idle', nextStatus);
  }

  function scheduleWakeListening(delayMs = 280): void {
    clearWakeRestartTimer();
    if (!wakeConversationRef.current) return;
    const startedAt = wakeConversationStartedAt.current;
    if (wakeConversationExpired(startedAt, Date.now())) {
      stopWakeConversation('连续对话已达到 10 分钟安全上限，继续等待“嘿 Velia”');
      return;
    }
    transition('idle', '我在听，可以直接继续说；安静 9 秒后退出');
    wakeRestartTimer.current = window.setTimeout(() => {
      wakeRestartTimer.current = undefined;
      if (wakeConversationRef.current) void startRecording('wakeCommand');
    }, delayMs);
  }

  function clearWakeRestartTimer(): void {
    if (wakeRestartTimer.current === undefined) return;
    window.clearTimeout(wakeRestartTimer.current);
    wakeRestartTimer.current = undefined;
  }

  function transition(next: VoiceState, nextStatus: string): void {
    stateRef.current = next;
    setVoiceState(next);
    setStatus(nextStatus);
  }

  async function saveCredentials(): Promise<void> {
    setProviderBusy(true);
    setProviderMessage('');
    try {
      await window.velia.voice.saveAsr(mode === 'apiKey'
        ? { mode, apiKey: apiKey.trim() }
        : { mode, appId: appId.trim(), accessToken: accessToken.trim() });
      setApiKey(''); setAppId(''); setAccessToken('');
      onAsrConfigChange('configured');
      setProviderMessage('火山语音凭据已加密保存，不会在界面中回显。');
    } catch (error: unknown) {
      setProviderMessage(readError(error, '火山语音凭据保存失败。'));
    } finally { setProviderBusy(false); }
  }

  async function changeShortcut(shortcutId: VoiceShortcutId): Promise<void> {
    if (!['idle', 'error'].includes(stateRef.current)) {
      setShortcutMessage('请先停止当前录音或任务，再更换快捷键。');
      return;
    }
    setShortcutBusy(true);
    setShortcutMessage('正在保存快捷键…');
    try {
      const next = await window.velia.voice.setShortcut(shortcutId);
      onShortcutChange(next);
      setStatus(`按住 ${next.label} 说话`);
      setShortcutMessage(`已启用 ${next.label}，设置会在下次启动时保留。`);
    } catch (error: unknown) {
      setShortcutMessage(readError(error, '快捷键保存失败。'));
    } finally {
      setShortcutBusy(false);
    }
  }

  async function removeCredentials(): Promise<void> {
    setProviderBusy(true);
    try {
      await window.velia.voice.removeAsr();
      onAsrConfigChange('notConfigured');
      setProviderMessage('已删除火山语音凭据。');
    } catch (error: unknown) { setProviderMessage(readError(error, '删除失败。')); }
    finally { setProviderBusy(false); }
  }

  async function testAsr(): Promise<void> {
    setProviderBusy(true);
    setProviderMessage('正在检测火山语音连接…');
    try {
      const result = await window.velia.voice.testAsr();
      setProviderMessage(result.message);
    } catch (error: unknown) { setProviderMessage(readError(error, '连接检测失败。')); }
    finally { setProviderBusy(false); }
  }

  async function openAsrPortal(): Promise<void> {
    setProviderMessage('');
    try {
      await window.velia.system.openExternalResource('volcengineSpeechApiKeys');
      setProviderMessage('已打开豆包语音 API Key 管理页。推荐复制单一 API Key，不需要 App ID。');
    } catch (error: unknown) {
      setProviderMessage(readError(error, '无法打开豆包语音 API Key 管理页。'));
    }
  }

  async function prepareWakeAcknowledgement(): Promise<CachedWakeAcknowledgement | undefined> {
    const key = `${ttsSetting.voiceId}:${ttsSetting.rate}`;
    if (wakeAcknowledgement.current && wakeAcknowledgementKey(wakeAcknowledgement.current) === key) {
      return wakeAcknowledgement.current;
    }
    const stored = loadWakeAcknowledgement();
    if (stored && wakeAcknowledgementKey(stored) === key) {
      wakeAcknowledgement.current = stored;
      return stored;
    }
    if (wakeAcknowledgementRequest.current?.key === key) return wakeAcknowledgementRequest.current.promise;

    const promise = window.velia.voice.synthesize('我在', ttsSetting.voiceId, ttsSetting.rate)
      .then((result): CachedWakeAcknowledgement | undefined => {
        if (result.mimeType !== 'audio/mpeg' || result.audioBase64.length < 16 || result.audioBase64.length > 500_000) return undefined;
        const cached: CachedWakeAcknowledgement = {
          schema: 'velia-wake-ack/v1',
          voiceId: ttsSetting.voiceId,
          rate: ttsSetting.rate,
          audioBase64: result.audioBase64,
          mimeType: 'audio/mpeg',
        };
        localStorage.setItem(wakeAcknowledgementStorageKey, JSON.stringify(cached));
        if (`${ttsSetting.voiceId}:${ttsSetting.rate}` === key) wakeAcknowledgement.current = cached;
        return cached;
      })
      .catch(() => undefined)
      .finally(() => {
        if (wakeAcknowledgementRequest.current?.key === key) wakeAcknowledgementRequest.current = undefined;
      });
    wakeAcknowledgementRequest.current = { key, promise };
    return promise;
  }

  async function playWakeAcknowledgement(activation: number): Promise<void> {
    const prepared = await settleWithinWakeAcknowledgement(prepareWakeAcknowledgement(), 900);
    if (wakeActivationSession.current !== activation || !wakeConversationRef.current) return;
    if (prepared) {
      try {
        const completed = await playTransientAudio(base64AudioBlob(prepared.audioBase64, prepared.mimeType), activation, '我在');
        if (completed) return;
      } catch {
        // A local chime below keeps wake-up usable when cached audio cannot play.
      }
    }
    await playTransientAudio(createWakeChimeBlob(), activation, '我在').catch(() => undefined);
  }

  function playTransientAudio(blob: Blob, activation: number, label: string): Promise<boolean> {
    const url = URL.createObjectURL(blob);
    playbackUrl.current = url;
    const audio = new Audio(url);
    playback.current = audio;
    return new Promise<boolean>((resolve, reject) => {
      let settled = false;
      const finish = (completed: boolean): void => {
        if (settled) return;
        settled = true;
        audio.onplaying = null;
        audio.onended = null;
        audio.onerror = null;
        if (playback.current === audio) playback.current = undefined;
        if (playbackCompletion.current === cancel) playbackCompletion.current = undefined;
        releasePlaybackUrl();
        resolve(completed);
      };
      const fail = (): void => {
        if (settled) return;
        settled = true;
        audio.onplaying = null;
        audio.onended = null;
        audio.onerror = null;
        if (playback.current === audio) playback.current = undefined;
        if (playbackCompletion.current === cancel) playbackCompletion.current = undefined;
        releasePlaybackUrl();
        reject(new Error('Wake acknowledgement playback failed.'));
      };
      const cancel = (): void => finish(false);
      playbackCompletion.current = cancel;
      audio.onplaying = () => {
        if (wakeActivationSession.current === activation) setStatus(label);
      };
      audio.onended = () => finish(true);
      audio.onerror = fail;
      void audio.play().catch(fail);
    });
  }

  async function toggleWakeWord(): Promise<void> {
    if (wakeBusy) return;
    const enable = !wakeStatus?.enabled;
    setWakeBusy(true);
    try {
      const next = await window.velia.wake.setEnabled(enable);
      setWakeStatus(next);
      const detector = wakeDetector.current;
      if (enable && next.available && detector) {
        void prepareWakeAcknowledgement();
        detector.start(handleWakeDetected, setWakeRuntime);
      } else {
        if (wakeConversationRef.current) stopWakeConversation('唤醒词已关闭');
        detector?.stop();
      }
    } catch (error: unknown) {
      setWakeRuntime({ state: 'error', detail: readError(error, '无法更改唤醒设置。') });
    } finally {
      setWakeBusy(false);
    }
  }

  async function toggleRecording(): Promise<void> {
    const current = stateRef.current;
    if (wakeConversationRef.current) {
      stopWakeConversation('唤醒对话已切换为手动录音');
      await startRecording();
      return;
    }
    if (current === 'recording') {
      await finishRecording();
      return;
    }
    if (current === 'speaking') {
      speechSession.current += 1;
      stopPlayback();
      setVoiceState('idle'); setStatus(`播报已停止；按住 ${shortcut.label} 继续`);
      return;
    }
    if (operationRef.current) {
      await window.velia.conversation.cancel(operationRef.current).catch(() => undefined);
      return;
    }
    if (!canRecord || !['idle', 'error'].includes(current)) {
      setStatus(!configured ? '请先配置火山语音识别' : '当前任务尚未结束');
      return;
    }

    await startRecording();
  }

  async function startRecording(recordingMode: 'pushToTalk' | 'longConversation' | 'wakeCommand' = 'pushToTalk'): Promise<void> {
    const current = stateRef.current;
    if (!canRecord || !['idle', 'error'].includes(current)) {
      setStatus(!configured ? '请先配置火山语音识别' : '当前任务尚未结束');
      return;
    }
    speechSession.current += 1; stopPlayback();
    transcript.current = ''; answer.current = ''; metricsRef.current = {};
    turnStartedAt.current = performance.now(); releasedAt.current = undefined; firstTokenSeen.current = false;
    const session = turnSession.current + 1;
    turnSession.current = session;
    if (recordingMode === 'pushToTalk') setMessages([]);
    const nextRecorder = new MicrophoneRecorder(recordingMode);
    recorder.current = nextRecorder;
    transition('starting', '正在打开麦克风…');
    try {
      await liveSessionHandoff.stopBeforeOrdinaryRecording();
      await nextRecorder.start(() => void finishRecording());
      if (turnSession.current !== session || recorder.current !== nextRecorder) {
        nextRecorder.cancel();
        return;
      }
      transition('recording', recordingMode === 'longConversation'
        ? '长对话正在聆听；说完后自动发送，Ctrl + Alt + L 退出'
        : recordingMode === 'wakeCommand'
          ? '已唤醒，正在聆听；说完后自动发送'
          : `正在聆听；松开 ${shortcut.label} 自动发送（最长 20 秒）`);
      if (recordingMode === 'pushToTalk' && releasePending.current) await finishRecording();
    } catch (error: unknown) {
      if (turnSession.current !== session) return;
      recorder.current = undefined;
      if (recordingMode === 'longConversation') {
        longConversationRef.current = false;
        setLongConversation(false);
      }
      transition('error', readMicrophoneError(error));
    }
  }

  async function finishRecording(): Promise<void> {
    const currentRecorder = recorder.current;
    if (!currentRecorder || stateRef.current !== 'recording') return;
    recorder.current = undefined;
    releasePending.current = false;
    const session = turnSession.current;
    transition('transcribing', '正在识别语音…');
    try {
      const recording = await currentRecorder.stop();
      await processCapturedRecording(recording, currentRecorder.mode, session);
    } catch (error: unknown) {
      handleRecordingFailure(error, currentRecorder.mode, session);
    }
  }

  async function processCapturedRecording(
    recording: RecordingResult,
    recordingMode: 'pushToTalk' | 'longConversation' | 'wakeCommand' | 'bargeIn',
    session: number,
  ): Promise<void> {
    const interruptionMode = recordingMode === 'bargeIn' ? bargeConversationMode.current : undefined;
    const isLongTurn = recordingMode === 'longConversation' || interruptionMode === 'long';
    const isWakeTurn = recordingMode === 'wakeCommand' || interruptionMode === 'wake';
    metricsRef.current = { ...metricsRef.current, recordingMs: recording.durationMs };
    if (!recording.speechDetected) {
      if (isLongTurn) stopLongConversation('长对话已因 12 秒没有听到说话而自动结束');
      else if (isWakeTurn) stopWakeConversation('连续对话已因 9 秒没有听到说话而结束，继续等待“嘿 Velia”');
      return;
    }
    if (recording.durationMs < 250) throw new Error('录音太短，请至少说半秒。');
    const transcription = await window.velia.voice.transcribe(bytesToBase64(recording.wav));
    if (turnSession.current !== session) return;
    if (recordingMode === 'bargeIn' && isLikelyPlaybackEcho(transcription.text, bargePlaybackText.current)) {
      bargePlaybackText.current = '';
      if (interruptionMode === 'long') scheduleLongListening(450);
      else if (interruptionMode === 'wake') scheduleWakeListening(450);
      else transition('idle', `没有听到新的指令；按住 ${shortcut.label} 继续`);
      return;
    }
    if (isLongTurn && isVoiceConversationStop(transcription.text)) {
      stopLongConversation('长对话已按你的语音指令结束');
      return;
    }
    if (isWakeTurn && isWakeConversationExit(transcription.text)) {
      stopWakeConversation('好的，连续对话已结束，继续等待“嘿 Velia”');
      return;
    }
    if (isVoiceConversationStop(transcription.text)) {
      stopCurrentWork();
      return;
    }
    transcript.current = transcription.text;
    metricsRef.current = { ...metricsRef.current, asrMs: transcription.providerLatencyMs };
    setMessages((current) => [...current, { role: 'user', text: transcription.text }, { role: 'assistant', text: '' }]);
    transition('thinking', `已识别（${(transcription.providerLatencyMs / 1_000).toFixed(2)} 秒），正在思考…`);
    waitingForOperation.current = true;
    answer.current = '';
    bargePlaybackText.current = '';
    const result = await window.velia.conversation.submitText(transcription.text, 'voice', 'auto');
    if (turnSession.current !== session) {
      await window.velia.conversation.cancel(result.operationId).catch(() => undefined);
      return;
    }
    if (!terminalOperations.current.has(result.operationId)) {
      operationRef.current = result.operationId;
      setOperationId(result.operationId);
    }
    interruptingOperation.current = undefined;
    waitingForOperation.current = false;
  }

  function handleRecordingFailure(
    error: unknown,
    recordingMode: 'pushToTalk' | 'longConversation' | 'wakeCommand' | 'bargeIn',
    session: number,
  ): void {
    if (turnSession.current !== session) return;
    waitingForOperation.current = false;
    interruptingOperation.current = undefined;
    if (recordingMode === 'wakeCommand' || (recordingMode === 'bargeIn' && bargeConversationMode.current === 'wake')) {
      stopWakeConversation(readError(error, '唤醒对话识别失败，已返回待机。'));
      return;
    }
    if (longConversationRef.current || recordingMode === 'longConversation') {
      longConversationRef.current = false;
      setLongConversation(false);
      longConversationStartedAt.current = undefined;
    }
    transition('error', readError(error, '语音识别失败。'));
  }

  function handleCoreEvent(event: CoreEvent): void {
    if (interruptingOperation.current === event.operationId) {
      if (event.type === 'completed' || event.type === 'failed' || event.type === 'cancelled') {
        markTerminalOperation(event.operationId);
        operationRef.current = undefined;
        setOperationId(undefined);
      }
      return;
    }
    const currentOperation = operationRef.current;
    if (!currentOperation && waitingForOperation.current) {
      operationRef.current = event.operationId;
      setOperationId(event.operationId);
    } else if (currentOperation !== event.operationId) return;

    if (event.type === 'content' && event.delta) {
      if (!firstTokenSeen.current) {
        firstTokenSeen.current = true;
        const baseline = releasedAt.current ?? turnStartedAt.current;
        if (baseline !== undefined) metricsRef.current = { ...metricsRef.current, firstTokenMs: Math.round(performance.now() - baseline) };
      }
      answer.current += event.delta;
      transition('responding', '正在回答…');
      setMessages((current) => updateLastAssistant(current, answer.current));
      enqueueIncrementalSpeech(event.operationId, event.delta);
    } else if (event.type === 'tool') {
      setStatus(voiceToolStatus(event.toolCall?.name));
    } else if (event.type === 'mission' && event.mission) {
      if (event.mission.state === 'awaitingInput') setStatus(event.mission.message ? `等待你回答：${event.mission.message}` : '等待你提供信息…');
      else if (event.mission.state === 'correcting') setStatus('正在停止旧计划并应用纠正…');
      else if (event.mission.state === 'running') setStatus('任务正在执行…');
    } else if (event.type === 'completed') {
      markTerminalOperation(event.operationId);
      operationRef.current = undefined; setOperationId(undefined);
      const baseline = releasedAt.current ?? turnStartedAt.current;
      if (baseline !== undefined) metricsRef.current = { ...metricsRef.current, totalMs: Math.round(performance.now() - baseline) };
      const finalAnswer = answer.current.trim();
      if (incrementalSpeechTurn.current?.operationId === event.operationId) {
        void completeIncrementalSpeech(event.operationId);
      } else if (finalAnswer) speak(finalAnswer);
      else if (shouldResumeOrdinaryLongConversation(longConversationRef.current, 'completed')) scheduleLongListening();
      else if (wakeConversationRef.current) scheduleWakeListening();
      else transition('idle', `回答完成；按住 ${shortcut.label} 继续`);
    } else if (event.type === 'failed' || event.type === 'cancelled') {
      markTerminalOperation(event.operationId);
      operationRef.current = undefined; setOperationId(undefined);
      if (event.type === 'cancelled') {
        if (shouldResumeOrdinaryLongConversation(longConversationRef.current, 'cancelled')) scheduleLongListening();
        else if (wakeConversationRef.current) stopWakeConversation('任务已停止，继续等待“嘿 Velia”');
        else transition('idle', `任务已停止；按住 ${shortcut.label} 继续`);
      } else {
        const failureSpeech = coreFailureSpeechText(event) ?? '任务没有完成。';
        if (!wakeConversationRef.current && !longConversationRef.current) transition('error', failureSpeech);
        speak(failureSpeech);
      }
    }
  }

  function enqueueIncrementalSpeech(activeOperationId: string, delta: string): void {
    let turn = incrementalSpeechTurn.current;
    if (!turn || turn.operationId !== activeOperationId) {
      const session = speechSession.current + 1;
      speechSession.current = session;
      stopPlayback();
      const queue = onlineSpeechQueue.current ??= new EncodedSpeechPlaybackQueue();
      turn = {
        operationId: activeOperationId,
        session,
        buffer: new IncrementalSpeechTextBuffer(),
        queue,
        synthesisTail: Promise.resolve(),
        playbackResults: [],
        playbackStarted: false,
        failed: false,
      };
      incrementalSpeechTurn.current = turn;
    }
    enqueueIncrementalSpeechChunks(turn, turn.buffer.push(delta));
  }

  function enqueueIncrementalSpeechChunks(turn: IncrementalSpeechTurn, chunks: readonly string[]): void {
    if (chunks.length === 0 || turn.failed) return;
    transition('speaking', '正在生成自然语音…');
    startSpeechBargeIn();
    setTtsMessage('');
    for (const chunk of chunks) {
      // Start network synthesis immediately; only the final scheduling step is
      // serialized so chunks can never play out of textual order.
      const generated = Promise.resolve()
        .then(() => window.velia.voice.synthesize(chunk, ttsSetting.voiceId, ttsSetting.rate))
        .then(
          value => ({ ok: true as const, value }),
          error => ({ ok: false as const, error }),
        );
      turn.synthesisTail = turn.synthesisTail.then(async () => {
        const result = await generated;
        if (!result.ok) throw result.error;
        if (turn.failed || speechSession.current !== turn.session || incrementalSpeechTurn.current !== turn) return;
        const ticket = await turn.queue.schedule(result.value.audioBase64);
        if (!turn.playbackStarted) {
          turn.playbackStarted = true;
          setStatus(`正在播报；再次按 ${shortcut.label} 可停止`);
        }
        turn.playbackResults.push(ticket.done);
      }).catch(() => {
        turn.failed = true;
        if (incrementalSpeechTurn.current === turn) turn.queue.clear();
      });
    }
  }

  async function completeIncrementalSpeech(activeOperationId: string): Promise<void> {
    const turn = incrementalSpeechTurn.current;
    if (!turn || turn.operationId !== activeOperationId) return;
    enqueueIncrementalSpeechChunks(turn, turn.buffer.flush());
    await turn.synthesisTail;
    if (speechSession.current !== turn.session || incrementalSpeechTurn.current !== turn) return;
    if (turn.failed && !turn.playbackStarted) {
      incrementalSpeechTurn.current = undefined;
      turn.queue.clear();
      speakWithSystemFallback(answer.current, turn.session);
      return;
    }
    const completed = await Promise.all(turn.playbackResults);
    if (speechSession.current !== turn.session || incrementalSpeechTurn.current !== turn) return;
    incrementalSpeechTurn.current = undefined;
    if (turn.failed || completed.some(value => !value)) {
      stopSpeechBargeIn();
      turn.queue.clear();
      setTtsMessage('在线自然语音中断，已停止播报；文字回答仍已保留。');
      resumeAfterSpeech(`语音中断；文字回答已保留；按住 ${shortcut.label} 继续`);
      return;
    }
    turn.queue.completeResponse();
    stopSpeechBargeIn();
    resumeAfterSpeech(`回答完成；按住 ${shortcut.label} 继续`);
  }

  function resumeAfterSpeech(singleTurnStatus: string): void {
    if (longConversationRef.current) scheduleLongListening();
    else if (wakeConversationRef.current) scheduleWakeListening();
    else transition('idle', singleTurnStatus);
  }

  function markTerminalOperation(id: string): void {
    terminalOperations.current.add(id);
    if (terminalOperations.current.size <= 100) return;
    terminalOperations.current.clear();
    terminalOperations.current.add(id);
  }

  function startSpeechBargeIn(): void {
    if (bargeInDetector.current) return;
    bargeConversationMode.current = wakeConversationRef.current
      ? 'wake'
      : longConversationRef.current
        ? 'long'
        : 'single';
    const detector = new BargeInDetector();
    bargeInDetector.current = detector;
    void detector.start(handleSpeechBargeIn, (recording) => void handleSpeechBargeInRecording(recording))
      .then((started) => {
        if (started && ['speaking', 'recording'].includes(stateRef.current)) return;
        detector.stop();
        if (bargeInDetector.current === detector) bargeInDetector.current = undefined;
      });
  }

  function handleSpeechBargeIn(): void {
    if (stateRef.current !== 'speaking') return;
    bargePlaybackText.current = answer.current;
    const session = turnSession.current + 1;
    turnSession.current = session;
    bargeTurnSession.current = session;
    speechSession.current += 1;
    stopPlayback(true);
    transcript.current = '';
    answer.current = '';
    metricsRef.current = {};
    turnStartedAt.current = performance.now();
    releasedAt.current = undefined;
    firstTokenSeen.current = false;
    transition('recording', '已停止播报，正在聆听你的插话…');
  }

  async function handleSpeechBargeInRecording(recording: RecordingResult): Promise<void> {
    bargeInDetector.current = undefined;
    const session = bargeTurnSession.current;
    if (session !== turnSession.current || stateRef.current !== 'recording') return;
    transition('transcribing', '正在识别你的插话…');
    try {
      await processCapturedRecording(recording, 'bargeIn', session);
    } catch (error: unknown) {
      handleRecordingFailure(error, 'bargeIn', session);
    }
  }

  function stopSpeechBargeIn(): void {
    bargeInDetector.current?.stop();
    bargeInDetector.current = undefined;
    bargePlaybackText.current = '';
  }

  function speak(text: string): void {
    void speakOnline(text);
  }

  async function speakOnline(text: string): Promise<void> {
    const session = speechSession.current + 1;
    speechSession.current = session;
    stopPlayback();
    const chunks = splitSpeechText(text);
    if (chunks.length === 0) {
      if (longConversationRef.current) scheduleLongListening();
      else if (wakeConversationRef.current) scheduleWakeListening();
      else transition('idle', `回答完成；按住 ${shortcut.label} 继续`);
      return;
    }
    let fallbackStarted = false;
    let onlinePlaybackStarted = false;
    transition('speaking', '正在生成自然语音…');
    startSpeechBargeIn();
    setTtsMessage('');
    try {
      const queue = onlineSpeechQueue.current ??= new EncodedSpeechPlaybackQueue();
      const playbackResults: Promise<boolean>[] = [];
      for await (const generated of synthesizeInOrder(
        chunks,
        async textChunk => await window.velia.voice.synthesize(textChunk, ttsSetting.voiceId, ttsSetting.rate),
        { maximumConcurrent: 4, isCancelled: () => speechSession.current !== session },
      )) {
        if (speechSession.current !== session) return;
        const ticket = await queue.schedule(generated.value.audioBase64);
        if (!onlinePlaybackStarted) {
          onlinePlaybackStarted = true;
          setStatus(`正在播报；再次按 ${shortcut.label} 可停止`);
        }
        playbackResults.push(ticket.done);
      }
      const completed = await Promise.all(playbackResults);
      if (speechSession.current !== session || completed.some(value => !value)) return;
      queue.completeResponse();
      stopSpeechBargeIn();
      if (longConversationRef.current) scheduleLongListening();
      else if (wakeConversationRef.current) scheduleWakeListening();
      else transition('idle', `回答完成；按住 ${shortcut.label} 继续`);
    } catch {
      if (speechSession.current === session && !fallbackStarted && !onlinePlaybackStarted) {
        fallbackStarted = true;
        onlineSpeechQueue.current?.clear();
        speakWithSystemFallback(chunks.join(' '), session);
      } else if (speechSession.current === session && onlinePlaybackStarted) {
        stopSpeechBargeIn();
        onlineSpeechQueue.current?.clear();
        setTtsMessage('在线自然语音中断，已停止播报；文字回答仍已保留。');
        if (longConversationRef.current) scheduleLongListening();
        else if (wakeConversationRef.current) scheduleWakeListening();
        else transition('idle', `语音中断；文字回答已保留；按住 ${shortcut.label} 继续`);
      }
    }
  }

  function previewVoice(): void { speak('你好，我是 Velia。很高兴和你一起工作。'); }

  function speakWithSystemFallback(text: string, session: number): void {
    setTtsMessage('在线自然语音暂不可用，已使用 Windows 本机声音。');
    const utterance = new SpeechSynthesisUtterance(text);
    utterance.rate = Math.max(0.5, Math.min(1.5, 1 + ttsSetting.rate / 100));
    utterance.onstart = () => {
      if (speechSession.current === session) {
        setStatus(`正在使用本机备用声音播报；再次按 ${shortcut.label} 可停止`);
        startSpeechBargeIn();
      }
    };
    utterance.onend = () => {
      if (speechSession.current !== session) return;
      stopSpeechBargeIn();
      if (longConversationRef.current) scheduleLongListening();
      else if (wakeConversationRef.current) scheduleWakeListening();
      else transition('idle', `回答完成；按住 ${shortcut.label} 继续`);
    };
    utterance.onerror = (event) => {
      if (speechSession.current !== session) return;
      stopSpeechBargeIn();
      if (isSpeechCancellationError(event.error)) {
        if (longConversationRef.current) scheduleLongListening();
        else transition('idle', `播报已停止；按住 ${shortcut.label} 继续`);
      }
      else if (longConversationRef.current) {
        setTtsMessage('语音播报失败，文字回答仍已保留；长对话继续聆听。');
        scheduleLongListening();
      }
      else if (wakeConversationRef.current) stopWakeConversation('语音播报失败，文字回答仍已保留；已返回唤醒待机。');
      else transition('error', '在线语音与系统备用声音均无法播放，文字回答仍已保留。');
    };
    window.speechSynthesis.speak(utterance);
  }

  function updateTtsSetting(next: TtsSetting): void {
    setTtsSetting(next);
    localStorage.setItem(ttsStorageKey, JSON.stringify(next));
    setTtsMessage('播报设置已自动保存。');
  }

  function stopPlayback(preserveBargeIn = false): void {
    incrementalSpeechTurn.current?.buffer.clear();
    incrementalSpeechTurn.current = undefined;
    onlineSpeechQueue.current?.clear();
    const audio = playback.current;
    const complete = playbackCompletion.current;
    playbackCompletion.current = undefined;
    complete?.();
    if (audio) {
      audio.pause();
      audio.removeAttribute('src');
      audio.load();
      playback.current = undefined;
    }
    releasePlaybackUrl();
    window.speechSynthesis.cancel();
    if (!preserveBargeIn) stopSpeechBargeIn();
  }

  function releasePlaybackUrl(): void {
    if (!playbackUrl.current) return;
    URL.revokeObjectURL(playbackUrl.current);
    playbackUrl.current = undefined;
  }

  const credentialReady = mode === 'apiKey' ? apiKey.trim().length >= 4 : appId.trim().length >= 4 && accessToken.trim().length >= 4;

  return (
    <>
      <div className="voice-layout" hidden={!active}>
        <section className="voice-console-card">
          <div className="section-heading"><div><h3>{shortcut.label} 语音对话</h3></div><AudioLines size={21} /></div>
          <div className={`voice-orb voice-orb--${voiceState}`}><Mic size={30} /><span /></div>
          <h2>{status}</h2>
          <p>{longConversation
            ? '普通长对话已开启：每轮说完后自动发送；连续安静 12 秒或达到 30 分钟会安全退出。'
            : wakeConversation
              ? '唤醒连续对话正在进行：回答后自动继续聆听，可以直接插话；安静 9 秒或说“结束对话”退出。'
              : `按住 ${shortcut.label} 立即录音，松开后自动识别并发送。`}</p>
          <button className="button button--primary voice-main-button" type="button" disabled={!canRecord || !['idle', 'recording', 'error', 'speaking'].includes(voiceState)} onClick={() => void toggleRecording()}>
            {voiceState === 'recording' ? <><Square size={15} /> 停止并发送</> : voiceState === 'speaking' ? <><Square size={15} /> 停止播报</> : <><Mic size={15} /> 开始录音</>}
          </button>
          <button className="button button--ghost" type="button" disabled={!canRecord && !longConversation} onClick={() => void toggleLongConversation()}>
            <Radio size={14} /> {longConversation ? '退出长对话' : '开启长对话'}
          </button>
          <div className="voice-long-shortcut" aria-label="普通长对话快捷键 Ctrl 加 Alt 加 L">
            <span>普通长对话快捷键</span>
            <strong><kbd>Ctrl</kbd><b>+</b><kbd>Alt</kbd><b>+</b><kbd>L</kbd></strong>
            <em>再次按下即可停止</em>
          </div>
        </section>

        <div className="voice-settings-stack">
          <section className="voice-settings-card">
            <div className="section-heading"><div><h3>按住说话快捷键</h3></div><KeyRound size={20} /></div>
            <SoftSelect
              label="当前快捷键"
              value={shortcut.id}
              disabled={shortcutBusy || !['idle', 'error'].includes(voiceState)}
              groups={[{ options: [
                { value: 'ctrlShiftSpace', label: 'Ctrl + Shift + 空格' },
                { value: 'f8', label: 'F8（笔记本可能需要 Fn）' },
              ] }]}
              onChange={(value) => void changeShortcut(value as VoiceShortcutId)}
            />
            {shortcutMessage && <p className="inline-message">{shortcutMessage}</p>}
          </section>

          <section className="voice-settings-card">
            <div className="section-heading"><div><h3>“嘿 Velia”唤醒</h3></div><Ear size={20} /></div>
            <p className="settings-copy">本机检测唤醒词，锁屏、睡眠和 Live 会释放麦克风。</p>
            <button
              type="button"
              className={`wake-toggle${wakeStatus?.enabled ? ' wake-toggle--active' : ''}`}
              aria-pressed={wakeStatus?.enabled ?? false}
              disabled={wakeBusy || (!wakeStatus?.available && !wakeStatus?.enabled)}
              onClick={() => void toggleWakeWord()}
            >
              <span aria-hidden="true"><i /></span>
              {wakeStatus?.enabled ? '已开启' : '已关闭'}
            </button>
            <p className={`wake-runtime wake-runtime--${wakeRuntime.state}`}>{wakeRuntimeLabel(wakeRuntime.state)}：{wakeRuntime.detail}</p>
          </section>

          <section className="voice-settings-card voice-broadcast-card">
            <div className="section-heading"><div><h3>语音播报</h3></div><Volume2 size={20} /></div>
            <SoftSelect
              label="播报音色"
              value={ttsSetting.voiceId}
              upward
              groups={[
                { label: '女声', options: edgeVoices.filter((voice) => voice.group === 'female').map((voice) => ({ value: voice.id, label: voice.label })) },
                { label: '男声', options: edgeVoices.filter((voice) => voice.group === 'male').map((voice) => ({ value: voice.id, label: voice.label })) },
                { label: '特色方言', options: edgeVoices.filter((voice) => voice.group === 'dialect').map((voice) => ({ value: voice.id, label: voice.label })) },
              ]}
              onChange={(value) => updateTtsSetting({ ...ttsSetting, voiceId: value as EdgeVoiceId })}
            />
            <label className="range-field"><span>语速调整 <strong>{ttsSetting.rate >= 0 ? '+' : ''}{ttsSetting.rate}%</strong></span><input type="range" min="-50" max="50" step="5" value={ttsSetting.rate} onChange={(event) => updateTtsSetting({ ...ttsSetting, rate: Number(event.target.value) })} /></label>
            <button className="button button--ghost voice-preview-button" type="button" onClick={previewVoice}><Play size={14} /> 试听当前音色</button>
            {ttsMessage && <p className="inline-message">{ttsMessage}</p>}
          </section>
        </div>
      </div>

      <div className={`voice-asr-layer${asrOpen ? ' is-open' : ''}`} aria-hidden={!asrOpen} onMouseDown={(event) => { if (event.target === event.currentTarget) onAsrClose(); }}>
        <section className="voice-asr-modal" aria-label="火山 ASR 配置">
          <header><div><h2>火山 ASR</h2><p>普通语音识别服务</p></div><button type="button" aria-label="关闭火山 ASR 配置" onClick={onAsrClose}><X size={18} /></button></header>
          <div className="section-heading__actions voice-asr-resource">
            <button className="resource-link-button" type="button" onClick={() => void openAsrPortal()}><ExternalLink size={13} />获取 API</button>
          </div>
          <div className="segmented-control">
            <button type="button" className={mode === 'apiKey' ? 'active' : ''} onClick={() => setMode('apiKey')}>新版 App Key</button>
            <button type="button" className={mode === 'legacy' ? 'active' : ''} onClick={() => setMode('legacy')}>旧版双凭据</button>
          </div>
          {mode === 'apiKey' ? (
            <label className="secret-field"><span><KeyRound size={14} /> App Key</span><input type="password" value={apiKey} autoComplete="off" placeholder={configured ? '已保存；输入新值可替换' : '在这里粘贴，不要发到聊天中'} onChange={(event) => setApiKey(event.target.value)} /></label>
          ) : (
            <div className="voice-credential-grid">
              <label className="secret-field"><span>App ID</span><input type="password" value={appId} autoComplete="off" placeholder={configured ? '已保存' : '输入 App ID'} onChange={(event) => setAppId(event.target.value)} /></label>
              <label className="secret-field"><span>Access Token</span><input type="password" value={accessToken} autoComplete="off" placeholder={configured ? '已保存' : '输入 Access Token'} onChange={(event) => setAccessToken(event.target.value)} /></label>
            </div>
          )}
          <div className="model-actions">
            <button className="button button--primary" type="button" disabled={providerBusy || !credentialReady} onClick={() => void saveCredentials()}>保存凭据</button>
            <button className="button button--ghost" type="button" disabled={providerBusy || !configured} onClick={() => void testAsr()}><Wifi size={14} /> 检测连接</button>
            <button className="button button--danger" type="button" disabled={providerBusy || !configured} onClick={() => void removeCredentials()}><Trash2 size={14} /> 删除</button>
          </div>
          {providerMessage && <p className="inline-message">{providerMessage}</p>}
        </section>
      </div>
    </>
  );
}

interface SoftSelectOption { readonly value: string; readonly label: string; }
interface SoftSelectGroup { readonly label?: string; readonly options: readonly SoftSelectOption[]; }

function SoftSelect({ label, value, groups, disabled = false, upward = false, onChange }: {
  readonly label: string;
  readonly value: string;
  readonly groups: readonly SoftSelectGroup[];
  readonly disabled?: boolean;
  readonly upward?: boolean;
  readonly onChange: (value: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const selected = groups.flatMap((group) => group.options).find((option) => option.value === value);

  useEffect(() => {
    if (!open) return;
    const close = (event: PointerEvent) => {
      if (event.target instanceof Node && !root.current?.contains(event.target)) setOpen(false);
    };
    document.addEventListener('pointerdown', close);
    return () => document.removeEventListener('pointerdown', close);
  }, [open]);

  return (
    <div className="voice-soft-field" ref={root} onKeyDown={(event) => { if (event.key === 'Escape') setOpen(false); }}>
      <span>{label}</span>
      <div className={`voice-soft-select${upward ? ' is-upward' : ''}`}>
        <button type="button" className="voice-soft-select__trigger" aria-haspopup="listbox" aria-expanded={open} disabled={disabled} onClick={() => setOpen((current) => !current)}><span>{selected?.label ?? value}</span><b>⌄</b></button>
        <div className="voice-soft-select__menu" role="listbox" hidden={!open}>
          {groups.map((group, groupIndex) => (
            <div className="voice-soft-select__group" key={group.label ?? groupIndex}>
              {group.label && <strong>{group.label}</strong>}
              {group.options.map((option) => <button type="button" role="option" aria-selected={option.value === value} key={option.value} onClick={() => { onChange(option.value); setOpen(false); }}>{option.label}</button>)}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

function voiceToolStatus(name: string | undefined): string {
  switch (name) {
    case 'web_research': return '正在检索网页…';
    case 'browser': return '正在操作浏览器…';
    case 'desktop_context': return '正在处理当前内容…';
    case 'desktop_visual': return '正在操作当前桌面…';
    case 'local_desktop_action': return '正在快速操作当前窗口…';
    case 'windows_control': return '正在执行系统操作…';
    default: return '正在执行任务…';
  }
}

function updateLastAssistant(messages: readonly VoiceMessage[], text: string): readonly VoiceMessage[] {
  const index = messages.findLastIndex((message) => message.role === 'assistant');
  return index < 0 ? messages : messages.map((message, current) => current === index ? { ...message, text } : message);
}

function loadWakeAcknowledgement(): CachedWakeAcknowledgement | undefined {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(wakeAcknowledgementStorageKey) ?? 'null');
    if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) return undefined;
    const value = parsed as Record<string, unknown>;
    if (value.schema !== 'velia-wake-ack/v1' || typeof value.voiceId !== 'string' ||
        !edgeVoices.some((voice) => voice.id === value.voiceId) || typeof value.rate !== 'number' ||
        !Number.isInteger(value.rate) || value.rate < -50 || value.rate > 50 || value.rate % 5 !== 0 ||
        typeof value.audioBase64 !== 'string' || value.audioBase64.length < 16 || value.audioBase64.length > 500_000 ||
        !/^[A-Za-z0-9+/]+={0,2}$/u.test(value.audioBase64) || value.mimeType !== 'audio/mpeg') return undefined;
    return value as unknown as CachedWakeAcknowledgement;
  } catch {
    return undefined;
  }
}

function wakeAcknowledgementKey(value: Pick<CachedWakeAcknowledgement, 'voiceId' | 'rate'>): string {
  return `${value.voiceId}:${value.rate}`;
}

function settleWithinWakeAcknowledgement(
  promise: Promise<CachedWakeAcknowledgement | undefined>,
  milliseconds: number,
): Promise<CachedWakeAcknowledgement | undefined> {
  return new Promise((resolve) => {
    const timer = window.setTimeout(() => resolve(undefined), milliseconds);
    void promise.then((value) => {
      window.clearTimeout(timer);
      resolve(value);
    });
  });
}

function createWakeChimeBlob(): Blob {
  const sampleRate = 16_000;
  const durationFrames = Math.round(sampleRate * 0.16);
  const samples = new Float32Array(durationFrames);
  for (let index = 0; index < samples.length; index += 1) {
    const progress = index / samples.length;
    const envelope = Math.sin(Math.PI * progress) ** 2;
    samples[index] = Math.sin(2 * Math.PI * 720 * index / sampleRate) * envelope * 0.18;
  }
  return new Blob([encodePcm16Wav(samples)], { type: 'audio/wav' });
}

function base64AudioBlob(base64: string, mimeType: string): Blob {
  const bytes = Uint8Array.from(atob(base64), (character) => character.charCodeAt(0));
  return new Blob([bytes], { type: mimeType });
}

function wakeRuntimeLabel(state: WakeWordRuntimeState): string {
  return ({
    disabled: '已关闭',
    starting: '正在启动',
    listening: '正在待机监听',
    processing: '本机识别中',
    paused: '已暂停',
    error: '需要处理',
    unavailable: '组件不可用',
  })[state];
}

function privacyReason(reason: string): string {
  return ({
    'screen-locked': 'Windows 已锁屏，唤醒麦克风已释放',
    'system-suspended': '电脑已睡眠，唤醒麦克风已释放',
    'screen-unlocked': 'Windows 已解锁',
    'system-resumed': '电脑已恢复',
  })[reason] ?? '系统隐私策略已暂停唤醒麦克风';
}

function readMicrophoneError(error: unknown): string {
  if (error instanceof DOMException && error.name === 'NotAllowedError') return '麦克风权限未开启，请允许 Velia 使用麦克风。';
  if (error instanceof DOMException && error.name === 'NotFoundError') return '没有检测到可用麦克风。';
  return readError(error, '无法打开麦克风。');
}

function readError(error: unknown, fallback: string): string {
  if (!(error instanceof Error)) return fallback;
  const match = /(?:Error:\s*)?([a-z_]+):\s*(.+)$/iu.exec(error.message);
  return match?.[2] ?? error.message ?? fallback;
}
