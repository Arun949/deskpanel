import { describe, expect, it } from 'vitest';
import { parseVia } from './transport';

describe('parseVia', () => {
  it('reads the route the Android shell reports', () => {
    expect(parseVia('{"via":"usb"}')).toBe('usb');
    expect(parseVia('{"via":"wifi"}')).toBe('wifi');
  });

  it('treats anything else as unknown', () => {
    expect(parseVia('{}')).toBeNull();
    expect(parseVia('')).toBeNull(); // the dev EventSource sends no data
    expect(parseVia('{"via":"<b>x</b>"}')).toBeNull();
    expect(parseVia('not json')).toBeNull();
  });
});
