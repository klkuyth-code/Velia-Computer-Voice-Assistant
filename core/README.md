# Velia Core

Trusted Windows process for orchestration, provider access, tool policy, and
secure configuration. The Electron renderer cannot call Windows or provider
APIs directly.

The conversation service owns both the bounded model/tool loop and the active
mission lifecycle. It preserves one mission across explicit additions,
corrections, progress queries, waits for user input, and stops. Correcting a
running mission first cancels its provider and browser work before the revised
operation starts. Ambiguous speech cannot silently replace running work.

Browser mutations pass the central consent policy, complete tool calls are
validated twice across the Core/Desktop boundary, and only correlated bounded
results continue the same model operation. A mission that asks for user input
suppresses every other tool in that model turn.

Development transport is newline-delimited JSON over inherited standard input and
output. Production transport will use a current-user restricted Windows named pipe
with the same message envelopes.

Screen images are accepted only through the allowlisted one-shot vision RPC.
Core validates the configured Gemini credential, JPEG signature, byte limit,
prompt limit, and model allowlist before sending the image. Screenshot
content is treated as untrusted data and cannot authorize tool execution.

Document analysis accepts only bounded extracted text through an allowlisted
RPC. Sessions are memory-only and expire after two hours. Document content is
untrusted data and cannot authorize commands, links, file mutations, or tools.

On Windows, Core samples the selected allowlisted push-to-talk shortcut (F8 or
Ctrl+Shift+Space). Its low-level keyboard hook captures the fixed Ctrl+Alt+L ordinary
long-conversation toggle without requiring exclusive system-wide shortcut registration.
For the alternative shortcut, Core suppresses only the final Space key so it
cannot enter the foreground document; legacy Ctrl+Alt+Space settings migrate
locally. It emits only bounded press and release events. Startup-held keys and auto-repeat
do not create phantom turns. The push-to-talk selection is stored locally in
`%LOCALAPPDATA%\Velia\settings.json`.

Selected-file mutations remain inside Core. A model must first capture a fresh
Explorer selection and then reference its short-lived context id. Copy and move
require exactly one selected destination folder plus one or more selected regular
files; rename requires exactly one selected regular file. Core never returns full
paths, never overwrites, exposes no delete method or shell, rejects reparse points
and protected application/security folders, verifies the resulting disk state,
and consumes the context after one mutation to prevent replay.

Explicitly named desktop moves also have a deterministic local path. Commands
such as moving a named desktop item into a named desktop folder resolve unique
files or folders recursively below the Desktop, normalize common spoken type
suffixes and whitespace, reject ambiguous names and reparse points, never
overwrite, and reuse the verified move/rollback implementation for files while
using the same checks for folder moves. Moving a folder into itself or one of
its descendants is rejected. No arbitrary path or shell command is exposed.

An untitled foreground Notepad window can be edited through the verified UI
Automation text surface. These edits are reported as unsaved and never press
Ctrl+S or claim disk persistence; save/close still require a named document.

Gemini Live monitors each locally recognized conversational turn for real
provider activity. A transport that reconnects but stops accepting microphone
turns is replaced with a fresh native-audio session and the pending utterance is
replayed once. If the fresh session also stays silent, Core emits a real failed
state so Desktop stops the microphone instead of showing false listening.

Allowlisted Windows controls also execute inside Core. The model can choose only
fixed application ids and fixed actions for open, normal close, minimize, restore
to foreground, or maximize, exact verified master-volume changes, mute state, or
one timer of at most 24 hours. Installed applications are resolved through trusted
fixed locations, running process images, per-user or machine App Paths, trusted
Start Menu shortcuts, and uninstall registration. Paths are never exposed to the
model and accept no arguments. Every window-state action is checked against the
real top-level window. Closing posts a normal window-close request and never kills
a process; save prompts therefore remain under user control. Timer expiry plays a
bounded local alert and emits one notification event for the Electron host.
