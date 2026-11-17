# Velia Desktop

Electron/React presentation layer and low-privilege window host. The renderer has
no Node.js access and cannot call arbitrary Core methods. System capabilities,
provider secrets, and tools belong to Velia Core.

## Security invariants

- sandboxed renderer with context isolation;
- strict navigation and popup denial;
- fixed IPC allowlist in the preload bridge;
- no generic `invoke(method, payload)` bridge;
- no provider key returned to the renderer;
- no shell or filesystem API exposed to React.

## Current functional slice

The model page can configure the built-in DeepSeek V4 Flash profile, run a
bounded connection test, stream responses, and show browser-tool progress. The
trusted main process validates complete tool arguments, applies per-task tab
budgeting, returns bounded results to Core, and propagates cancellation to the
extension. API keys cross the preload boundary only when the user saves them and
are never returned to React. Release packaging excludes JavaScript source maps
and Core PDB files.

Verified Windows application, volume, and timer actions execute entirely in Core.
Desktop receives no executable path or command. It handles only the bounded timer
expiry event by expanding the Velia overlay, playing the system alert, and showing
a native notification when Windows supports it; that event is not injected into
conversation state.

Ordinary voice supports both hold-to-talk and a Ctrl+Alt+L long-conversation mode.
Long conversation uses local RMS/VAD only to close each turn after speech,
automatically exits after 12 seconds of initial silence or 30 minutes total,
and remains mutually exclusive with Live and wake-word audio ownership. During
a running task, hold-to-talk can capture an explicit correction or stop command.

The optional “嘿 Velia” mode is off by default. When the user enables its single
visible switch, a local adaptive energy gate and the bundled whisper.cpp base
model detect only the wake phrase. No wake audio is uploaded. Explicit recording
and Live preempt passive wake ownership, and playback pauses it. Locking or
suspending Windows releases continuous microphone modes; unlock/resume restores
wake monitoring only when the saved switch is still enabled and no foreground
audio mode owns the microphone.

After a confirmed wake, Desktop plays “我在” with the selected ordinary Edge TTS
voice. The short acknowledgement is generated ahead of time and cached locally;
recording starts only after it finishes so Velia does not transcribe itself. A
wake conversation automatically listens again after every answer and accepts
follow-up speech without another wake phrase. Nine seconds of silence, a fixed
exit phrase, or a ten-minute session limit returns the app to passive wake
monitoring. While an answer is playing, a local strict VAD with a short preroll
can stop playback and capture an interruption. A bounded transcript comparison
rejects likely loudspeaker echo before the interruption is submitted to Core.
This keeps the UI at one wake switch and adds no separate conversation or
interruption control.

The complete wake acceptance run passed on Windows: wake latency was about two
seconds, the selected voice said “我在”, follow-up turns continued automatically,
spoken interruption stopped playback and produced the replacement answer, exit
phrases restored passive monitoring, and the silence timeout closed the session.

The conversation-history page is connected to fixed Core RPCs. Core stores only
completed user/assistant text turns in a DPAPI-protected vault for the current
Windows account. Renderer code never receives provider secrets, audio, images,
document bodies, or tool results. Users can view, continue, delete, or clear
history, disable persistence, and choose a 7/30/90/365-day retention period.
Disabling persistence clears the vault and starts a fresh in-memory conversation;
opening or deleting history is rejected while a model operation is active.

The Windows package prepares whisper.cpp v1.9.2 and the multilingual base model
from official sources, verifies pinned hashes, bundles only the required CLI and
runtime DLLs, and retains the whisper.cpp and OpenAI Whisper MIT license texts.
Package verification fails if the model, runtime, manifest, or notices are absent.

The screen-vision page enumerates local displays and captures exactly one JPEG
only after a user click. The main window is hidden for the capture, the image is
bounded to a 2048-pixel longest edge, and no screenshot is written to disk.
Continuous Live vision is available only while the user explicitly enables Live;
coordinate-driven desktop automation remains outside this release.

The document page reads explicitly selected TXT, Markdown, CSV, PDF, and DOCX
files without modifying them. Extraction happens in the Electron main process;
Core holds only bounded text in a two-hour in-memory session. Original files are
never uploaded, and the extracted text is sent to the configured DeepSeek model
only after the user clicks Analyze. Write, overwrite, delete, and OCR operations
are not exposed in this release.
