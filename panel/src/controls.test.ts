import { control } from './transport';

type Call = { url: string; init: RequestInit };

function mockFetch(responses: Array<Response | Error>): Call[] {
  const calls: Call[] = [];
  vi.stubGlobal('fetch', async (url: string, init: RequestInit) => {
    calls.push({ url, init });
    const next = responses.shift();
    if (next instanceof Error) throw next;
    return next ?? new Response(null, { status: 500 });
  });
  return calls;
}

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

afterEach(() => vi.unstubAllGlobals());

describe('control()', () => {
  it('POSTs with the panel header and a fresh Idempotency-Key per tap', async () => {
    const calls = mockFetch([json(200, { ok: true }), json(200, { ok: true })]);
    expect(await control('/media/next')).toEqual({ ok: true, message: null });
    await control('/media/next');
    expect(calls[0]!.url).toBe('/agent/api/v1/media/next');
    expect(calls[0]!.init.method).toBe('POST');
    const h0 = calls[0]!.init.headers as Record<string, string>;
    const h1 = calls[1]!.init.headers as Record<string, string>;
    expect(h0['X-DeskPanel-Panel']).toBe('1');
    expect(h0['Idempotency-Key']).toMatch(/^[A-Za-z0-9-]{8,64}$/);
    expect(h0['Idempotency-Key']).not.toBe(h1['Idempotency-Key']);
  });

  it('explains refusals in plain words', async () => {
    mockFetch([json(403, { error: 'not-allowed' }), json(429, { error: 'slow-down' }), json(502, { ok: false, error: 'no-player-running' }), json(418, {})]);
    expect((await control('/media/next')).message).toMatch(/turn it on in DeskPanel settings/);
    expect((await control('/media/next')).message).toMatch(/Too many taps/);
    expect((await control('/media/next')).message).toMatch(/Spotify or Music/);
    expect((await control('/media/next')).message).toMatch(/http-418/);
  });

  it('does not retry after a network error (the computer may already have acted)', async () => {
    const calls = mockFetch([new TypeError('network down')]);
    const r = await control('/actions/open-fake');
    expect(r).toEqual({ ok: false, message: 'Your computer isn’t connected' });
    expect(calls).toHaveLength(1);
  });
});
