// Screen care for a panel that stays on all day (PhoneBox Module 10: pixel shift and night dimming).

/** Night hours (local time, 23:00–07:00): the panel dims to protect the OLED and not light up the room. */
export const isNight = (d: Date): boolean => d.getHours() >= 23 || d.getHours() < 7;

/**
 * Burn-in protection: the whole panel drifts by a few pixels, one step every 2 minutes, around a small square.
 * Deterministic from the clock, so it needs no state and is easy to test.
 */
export function pixelShift(d: Date): { x: number; y: number } {
  const steps: Array<[number, number]> = [[0, 0], [2, 0], [2, 2], [0, 2], [-2, 2], [-2, 0], [-2, -2], [0, -2], [2, -2]];
  const i = Math.floor(d.getTime() / 120_000) % steps.length;
  const [x, y] = steps[i]!;
  return { x, y };
}

export function applyScreenCare(root: HTMLElement, now: Date): void {
  const { x, y } = pixelShift(now);
  root.style.setProperty('--shift-x', `${x}px`);
  root.style.setProperty('--shift-y', `${y}px`);
  root.classList.toggle('night', isNight(now));
}
