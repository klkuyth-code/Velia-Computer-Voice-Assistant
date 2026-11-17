import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import { describe, expect, it } from 'vitest';

function loadSchema(name: string): object {
  const url = new URL(`../schemas/${name}`, import.meta.url);
  return JSON.parse(readFileSync(fileURLToPath(url), 'utf8')) as object;
}

function createValidator(schemaName: string) {
  const ajv = new Ajv2020({ allErrors: true, strict: true });
  addFormats(ajv);
  return ajv.compile(loadSchema(schemaName));
}

describe('protocol schemas', () => {
  it('accepts bounded Live audio events and rejects unknown fields', () => {
    const validate = createValidator('live-event.v1.schema.json');
    const event = {
      schema: 'velia-live-event/v1',
      sessionId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 1,
      atUtc: '2026-08-14T01:00:00Z',
      type: 'audio',
      audioBase64: 'AAECAw==',
      mimeType: 'audio/pcm;rate=24000',
    };
    expect(validate(event), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...event, apiKey: 'must-not-cross-the-boundary' })).toBe(false);
    expect(validate({ ...event, mimeType: 'audio/mpeg' })).toBe(false);
  });

  it('accepts only the bounded Live screen-analysis tool event', () => {
    const validate = createValidator('live-event.v1.schema.json');
    const event = {
      schema: 'velia-live-event/v1',
      sessionId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 2,
      atUtc: '2026-08-14T01:00:00Z',
      type: 'toolCall',
      toolCall: { id: 'call-1', name: 'capture_current_screen', question: '当前是什么软件？' },
    };
    expect(validate(event), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...event, toolCall: { ...event.toolCall, name: 'run_shell' } })).toBe(false);
    expect(validate({ ...event, toolCall: { ...event.toolCall, command: 'anything' } })).toBe(false);
  });
  it('accepts a valid health request and rejects extra properties', () => {
    const validate = createValidator('rpc-request.v1.schema.json');
    const request = {
      schema: 'velia-rpc-request/v1',
      id: '1196ea21-1692-46a8-b87b-92c608429ead',
      method: 'system.health',
      params: {},
    };

    expect(validate(request), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...request, command: 'powershell.exe' })).toBe(false);
  });

  it('validates assistant state events without accepting arbitrary details', () => {
    const validate = createValidator('assistant-event.v1.schema.json');
    const event = {
      schema: 'velia-assistant-event/v1',
      eventId: 'c4dbe41a-c39e-4dba-8565-ea679d8c361a',
      sequence: 7,
      atUtc: '2026-08-10T09:00:00Z',
      state: 'thinking',
      source: 'task',
    };

    expect(validate(event), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...event, command: 'delete everything' })).toBe(false);
  });

  it('validates mission progress and rejects an unknown lifecycle state', () => {
    const validate = createValidator('mission-snapshot.v1.schema.json');
    const snapshot = {
      schema: 'velia-mission-snapshot/v1',
      missionId: 'bf202102-fd41-4a58-a158-7d663efbc3a4',
      revision: 2,
      origin: 'voice',
      state: 'correcting',
      currentRequest: '整理资料，但不要移动原文件',
      requirementCount: 2,
      createdAtUtc: '2026-08-10T09:00:00Z',
      updatedAtUtc: '2026-08-10T09:00:10Z',
    };

    expect(validate(snapshot), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...snapshot, state: 'secretBackgroundMode' })).toBe(false);
  });

  it('requires explicit risk declarations for a consent request', () => {
    const validate = createValidator('tool-consent-request.v1.schema.json');
    const request = {
      schema: 'velia-tool-consent-request/v1',
      confirmationId: 'c4dbe41a-c39e-4dba-8565-ea679d8c361a',
      toolName: 'mail.send',
      risks: ['externalCommunication'],
      summary: '向指定联系人发送一封邮件',
      target: 'example@example.com',
      expiresAtUtc: '2026-08-10T09:02:00Z',
    };

    expect(validate(request), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...request, risks: [] })).toBe(false);
  });

  it('validates bounded Core stream events and rejects executable fields', () => {
    const validate = createValidator('core-event.v1.schema.json');
    const event = {
      schema: 'velia-core-event/v1',
      operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 3,
      atUtc: '2026-08-10T09:00:00Z',
      type: 'content',
      origin: 'text',
      delta: '你好',
    };

    expect(validate(event), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...event, shell: 'powershell.exe' })).toBe(false);
    expect(validate({ ...event, delta: '' })).toBe(false);
  });

  it('accepts only a bounded timer notification from Core', () => {
    const validate = createValidator('core-event.v1.schema.json');
    const event = {
      schema: 'velia-core-event/v1',
      operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 1,
      atUtc: '2026-08-16T06:00:00Z',
      type: 'notification',
      origin: 'system',
      notification: { kind: 'timerElapsed', title: 'Velia 计时器', message: '计时结束。' },
    };
    expect(validate(event), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...event, notification: { ...event.notification, command: 'anything' } })).toBe(false);
    expect(validate({ ...event, notification: { ...event.notification, kind: 'runProgram' } })).toBe(false);
  });

  it('accepts the allowlisted tool-result continuation method', () => {
    const validate = createValidator('rpc-request.v1.schema.json');
    expect(validate({
      schema: 'velia-rpc-request/v1',
      id: '1196ea21-1692-46a8-b87b-92c608429ead',
      method: 'conversation.submitToolResult',
      params: {
        operationId: '88dbc31f-e8e4-4500-8af5-cad59b87acdf',
        toolCallId: 'call_1',
        ok: true,
        result: { tabs: [] },
      },
    }), JSON.stringify(validate.errors)).toBe(true);
  });

  it('accepts only the allowlisted vision RPC methods', () => {
    const validate = createValidator('rpc-request.v1.schema.json');
    const common = {
      schema: 'velia-rpc-request/v1',
      id: '1196ea21-1692-46a8-b87b-92c608429ead',
      params: {},
    };
    expect(validate({ ...common, method: 'vision.test' }), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...common, method: 'vision.analyze' }), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...common, method: 'vision.streamDesktop' })).toBe(false);
  });

  it('allows only the fixed Firecrawl connection-test method', () => {
    const validate = createValidator('rpc-request.v1.schema.json');
    const common = {
      schema: 'velia-rpc-request/v1', id: '1196ea21-1692-46a8-b87b-92c608429ead', params: {},
    };
    expect(validate({ ...common, method: 'web.test' }), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...common, method: 'web.requestArbitraryEndpoint' })).toBe(false);
    expect(validate({ ...common, method: 'web.scrape' })).toBe(false);
    for (const params of [
      { endpoint: 'https://example.invalid' },
      { url: 'https://example.invalid' },
      { headers: { authorization: 'secret' } },
      { apiKey: 'must-not-cross-the-boundary' },
    ]) {
      expect(validate({ ...common, method: 'web.test', params })).toBe(false);
    }
    expect(validate({ ...common, method: 'web.test', endpoint: 'https://example.invalid' })).toBe(false);
  });

  it('accepts only the bounded document RPC methods', () => {
    const validate = createValidator('rpc-request.v1.schema.json');
    const common = {
      schema: 'velia-rpc-request/v1', id: '1196ea21-1692-46a8-b87b-92c608429ead', params: {},
    };
    expect(validate({ ...common, method: 'document.open' })).toBe(true);
    expect(validate({ ...common, method: 'document.analyze' })).toBe(true);
    expect(validate({ ...common, method: 'document.close' })).toBe(true);
    expect(validate({ ...common, method: 'document.write' })).toBe(false);
  });

  it('accepts only the allowlisted desktop-context mutation methods', () => {
    const validate = createValidator('rpc-request.v1.schema.json');
    const common = {
      schema: 'velia-rpc-request/v1', id: '1196ea21-1692-46a8-b87b-92c608429ead', params: {},
    };
    expect(validate({ ...common, method: 'desktopContext.capture' })).toBe(true);
    expect(validate({ ...common, method: 'desktopContext.replaceSelection' })).toBe(true);
    expect(validate({ ...common, method: 'desktopContext.operateSelectedFiles' })).toBe(true);
    expect(validate({ ...common, method: 'desktopContext.deleteSelectedFiles' })).toBe(false);
  });

  it('requires a complete bounded tool request from Core', () => {
    const validate = createValidator('core-event.v1.schema.json');
    const common = {
      schema: 'velia-core-event/v1',
      operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 2,
      atUtc: '2026-08-12T04:00:00Z',
      type: 'tool',
      origin: 'text',
    };
    expect(validate({ ...common, toolCall: {
      index: 0,
      id: 'call_1',
      name: 'browser',
      argumentsFragment: '{"method":"getPage","params":{}}',
    }}), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...common, toolCall: { index: 0 } })).toBe(false);
  });

  it('validates mission lifecycle events emitted by Core', () => {
    const validate = createValidator('core-event.v1.schema.json');
    const event = {
      schema: 'velia-core-event/v1',
      operationId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 2,
      atUtc: '2026-08-15T02:00:00Z',
      type: 'mission',
      origin: 'voice',
      mission: {
        schema: 'velia-mission-snapshot/v1',
        missionId: '88dbc31f-e8e4-4500-8af5-cad59b87acdf',
        revision: 2,
        origin: 'voice',
        state: 'awaitingInput',
        currentRequest: '创建文档',
        requirementCount: 2,
        createdAtUtc: '2026-08-15T01:59:00Z',
        updatedAtUtc: '2026-08-15T02:00:00Z',
        message: '请告诉我文件名',
      },
    };
    expect(validate(event), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...event, mission: { ...event.mission, state: 'executingShell' } })).toBe(false);
  });

  it('accepts only allowlisted physical push-to-talk edges', () => {
    const validate = createValidator('input-event.v1.schema.json');
    const event = {
      schema: 'velia-input-event/v1',
      eventId: '1196ea21-1692-46a8-b87b-92c608429ead',
      sequence: 1,
      atUtc: '2026-08-13T05:00:00Z',
      shortcut: 'voicePushToTalk',
      key: 'F8',
      edge: 'pressed',
    };
    expect(validate(event), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...event, key: 'Ctrl+Shift+Space' }), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...event, key: 'Ctrl+Alt+Space' })).toBe(false);
    expect(validate({ ...event, shortcut: 'voiceLongConversation', key: 'Ctrl+Alt+L' }), JSON.stringify(validate.errors)).toBe(true);
    expect(validate({ ...event, key: 'Ctrl+Alt+L' })).toBe(false);
    expect(validate({ ...event, shortcut: 'voiceLongConversation', key: 'F8' })).toBe(false);
    expect(validate({ ...event, edge: 'repeat' })).toBe(false);
    expect(validate({ ...event, key: 'F9' })).toBe(false);
    expect(validate({ ...event, command: 'anything' })).toBe(false);
  });

  it('allows only the bounded voice shortcut configuration methods', () => {
    const validate = createValidator('rpc-request.v1.schema.json');
    const common = {
      schema: 'velia-rpc-request/v1', id: '1196ea21-1692-46a8-b87b-92c608429ead', params: {},
    };
    expect(validate({ ...common, method: 'voice.getShortcut' })).toBe(true);
    expect(validate({ ...common, method: 'voice.setShortcut' })).toBe(true);
    expect(validate({ ...common, method: 'voice.registerArbitraryShortcut' })).toBe(false);
  });

  it('allows only the fixed conversation-history RPC surface', () => {
    const validate = createValidator('rpc-request.v1.schema.json');
    const common = {
      schema: 'velia-rpc-request/v1',
      id: '1196ea21-1692-46a8-b87b-92c608429ead',
      params: {},
    };
    for (const method of [
      'conversation.history.getSettings',
      'conversation.history.updateSettings',
      'conversation.history.list',
      'conversation.history.get',
      'conversation.history.open',
      'conversation.history.startNew',
      'conversation.history.delete',
      'conversation.history.clear',
    ]) {
      expect(validate({ ...common, method }), JSON.stringify(validate.errors)).toBe(true);
    }
    expect(validate({ ...common, method: 'conversation.history.exportPlaintext' })).toBe(false);
  });
});
