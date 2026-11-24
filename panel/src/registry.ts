// The widget contract (adapted from PhoneBox's, Module 10) with types.

/** Kinds of data a widget can ask for. The agent pushes them; `tick` is the panel's own 1 s clock. */
export type DataKind = 'tick' | 'metrics' | 'media' | 'volume' | 'presence' | 'layout';

/** Read access to the live state, given to every widget. */
export interface LiveView {
  readonly metrics: import('./store').Metrics | null;
  readonly media: import('./store').Media | null;
  readonly volume: import('./store').Volume | null;
  readonly presence: import('./store').Presence | null;
  /** Local time the media section was last received, to advance the progress bar between messages. */
  readonly receivedAt: number;
}

/** One widget placed on the 12 × 6 grid, as the agent's layout.json describes it. */
export interface WidgetConfig {
  id: string;
  type: string;
  x: number;
  y: number;
  w: number;
  h: number;
  options?: Record<string, unknown>;
}

export interface WidgetCtx {
  config: WidgetConfig;
  options: Record<string, unknown>;
  live: LiveView;
}

export interface WidgetInstance {
  /** Refresh what `kind` changed. `background` is true for off-screen `always` widgets. */
  update?(kind: DataKind, background: boolean): void;
  destroy?(): void;
}

export interface WidgetDef {
  needs: readonly DataKind[];
  /** Gets `tick` even when its page is not visible (timers that must ring anywhere). */
  always?: boolean;
  create(box: HTMLElement, ctx: WidgetCtx): WidgetInstance;
}

const registry = new Map<string, WidgetDef>();

export function registerWidget(type: string, def: WidgetDef): void {
  if (!/^[a-z][a-z0-9-]{0,39}$/.test(type)) throw new Error(`invalid widget type "${type}"`);
  if (registry.has(type)) throw new Error(`widget type "${type}" is registered twice`);
  registry.set(type, def);
}

export const widgetDef = (type: string): WidgetDef | undefined => registry.get(type);
export const widgetTypes = (): string[] => [...registry.keys()].sort();
