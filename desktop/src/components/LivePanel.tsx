import { useEffect, useRef, useState } from 'react';
import { ExternalLink, Mic, Radio, ShieldCheck, Square, Waves } from 'lucide-react';
import type { AssistantOverlaySnapshot, LiveEvent, LiveState, LiveSubmitLocalTranscriptResult, LiveVisionStatus } from '@velia/protocol';
import { LiveMicrophoneStream } from '../voice/LiveMicrophoneStream';
import { LiveLocalCommandPipeline } from '../voice/LiveLocalCommandPipeline';
import type { LiveLocalUtterance } from '../voice/LiveLocalCommandPipeline';
import { EncodedSpeechPlaybackQueue } from '../voice/EncodedSpeechPlaybackQueue';
import { PcmPlaybackQueue } from '../voice/PcmPlaybackQueue';
import { loadTtsSetting } from '../voice/edgeVoices';
import { liveSessionHandoff } from '../voice/LiveSessionHandoff';
import { LiveResultAcknowledgementStore } from '../voice/LiveResultAcknowledgements';
import { latestLiveTurn, liveOverlayPhase } from '../lib/liveOverlay';
import { isVoiceConversationStop } from '../voice/voiceConversationControl';
import { LiveAudioTurnOwnership } from '../voice/LiveAudioTurnOwnership';

const model = 'gemini-3.1-flash-live-preview';

export function LivePanel({ active, configured, onOpenVision }: {
  readonly active: boolean;
  readonly configured: boolean;
  readonly onOpenVision: () => void;
}) {
  const [state, setState] = useState<LiveState>('inactive');
  const [message, setMessage] = useState('点击开始后，可像打电话一样连续交谈。');
  const [inputTranscript, setInputTranscript] = useState('');
  const [outputTranscript, setOutputTranscript] = useState('');
  const [visionStatus, setVisionStatus] = useState<LiveVisionStatus>({ active: false, frames: 0 });
  const [freshScreenChecks, setFreshScreenChecks] = useState(0);
  const sessionId = useRef<string | undefined>(undefined);
  const microphone = useRef<LiveMicrophoneStream | undefined>(undefined);
  const playback = useRef<PcmPlaybackQueue | undefined>(undefined);
  const localPlayback = useRef<EncodedSpeechPlaybackQueue | undefined>(undefined);
  const resultAcknowledgements = useRef<LiveResultAcknowledgementStore | undefined>(undefined);
  const localCommands = useRef<LiveLocalCommandPipeline<LiveSubmitLocalTranscriptResult> | undefined>(undefined);
  const pendingSends = useRef(0);
  const stopping = useRef(false);
  const failed = useRef(false);
  const inputStartsNewTurn = useRef(false);
  const outputStartsNewTurn = useRef(false);
  const localPlaybackGeneration = useRef(0);
  const pcmPlaybackActive = useRef(false);
  const localPlaybackActive = useRef(false);
  const overlayOwned = useRef(false);
  const audioTurnOwnership = useRef(new LiveAudioTurnOwnership());

  useEffect(() => {
    const unsubscribe = window.velia.live.onEvent(handleEvent);
    const unsubscribeVision = window.velia.live.onVisionStatus(setVisionStatus);
    void window.velia.live.status().then((status) => {
      if (status.state !== 'inactive') {
        setState('error');
        setMessage('检测到上次 Live 会话未正常结束，请重新启动 Velia。');
      }
    }).catch(() => undefined);
    return () => {
      unsubscribe();
      unsubscribeVision();
      void stopSession(false);
    };
  }, []);

  useEffect(() => window.velia.assistant.onCommand((command) => {
    if (command === 'stop' && sessionId.current) void stopSession();
  }), []);

  useEffect(() => liveSessionHandoff.register(
    () => Boolean(sessionId.current),
    () => stopSession(),
  ), []);

  function handleEvent(event: LiveEvent): void {
    if (sessionId.current && event.sessionId !== sessionId.current) return;
    if (event.state) setState(event.state);
    if (event.type === 'toolCall') {
      setFreshScreenChecks((current) => current + 1);
      setMessage('正在读取最新画面…');
    } else if (event.type === 'audio' && event.audioBase64) {
      if (!audioTurnOwnership.current.acceptProviderAudio()) return;
      setState('speaking');
      void playback.current?.enqueue(event.audioBase64).catch(() => {
        setState('error'); setMessage('实时语音播放失败，请重新开始 Live。');
      });
    } else if (event.type === 'localResult' && event.transcript !== undefined) {
      audioTurnOwnership.current.claimLocalResult();
      const startsNewTurn = outputStartsNewTurn.current;
      outputStartsNewTurn.current = false;
      setOutputTranscript((current) => appendTranscript(current, event.transcript!, startsNewTurn));
      void playLocalResult(event.transcript, event.resultOk === true);
    } else if (event.type === 'inputTranscript' && event.transcript !== undefined) {
      // The same user turn may be reported by Gemini after the local ASR has
      // already completed and claimed the result voice. Do not let that late
      // transcript release local ownership and re-enable Gemini PCM. Ownership
      // is released when the microphone actually captures the next utterance.
      const startsNewTurn = inputStartsNewTurn.current;
      inputStartsNewTurn.current = false;
      setInputTranscript((current) => appendTranscript(current, event.transcript!, startsNewTurn));
    } else if (event.type === 'outputTranscript' && event.transcript !== undefined) {
      const startsNewTurn = outputStartsNewTurn.current;
      outputStartsNewTurn.current = false;
      setOutputTranscript((current) => appendTranscript(current, event.transcript!, startsNewTurn));
    } else if (event.type === 'interrupted') {
      playback.current?.clear();
      cancelLocalPlayback();
      setState('listening'); setMessage('已停止播报，正在听你说话。');
    } else if (event.type === 'reconnecting') {
      setState('reconnecting'); setMessage(event.message ?? '网络波动，正在恢复会话…');
    } else if (event.type === 'failed') {
      playback.current?.clear();
      cancelLocalPlayback();
      failed.current = true;
      setState('error'); setMessage(event.message ?? 'Live 会话中断，请重试。');
      void stopSession(false);
    } else if (event.type === 'closed') {
      void stopSession(false);
      if (!failed.current) { setState('inactive'); setMessage('Live 已结束。'); }
    } else if (event.type === 'listening' || event.type === 'turnComplete') {
      if (event.type === 'turnComplete') {
        playback.current?.completeResponse();
        inputStartsNewTurn.current = true;
        outputStartsNewTurn.current = true;
      }
      if (playback.current?.active || localPlayback.current?.active) {
        setState('speaking'); setMessage('正在播报，你可以随时插话。');
      } else {
        setState('listening'); setMessage('正在聆听，你可以继续说话。');
      }
    }
  }

  function updatePlaybackActivity(source: 'pcm' | 'local', active: boolean): void {
    if (source === 'pcm') pcmPlaybackActive.current = active;
    else localPlaybackActive.current = active;
    const anyActive = pcmPlaybackActive.current || localPlaybackActive.current;
    microphone.current?.setPlaybackActive(anyActive);
    if (!sessionId.current || stopping.current) return;
    if (anyActive) {
      setState('speaking'); setMessage('正在播报，你可以随时插话。');
    } else {
      setState('listening'); setMessage('正在聆听，你可以继续说话。');
    }
  }

  function cancelLocalPlayback(): void {
    localPlaybackGeneration.current += 1;
    audioTurnOwnership.current.reset();
    localPlayback.current?.clear();
    if (sessionId.current && !stopping.current && !pcmPlaybackActive.current && !localPlaybackActive.current) {
      setState('listening'); setMessage('正在聆听，你可以继续说话。');
    }
  }

  async function playLocalResult(transcript: string, resultOk: boolean): Promise<void> {
    const activeSession = sessionId.current;
    const queue = localPlayback.current;
    if (!activeSession || !queue || stopping.current) return;
    const generation = ++localPlaybackGeneration.current;
    playback.current?.clear();
    queue.clear();
    setState('speaking'); setMessage('正在播报已验证的任务结果…');
    try {
      const setting = loadTtsSetting();
      const audioBase64 = await getResultAcknowledgementStore().prepareText(
        setting,
        transcript,
        (content, voiceId, rate) => window.velia.voice.synthesize(content, voiceId, rate),
      );
      if (generation !== localPlaybackGeneration.current || sessionId.current !== activeSession || stopping.current) return;
      const ticket = await queue.schedule(audioBase64);
      const completed = await ticket.done;
      if (generation !== localPlaybackGeneration.current || sessionId.current !== activeSession || stopping.current) return;
      if (completed) queue.completeResponse();
    } catch {
      if (generation === localPlaybackGeneration.current && sessionId.current === activeSession && !stopping.current) {
        setState('listening'); setMessage('任务结果已显示，本次语音播报暂不可用。');
      }
    }
  }

  async function startSession(): Promise<void> {
    if (!configured || state === 'connecting') return;
    failed.current = false;
    stopping.current = false;
    setInputTranscript(''); setOutputTranscript(''); setFreshScreenChecks(0);
    inputStartsNewTurn.current = false; outputStartsNewTurn.current = false;
    setState('connecting'); setMessage('正在建立 Gemini Live 安全连接…');
    pcmPlaybackActive.current = false;
    localPlaybackActive.current = false;
    localPlaybackGeneration.current += 1;
    const nextPlayback = new PcmPlaybackQueue((playbackActive) => updatePlaybackActivity('pcm', playbackActive));
    const nextLocalPlayback = new EncodedSpeechPlaybackQueue(
      (playbackActive) => updatePlaybackActivity('local', playbackActive),
    );
    playback.current = nextPlayback;
    localPlayback.current = nextLocalPlayback;
    void prepareLocalResultAcknowledgements();
    try {
      const started = await window.velia.live.start('Kore');
      sessionId.current = started.sessionId;
      const nextLocalCommands = new LiveLocalCommandPipeline({
        transcribe: async (utterance) => {
          const transcription = await window.velia.voice.transcribe(
            utterance.wavBase64,
            { maxLatencyMs: 6_000 },
          );
          return transcription.text;
        },
        submit: (transcript, utterance) => window.velia.live.submitLocalTranscript(
          started.sessionId,
          transcript,
          utterance.utteranceId,
          utterance.capturedAtUtc,
        ),
        interceptTranscript: (transcript) => {
          if (!isVoiceConversationStop(transcript)) return false;
          setMessage('已听到停止指令，正在结束 Live…');
          void stopSession();
          return true;
        },
        onSubmitted: (transcript, result) => {
          if (result.handled && sessionId.current === started.sessionId)
            setInputTranscript((current) => replaceCurrentTranscript(current, transcript));
        },
        onFailure: (failure) => {
          if (!stopping.current && sessionId.current === started.sessionId)
            setMessage(failure === 'transcribe'
              ? '有一句话没有识别清楚，请继续说。'
              : '有一条指令提交失败，Live 已继续聆听。');
        },
      });
      localCommands.current = nextLocalCommands;
      const nextMicrophone = new LiveMicrophoneStream();
      microphone.current = nextMicrophone;
      await nextMicrophone.start(
        (audioBase64) => sendAudio(started.sessionId, audioBase64),
        (utterance) => submitLocalUtterance(started.sessionId, utterance),
        () => interruptPlaybackForNearFieldSpeech(started.sessionId),
      );
      setState('listening'); setMessage('正在聆听，你可以直接开始说话。');
    } catch (error: unknown) {
      await microphone.current?.stop().catch(() => undefined);
      microphone.current = undefined;
      localCommands.current?.stop();
      localCommands.current = undefined;
      await nextPlayback.close().catch(() => undefined);
      playback.current = undefined;
      await nextLocalPlayback.close().catch(() => undefined);
      localPlayback.current = undefined;
      const activeSession = sessionId.current;
      sessionId.current = undefined;
      if (activeSession) await window.velia.live.stop(activeSession).catch(() => undefined);
      setState('error'); setMessage(readError(error, '无法启动 Live，请检查 Gemini API 和网络。'));
    }
  }

  async function prepareLocalResultAcknowledgements(): Promise<void> {
    if (typeof window.velia.voice?.synthesize !== 'function') return;
    const setting = loadTtsSetting();
    const synthesize = (text: string, voiceId: typeof setting.voiceId, rate: number) =>
      window.velia.voice.synthesize(text, voiceId, rate);
    await Promise.allSettled([
      getResultAcknowledgementStore().prepare(setting, true, synthesize),
      getResultAcknowledgementStore().prepare(setting, false, synthesize),
    ]);
  }

  function getResultAcknowledgementStore(): LiveResultAcknowledgementStore {
    return resultAcknowledgements.current ??= new LiveResultAcknowledgementStore(window.localStorage);
  }

  function sendAudio(activeSession: string, audioBase64: string): void {
    if (stopping.current || sessionId.current !== activeSession || pendingSends.current >= 8) return;
    pendingSends.current += 1;
    void window.velia.live.sendAudio(activeSession, audioBase64).then((result) => {
      if (!result.accepted && sessionId.current === activeSession) setMessage('网络暂时拥堵，已丢弃一小段旧音频。');
    }).catch(() => {
      if (!stopping.current && sessionId.current === activeSession) {
        setState('reconnecting'); setMessage('音频发送暂时中断，正在等待连接恢复…');
      }
    }).finally(() => { pendingSends.current = Math.max(0, pendingSends.current - 1); });
  }

  function submitLocalUtterance(activeSession: string, utterance: LiveLocalUtterance): void {
    if (stopping.current || sessionId.current !== activeSession) return;
    cancelLocalPlayback();
    if (!localCommands.current?.enqueue(utterance))
      setMessage('指令过密，已保留前面的任务，请稍慢一点再说。');
  }

  function interruptPlaybackForNearFieldSpeech(activeSession: string): void {
    if (stopping.current || sessionId.current !== activeSession) return;
    audioTurnOwnership.current.startUserTurn();
    playback.current?.clear();
    cancelLocalPlayback();
    setState('listening');
    setMessage('已停止播报，正在听你说话。');
  }

  async function stopSession(updateUi = true): Promise<void> {
    if (stopping.current) return;
    stopping.current = true;
    const activeSession = sessionId.current;
    sessionId.current = undefined;
    const activeMicrophone = microphone.current;
    microphone.current = undefined;
    const activeLocalCommands = localCommands.current;
    localCommands.current = undefined;
    const activePlayback = playback.current;
    playback.current = undefined;
    const activeLocalPlayback = localPlayback.current;
    localPlayback.current = undefined;
    localPlaybackGeneration.current += 1;
    audioTurnOwnership.current.reset();
    await activeMicrophone?.stop().catch(() => undefined);
    activeLocalCommands?.stop();
    await activePlayback?.close().catch(() => undefined);
    await activeLocalPlayback?.close().catch(() => undefined);
    pcmPlaybackActive.current = false;
    localPlaybackActive.current = false;
    if (activeSession) await window.velia.live.stop(activeSession).catch(() => undefined);
    pendingSends.current = 0;
    if (updateUi) { setState('inactive'); setMessage('Live 已结束。'); }
    stopping.current = false;
  }

  const running = ['connecting', 'listening', 'speaking', 'reconnecting'].includes(state);

  useEffect(() => {
    if (state === 'inactive' && !overlayOwned.current) return;
    overlayOwned.current = state !== 'inactive';
    const snapshot: AssistantOverlaySnapshot = {
      phase: liveOverlayPhase(state),
      status: message,
      transcript: latestLiveTurn(inputTranscript),
      answer: latestLiveTurn(outputTranscript),
      canStop: running,
      shortcutLabel: 'Live',
      wakeWordActive: false,
      metrics: {},
      updatedAtUtc: new Date().toISOString(),
    };
    void window.velia.assistant.publish(snapshot).catch(() => undefined);
  }, [inputTranscript, message, outputTranscript, running, state]);

  return (
    <div className="live-layout" hidden={!active}>
      <section className={`live-stage live-stage--${state}`}>
        <div className="section-heading">
          <div><p className="eyebrow">GEMINI LIVE</p><h3>实时双向语音</h3></div>
          <span className="live-preview-badge">Preview</span>
        </div>
        <div className="live-orb"><Waves size={46} /><span /></div>
        <strong className="live-state-title">{stateLabel(state)}</strong>
        <p className="live-state-message">{message}</p>
        <div className="live-actions">
          {!running ? (
            <button className="button button--primary" type="button" disabled={!configured} onClick={() => void startSession()}>
              <Radio size={15} />开始 Live
            </button>
          ) : (
            <button className="button button--danger" type="button" onClick={() => void stopSession()}>
              <Square size={14} />结束 Live
            </button>
          )}
        </div>
        {!configured && <p className="live-gate">Live 复用“屏幕视觉”的 Gemini API Key。<button type="button" onClick={onOpenVision}>前往配置</button></p>}
      </section>

      <section className="live-details">
        <div className="section-heading"><div><p className="eyebrow">会话信息</p><h3>实时状态与转写</h3></div><Mic size={20} /></div>
        <dl className="live-facts">
          <div><dt>模型</dt><dd>{model}</dd></div>
          <div><dt>麦克风</dt><dd>{running ? '16 kHz PCM · 40 ms 分片' : '未使用'}</dd></div>
          <div><dt>播放</dt><dd>24 kHz PCM · 支持插话立即停止</dd></div>
          <div><dt>当前画面</dt><dd>{visionStatus.active
            ? `共享中 · ${visionStatus.frames} 帧${freshScreenChecks > 0 ? ` · 按需截图 ${freshScreenChecks} 次` : ''}`
            : freshScreenChecks > 0 ? `已结束 · 按需截图 ${freshScreenChecks} 次` : running ? '正在等待画面' : '未共享'}</dd></div>
        </dl>
        <div className="live-transcript"><span>你说</span><p>{inputTranscript || '实时转写会显示在这里。'}</p></div>
        <div className="live-transcript live-transcript--assistant"><span>Velia</span><p>{outputTranscript || '回答转写会显示在这里。'}</p></div>
        <div className="live-notice"><ShieldCheck size={16} /><p>开启后，麦克风音频和当前画面会实时发送给 Google Gemini；画面每秒最多 1 帧，重复画面自动跳过且不在磁盘保存。Live 转写会由后台语义路由决定是直接聊天还是交给任务 Agent。API Key 只由隔离的 Velia Core 读取。</p></div>
        {visionStatus.error && <p className="live-preview-copy">画面共享暂时不可用：{visionStatus.error}</p>}
        <button className="resource-link-button" type="button" onClick={() => void window.velia.system.openExternalResource('geminiApiKeys')}>
          <ExternalLink size={12} />获取 Gemini API
        </button>
        <p className="live-preview-copy">该模型目前为 Preview，Google 可能调整接口、可用地区或模型名称。</p>
      </section>
    </div>
  );
}

function stateLabel(state: LiveState): string {
  return ({ inactive: '准备就绪', connecting: '正在连接', listening: '正在聆听', speaking: 'Velia 正在说话', reconnecting: '正在恢复连接', error: '需要处理' })[state];
}

function appendTranscript(current: string, next: string, startsNewTurn = false): string {
  const normalized = next.trim();
  if (!normalized) return current;
  const combined = `${current}${startsNewTurn && current ? '\n' : ''}${normalized}`;
  return combined.length <= 4_000 ? combined : combined.slice(-4_000);
}

export function replaceCurrentTranscript(current: string, corrected: string): string {
  const normalized = corrected.trim();
  if (!normalized) return current;
  const boundary = current.lastIndexOf('\n');
  const replaced = boundary >= 0 ? `${current.slice(0, boundary + 1)}${normalized}` : normalized;
  return replaced.length <= 4_000 ? replaced : replaced.slice(-4_000);
}

function readError(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message.replace(/^[a-z0-9_]+:\s*/iu, '') : fallback;
}
