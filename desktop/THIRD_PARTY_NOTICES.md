# Third-party notices

Velia uses third-party components including Electron, Chromium, Node.js, React,
Vite, TypeScript, Lucide, Ajv, ws, Mammoth (BSD-2-Clause), pdf-parse
(Apache-2.0), node-edge-tts (MIT), whisper.cpp (MIT), and OpenAI Whisper model
weights (MIT), and their transitive dependencies. Their copyright
notices and license texts must be included in release artifacts.

The Windows wake-word runtime is pinned to the official ggml-org whisper.cpp
release and the official converted multilingual base model. The build verifies
their published hashes before packaging. Exact license texts are retained under
`third-party/` and copied beside the packaged runtime.

This file is a construction-stage index, not the final generated notice bundle.
The release pipeline must collect exact package versions and license files from the
locked dependency graph before an installer is distributed.

## Project AIRI

Velia's bounded concurrent speech scheduling, ordered playback policy, native-tool-
first desktop routing, and retryable pre-action observation policy were informed by
and adapted from Project AIRI (`moeru-ai/airi`). No AIRI character, game, server,
account, billing, or macOS executor code is included.

MIT License

Copyright (c) 2024-PRESENT Neko Ayaka

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
