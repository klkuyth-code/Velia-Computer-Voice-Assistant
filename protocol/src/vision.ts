export const visionModels = ['gemini-3.5-flash-lite', 'gemini-3.6-flash'] as const;

export type VisionModel = (typeof visionModels)[number];

export interface VisionAnalyzeParams extends Record<string, unknown> {
  readonly imageBase64: string;
  readonly mimeType: 'image/jpeg';
  readonly prompt: string;
  readonly model: VisionModel;
  readonly displayId: string;
  readonly capturedAtUtc: string;
}

export interface VisionAnalyzeResult {
  readonly text: string;
  readonly model: VisionModel;
  readonly capturedAtUtc: string;
  readonly providerLatencyMs: number;
  readonly inputBytes: number;
}

export interface VisionTestResult {
  readonly state: 'ready' | 'error';
  readonly testedAtUtc: string;
  readonly message: string;
  readonly errorCode?: string;
  readonly retryable?: boolean;
}

export interface VisionDisplay {
  readonly id: string;
  readonly name: string;
  readonly width: number;
  readonly height: number;
  readonly scaleFactor: number;
  readonly primary: boolean;
}

export interface VisionCapture {
  readonly imageBase64: string;
  readonly mimeType: 'image/jpeg';
  readonly displayId: string;
  readonly capturedAtUtc: string;
  readonly width: number;
  readonly height: number;
  readonly inputBytes: number;
}
