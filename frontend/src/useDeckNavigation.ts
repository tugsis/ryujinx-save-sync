import { useEffect } from "react";

export function useDeckNavigation(enabled: boolean, modal: boolean, close: () => void) {
  useEffect(() => {
    if (!enabled) return;
    const visible = (scope: ParentNode) => Array.from(scope.querySelectorAll<HTMLElement>('button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href], summary'))
      .filter(e => e.getClientRects().length && getComputedStyle(e).visibility !== "hidden");
    const dialog = document.querySelector<HTMLElement>('[role="dialog"]');
    const background = document.querySelector<HTMLElement>('.deck-app');
    const previous = document.activeElement as HTMLElement | null;
    if (dialog) { if (background) background.inert = true; visible(dialog)[0]?.focus(); }
    const handler = (e: KeyboardEvent) => {
      const scope = document.querySelector('[role="dialog"]') ?? document;
      const list = visible(scope);
      if (e.key === 'Escape' && modal) { e.preventDefault(); close(); return; }
      if (e.key === 'Tab' && modal && list.length) {
        if (e.shiftKey && (document.activeElement === list[0] || !scope.contains(document.activeElement))) { e.preventDefault(); list.at(-1)?.focus(); }
        else if (!e.shiftKey && document.activeElement === list.at(-1)) { e.preventDefault(); list[0].focus(); }
      }
      if (!['ArrowUp','ArrowDown','ArrowLeft','ArrowRight'].includes(e.key) || /^(INPUT|TEXTAREA|SELECT)$/.test((e.target as HTMLElement).tagName)) return;
      const active = document.activeElement as HTMLElement;
      if (!list.includes(active)) { list[0]?.focus(); e.preventDefault(); return; }
      const rect = active.getBoundingClientRect(), x = rect.x + rect.width / 2, y = rect.y + rect.height / 2;
      const horizontal = ['ArrowLeft', 'ArrowRight'].includes(e.key), positive = ['ArrowRight','ArrowDown'].includes(e.key);
      const candidates = list.filter(item => item !== active).map(item => {
        const r = item.getBoundingClientRect(), dx = r.x + r.width / 2 - x, dy = r.y + r.height / 2 - y;
        return { item, along: horizontal ? dx : dy, across: horizontal ? dy : dx };
      }).filter(c => positive ? c.along > 4 : c.along < -4).sort((a,b) => Math.abs(a.along) + Math.abs(a.across)*2 - Math.abs(b.along) - Math.abs(b.across)*2);
      if (candidates.length) { e.preventDefault(); candidates[0].item.focus(); }
    };
    document.addEventListener('keydown', handler);
    return () => { document.removeEventListener('keydown', handler); if (background) background.inert = false; if (dialog) previous?.focus(); };
  }, [enabled, modal, close]);
}
