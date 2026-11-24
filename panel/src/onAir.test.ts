import { describe, expect, it } from 'vitest';
import { onAirView } from './widgets/onAir';

describe('On Air light', () => {
  it('lights up for whichever device is in use', () => {
    expect(onAirView({ camera: true, mic: false }, 'any')).toMatchObject({ on: true, sub: 'camera in use' });
    expect(onAirView({ camera: true, mic: true }, 'any')).toMatchObject({ on: true, sub: 'camera + mic in use' });
    expect(onAirView({ camera: false, mic: true }, 'any')).toMatchObject({ on: true, title: 'ON AIR', sub: 'mic in use' });
  });

  it('can watch only the camera or only the mic', () => {
    expect(onAirView({ camera: false, mic: true }, 'camera').on).toBe(false);
    expect(onAirView({ camera: false, mic: true }, 'mic').on).toBe(true);
    expect(onAirView({ camera: true, mic: true }, 'mic').sub).toBe('mic in use');
  });

  it('is off when both are free', () => expect(onAirView({ camera: false, mic: false }, 'any')).toMatchObject({ on: false, title: 'Off air' }));

  it('never shows unknown as off', () => {
    expect(onAirView(null, 'any').on).toBeNull();
    expect(onAirView({ camera: null, mic: null }, 'any').sub).toBe('not available on this computer');
    expect(onAirView({ camera: null, mic: false }, 'camera').on).toBeNull();
  });
});
