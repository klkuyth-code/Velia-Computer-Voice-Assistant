# @velia/protocol

Versioned transport contracts shared by Velia Desktop and Velia Core.

All JSON boundaries reject unknown properties. A consumer must opt into a newer
schema rather than silently accepting fields it does not understand.

`velia-core-event/v1` is the shared streaming envelope for conversation state,
content, tool deltas, mission snapshots, and terminal events. Mission snapshots
carry a revision and one of the bounded accepted/running/awaiting/correcting/
terminal states. RPC responses and Core events use different schema identifiers
even though they share one ordered transport.

The vision contract is deliberately one-shot: it carries one bounded JPEG, a
user question, capture metadata, and one of the fixed Gemini model identifiers.
It contains no streaming-screen or visual-action method.

The document contract supports only open, analyze, and close. It has no write,
overwrite, rename, delete, execute, or arbitrary-path RPC.

Physical voice edges use `velia-input-event/v1`. Push-to-talk is limited to F8
or Ctrl+Shift+Space, while ordinary long conversation uses fixed Ctrl+Alt+L. The
schema binds each shortcut kind to its allowed key and cannot carry text,
commands, or executable data.
