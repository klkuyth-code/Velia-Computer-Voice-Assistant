export const desktopVisualActions = ['click', 'type', 'scroll', 'press'] as const;
export type DesktopVisualAction = (typeof desktopVisualActions)[number];

export const desktopVisualKeys = [
  'enter', 'escape', 'tab', 'space', 'backspace',
  'arrowUp', 'arrowDown', 'arrowLeft', 'arrowRight',
] as const;
export type DesktopVisualKey = (typeof desktopVisualKeys)[number];

export interface DesktopVisualGetForegroundResult {
  readonly targetToken: string;
  readonly processId: number;
  readonly processName: string;
  readonly windowTitle: string;
  readonly screenX: number;
  readonly screenY: number;
  readonly capturedAtUtc: string;
}

export interface DesktopVisualGetForegroundParams extends Record<string, unknown> {
  readonly operationId: string;
}

export interface DesktopVisualExecuteActionParams extends Record<string, unknown> {
  readonly operationId: string;
  readonly targetToken: string;
  readonly action: DesktopVisualAction;
  readonly target: string;
  readonly displayId: string;
  readonly screenX: number;
  readonly screenY: number;
  readonly text?: string;
  readonly key?: DesktopVisualKey;
  readonly scrollDelta?: number;
  readonly expectedProcessId: number;
  readonly expectedProcessName: string;
  readonly expectedWindowTitle: string;
  /** Resolve and activate an accessible Windows control by name. Never falls back to coordinates. */
  readonly structuredTargetOnly?: boolean;
}

export interface DesktopVisualExecuteActionResult {
  readonly operationId: string;
  readonly action: DesktopVisualAction;
  readonly executed: true;
  readonly screenX: number;
  readonly screenY: number;
  readonly processId: number;
  readonly processName: string;
  readonly windowTitle: string;
  readonly verification:
    | 'inputAccepted'
    | 'menuExpanded'
    | 'menuInvoked'
    | 'uiaInvoked'
    | 'uiaSelected'
    | 'uiaExpanded'
    | 'uiaToggled'
    | 'uiaFocused'
    | 'uiaClicked'
    | 'uiaTextAppended';
}
