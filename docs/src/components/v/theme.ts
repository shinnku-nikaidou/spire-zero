// The figures' colours come from viz.css's tokens; canvases read them and draw again when the theme changes.
import { interpolateLab } from 'd3';

export function token(el: Element, name: string): string {
  return getComputedStyle(el).getPropertyValue(name).trim();
}

/** Calls back whenever Starlight switches between light and dark; returns the stop function. */
export function onTheme(callback: () => void): () => void {
  const observer = new MutationObserver(callback);
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
  return () => observer.disconnect();
}

/** A value's colour: sequential (0 → max, one hue) or diverging (−max → max, grey midpoint). */
export function colourScale(el: Element, diverging: boolean, max: number): (v: number) => string {
  const mid = token(el, '--v-mid');
  const pos = interpolateLab(mid, token(el, '--v-pos'));
  const neg = interpolateLab(mid, token(el, '--v-neg'));
  const m = max > 0 ? max : 1;
  return (v: number) => {
    const t = Math.min(1, Math.abs(v) / m);
    return v < 0 && diverging ? neg(t) : pos(t);
  };
}
