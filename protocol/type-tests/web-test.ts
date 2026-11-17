import type { RpcMethod, WebTestResult } from '../src/index.js';

const webTestMethod: RpcMethod = 'web.test';

const readyResult: WebTestResult = {
  state: 'ready',
  testedAtUtc: '2026-08-16T09:30:00Z',
  message: 'Firecrawl 已就绪。',
  remainingCredits: 500,
};

const errorResult: WebTestResult = {
  state: 'error',
  testedAtUtc: '2026-08-16T09:30:00Z',
  message: 'Firecrawl 暂不可用。',
  errorCode: 'rate_limited',
  retryable: true,
};

// @ts-expect-error Only the fixed connection-test RPC crosses the desktop/Core boundary.
const arbitraryWebMethod: RpcMethod = 'web.requestArbitraryEndpoint';

// @ts-expect-error A connection test has no intermediate "testing" result state.
const invalidState: WebTestResult = { state: 'testing', testedAtUtc: '2026-08-16T09:30:00Z', message: '...' };

// @ts-expect-error Web test results cannot expose or carry arbitrary endpoints.
const leakedEndpoint: WebTestResult = { state: 'ready', testedAtUtc: '2026-08-16T09:30:00Z', message: '...', endpoint: 'https://example.invalid' };

void webTestMethod;
void readyResult;
void errorResult;
void arbitraryWebMethod;
void invalidState;
void leakedEndpoint;
