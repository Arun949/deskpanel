import { h } from './dom';
import { widgetDef, type DataKind, type LiveView, type WidgetConfig, type WidgetDef, type WidgetInstance } from './registry';

export const COLS = 12;
export const ROWS = 6;

interface Mounted {
  config: WidgetConfig;
  def: WidgetDef;
  inst: WidgetInstance;
  dirty: boolean;
}

/** A single page of widgets on the 12 × 6 grid. Data reaches only widgets that asked for it. */
export class Page {
  private mounted: Mounted[] = [];
  visible = true;

  constructor(readonly root: HTMLElement, private readonly live: LiveView) {}

  mount(widgets: readonly WidgetConfig[]): void {
    this.unmount();
    const grid = h('div', 'grid');
    for (const config of widgets) {
      const def = widgetDef(config.type);
      if (!def) continue; // unknown types are dropped (the agent validates layouts too)
      const box = h('div', `card w-${config.type}`);
      const p = placement(config);
      box.style.gridArea = `${p.y + 1} / ${p.x + 1} / span ${p.h} / span ${p.w}`;
      grid.append(box);
      let inst: WidgetInstance;
      try {
        inst = def.create(box, { config, options: config.options ?? {}, live: this.live });
      } catch (e) {
        // one broken widget shows its own error; the rest of the page keeps working
        box.replaceChildren(h('div', 'empty', `${config.type}: ${e instanceof Error ? e.message : String(e)}`));
        inst = {};
      }
      this.mounted.push({ config, def, inst, dirty: true });
    }
    this.root.replaceChildren(grid);
    this.catchUp();
  }

  /** Delivers `kind` to the widgets that need it; off-screen ones are marked dirty and catch up when shown. */
  emit(kind: DataKind): void {
    for (const m of this.mounted) {
      if (!m.def.needs.includes(kind)) continue;
      if (this.visible) run(m, kind, false);
      else if (m.def.always && kind === 'tick') run(m, kind, true);
      else m.dirty = true;
    }
  }

  /** Whether any mounted widget needs `kind`, so loaders can skip data nobody shows. */
  needs(kind: DataKind): boolean {
    return this.mounted.some((m) => m.def.needs.includes(kind));
  }

  catchUp(): void {
    if (!this.visible) return;
    for (const m of this.mounted) {
      if (!m.dirty) continue;
      m.dirty = false;
      for (const k of m.def.needs) run(m, k, false);
    }
  }

  unmount(): void {
    for (const m of this.mounted) {
      try { m.inst.destroy?.(); } catch { /* a widget's cleanup must not stop the others */ }
    }
    this.mounted = [];
  }
}

function run(m: Mounted, kind: DataKind, background: boolean): void {
  try { m.inst.update?.(kind, background); } catch (e) { console.warn(m.config.type, e); }
}

/** Where a widget lands: kept inside the 12 × 6 grid whatever the layout says (the agent validates too). */
export function placement(c: Pick<WidgetConfig, 'x' | 'y' | 'w' | 'h'>): { x: number; y: number; w: number; h: number } {
  const x = clamp(c.x, 0, COLS - 1), y = clamp(c.y, 0, ROWS - 1);
  return { x, y, w: clamp(c.w, 1, COLS - x), h: clamp(c.h, 1, ROWS - y) };
}

const clamp = (v: number, lo: number, hi: number): number =>
  Math.min(hi, Math.max(lo, Number.isFinite(v) ? Math.round(v) : lo));
