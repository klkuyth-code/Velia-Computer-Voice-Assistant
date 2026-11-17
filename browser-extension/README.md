# Velia Browser Bridge

Manifest V3 extension for bounded DOM snapshots and deterministic browser
actions. It connects only to the loopback Velia Desktop gateway and requires a
one-time pairing token.

Model-requested actions are still constrained by the fixed bridge allowlist.
Requests support cooperative cancellation between queued batch actions, so a
stopped Velia task does not continue with later actions.

The extension does not expose cookies, passwords, downloads, arbitrary script
execution, or a generic browser RPC surface.
