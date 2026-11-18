/**
 * Builds DOM without innerHTML: children are elements or text nodes, so data from the agent can never become markup
 * (PhoneBox's h() helper, Module 10). Do not add an innerHTML escape hatch here.
 */
export function h<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  className?: string | null,
  ...kids: Array<Node | string | number | null | undefined | false>
): HTMLElementTagNameMap[K] {
  const el = document.createElement(tag);
  if (className) el.className = className;
  for (const k of kids) {
    if (k == null || k === false) continue;
    el.append(typeof k === 'object' ? k : String(k));
  }
  return el;
}
