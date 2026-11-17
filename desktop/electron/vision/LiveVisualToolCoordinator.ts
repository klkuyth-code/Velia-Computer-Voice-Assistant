import type {
  LiveEvent,
  LiveSubmitToolResultResult,
  VisionAnalyzeResult,
} from '@velia/protocol';
import type { CoreProcessClient } from '../core/CoreProcessClient.js';
import type { ScreenCaptureService } from './ScreenCaptureService.js';

const visionModel = 'gemini-3.5-flash-lite';
type ToolExecutionResult = { readonly ok: boolean; readonly value: string };

export class LiveVisualToolCoordinator {
  readonly #active = new Set<string>();
  readonly #turnResults = new Map<string, Promise<ToolExecutionResult>>();

  constructor(
    private readonly core: Pick<CoreProcessClient, 'request'>,
    private readonly capture: Pick<ScreenCaptureService, 'captureLiveFrame'>,
  ) {}

  async handle(event: LiveEvent): Promise<void> {
    if (['setup', 'turnComplete', 'interrupted', 'closed', 'failed'].includes(event.type)) {
      this.#turnResults.delete(event.sessionId);
      return;
    }
    const call = event.type === 'toolCall' ? event.toolCall : undefined;
    if (!call || call.name !== 'capture_current_screen' || typeof call.question !== 'string' || this.#active.has(call.id)) return;
    this.#active.add(call.id);
    try {
      let result = this.#turnResults.get(event.sessionId);
      if (!result) {
        result = this.execute(call.question);
        this.#turnResults.set(event.sessionId, result);
      }
      const completed = await result;
      await this.submit(event.sessionId, call.id, completed.ok, completed.value);
    } finally {
      this.#active.delete(call.id);
    }
  }

  private async execute(question: string): Promise<ToolExecutionResult> {
    try {
      const frame = await this.capture.captureLiveFrame();
      const analysis = await this.core.request<VisionAnalyzeResult>('vision.analyze', {
        imageBase64: frame.imageBase64,
        mimeType: frame.mimeType,
        displayId: frame.displayId,
        capturedAtUtc: frame.capturedAtUtc,
        model: visionModel,
        prompt: `只根据这张刚刚截取的当前窗口画面回答，不要参考旧画面，也不要猜测。无法确认时明确说明。\n用户问题：${question}`,
      }, 75_000);
      return { ok: true, value: analysis.text.trim() };
    } catch (error: unknown) {
      return { ok: false, value: safeError(error) };
    }
  }

  private async submit(sessionId: string, callId: string, ok: boolean, value: string): Promise<void> {
    const normalized = value.trim().slice(0, ok ? 32_768 : 300) || '当前画面分析失败，请稍后重试。';
    const params = ok
      ? { sessionId, callId, name: 'capture_current_screen', ok: true, result: normalized }
      : { sessionId, callId, name: 'capture_current_screen', ok: false, error: normalized };
    await this.core.request<LiveSubmitToolResultResult>('live.submitToolResult', params, 10_000).catch(() => undefined);
  }
}

function safeError(error: unknown): string {
  if (!(error instanceof Error) || !error.message) return '当前画面分析失败，请稍后重试。';
  return error.message.replace(/^[a-z0-9_]+:\s*/iu, '').slice(0, 300);
}
