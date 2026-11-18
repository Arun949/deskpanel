import { h } from '../dom';
import { DASH, gb, pct, rate } from '../format';
import { registerWidget } from '../registry';
import type { Metrics } from '../store';

type Metric = 'cpu' | 'ram' | 'net';
const HISTORY = 60;

/** The headline value, a sub-line, and a 0–100 fill for the sparkline. Pure, so it is unit-tested. */
export function statView(metric: Metric, m: Metrics | null): { label: string; value: string; sub: string; level: number | null } {
  switch (metric) {
    case 'cpu':
      return { label: 'CPU', value: pct(m?.cpu), sub: '', level: m?.cpu ?? null };
    case 'ram': {
      const level = m?.ramUsedGb != null && m.ramTotalGb ? (100 * m.ramUsedGb) / m.ramTotalGb : null;
      return { label: 'Memory', value: level == null ? DASH : pct(level), sub: gb(m?.ramUsedGb, m?.ramTotalGb), level };
    }
    case 'net': {
      const down = m?.netDownBps ?? null, up = m?.netUpBps ?? null;
      // log scale so both 1 KB/s and 50 MB/s are visible: 10 MB/s ≈ full
      const level = down == null ? null : Math.min(100, (Math.log10(1 + down + (up ?? 0)) / 7) * 100);
      return { label: 'Network', value: `↓ ${rate(down)}`, sub: `↑ ${rate(up)}`, level };
    }
  }
}

registerWidget('stat', {
  needs: ['metrics'],
  create(box, { options, live }) {
    const metric: Metric = options['metric'] === 'ram' || options['metric'] === 'net' ? options['metric'] : 'cpu';
    const label = h('div', 'stat-label');
    const value = h('div', 'stat-value');
    const sub = h('div', 'stat-sub');
    const spark = h('div', 'spark');
    const bars = Array.from({ length: HISTORY }, () => spark.appendChild(h('i')));
    box.append(label, value, sub, spark);
    const history: Array<number | null> = [];
    return {
      update() {
        const v = statView(metric, live.metrics);
        label.textContent = v.label;
        value.textContent = v.value;
        sub.textContent = v.sub;
        history.push(v.level);
        if (history.length > HISTORY) history.shift();
        const offset = HISTORY - history.length;
        bars.forEach((bar, i) => {
          const l = i < offset ? null : history[i - offset];
          bar.style.height = l == null ? '0' : `${Math.max(2, Math.round(l))}%`;
        });
      },
    };
  },
});
