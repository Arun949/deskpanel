import { h } from '../dom';
import { registerWidget } from '../registry';

registerWidget('clock', {
  needs: ['tick'],
  create(box, { options }) {
    const time = h('div', 'clock-time');
    const date = h('div', 'clock-date');
    box.append(time, date);
    const seconds = options['showSeconds'] !== false;
    return {
      update() {
        const now = new Date();
        time.textContent = now.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: seconds ? '2-digit' : undefined });
        date.textContent = now.toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long' });
      },
    };
  },
});
