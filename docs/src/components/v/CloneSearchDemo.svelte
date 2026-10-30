<script lang="ts">
  // CloneSearch on a toy rewards screen, step by step: options played on the clone, a beam over the node's screens,
  // a state reached twice searched once, no way back, and each option's worth backed up from its best line
  // (docs: macro-brain/search §3). Illustrative values; the beam is 2 here, 16 in the brain.
  import { words } from './i18n.ts';
  import type { Lang } from './names.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));
  const L: Record<string, Record<Lang, string>> = {
    root: { en: 'rewards: gold, potion, card', 'zh-CN': '奖励：金币、药水、卡牌', 'zh-TW': '獎勵：金幣、藥水、卡牌' },
    gold: { en: 'take gold', 'zh-CN': '拿金币', 'zh-TW': '拿金幣' },
    potion: { en: 'take potion', 'zh-CN': '拿药水', 'zh-TW': '拿藥水' },
    card: { en: 'open card reward', 'zh-CN': '打开卡牌奖励', 'zh-TW': '打開卡牌獎勵' },
    proceed: { en: 'proceed → map', 'zh-CN': '前进 → 地图', 'zh-TW': '前進 → 地圖' },
    pickA: { en: 'pick card A', 'zh-CN': '选牌 A', 'zh-TW': '選牌 A' },
    pickB: { en: 'pick card B', 'zh-CN': '选牌 B', 'zh-TW': '選牌 B' },
    skip: { en: 'skip → seen state', 'zh-CN': '略过 → 已见过', 'zh-TW': '略過 → 已見過' },
    same: { en: 'same state', 'zh-CN': '同一局面', 'zh-TW': '同一局面' },
  };
  const STEPS: Record<Lang, string[]> = {
    en: [
      'The decision: a rewards screen with three rewards. Each option will be played on the clone.',
      'Level 1: each option is played; what the game asks next is its afterstate, valued by V. "Proceed" leaves the node (the map): the search stops there.',
      'The beam keeps the 2 states V values best that are still screens of the node. "Open card reward" (34.05) is not kept.',
      'Level 2: the kept states’ own actions are played. Taking gold after the potion and the potion after the gold reach the same state: it is searched once.',
      'Level 3, from the 2 best states. Skipping the card reward would go back to a state already seen: that answer adds nothing.',
      'Each state is worth the best state after it, or V’s value where the search stopped. Each option is worth its afterstate’s worth; L0 takes the best.',
    ],
    'zh-CN': [
      '决策：一个有三个奖励的奖励界面。每个选项都会在 clone 上实际走一遍。',
      '第 1 层：走完每个选项，游戏接着问的就是它的 afterstate，由 V 打分。“前进”离开了节点（地图），搜索在那里停。',
      'beam 留下 V 分数最高、而且仍是节点内界面的 2 个局面。“打开卡牌奖励”（34.05）没被留下。',
      '第 2 层：走被留下的局面自己的动作。先拿药水再拿金币、和先拿金币再拿药水到达同一个局面：只搜一次。',
      '第 3 层，从最好的 2 个局面往下。略过卡牌奖励会回到已经见过的局面：这个答案不算数。',
      '每个局面的价值是它之后最好的局面，或搜索停下处 V 的值。每个选项的价值是它 afterstate 的价值；L0 取最高。',
    ],
    'zh-TW': [
      '決策：一個有三個獎勵的獎勵畫面。每個選項都會在複本上實際走一遍。',
      '第 1 層：走完每個選項，遊戲接著問的就是它的 afterstate，由 V 打分。「前進」離開了節點（地圖），搜尋在那裡停。',
      'beam 留下 V 分數最高、而且仍是節點內畫面的 2 個局面。「打開卡牌獎勵」（34.05）沒被留下。',
      '第 2 層：走被留下的局面自己的動作。先拿藥水再拿金幣、和先拿金幣再拿藥水到達同一個局面：只搜一次。',
      '第 3 層，從最好的 2 個局面往下。略過卡牌獎勵會回到已經見過的局面：這個答案不算數。',
      '每個局面的價值是它之後最好的局面，或搜尋停下處 V 的值。每個選項的價值是它 afterstate 的價值；L0 取最高。',
    ],
  };

  interface N {
    id: string;
    label: string;
    level: number;
    y: number;
    v: number | null;
    exit?: boolean; // left the node: the search stops
    parents: string[];
    beam?: boolean; // kept by the beam to be searched on
    back?: boolean; // goes back to a state seen before
    worth?: number;
  }
  const NODES: N[] = [
    { id: 'R', label: 'root', level: 0, y: 120, v: null, parents: [] },
    { id: 'b', label: 'potion', level: 1, y: 40, v: 34.15, parents: ['R'], beam: true, worth: 34.45 },
    { id: 'a', label: 'gold', level: 1, y: 120, v: 34.1, parents: ['R'], beam: true, worth: 34.22 },
    { id: 'c', label: 'card', level: 1, y: 190, v: 34.05, parents: ['R'], worth: 34.05 },
    { id: 'd', label: 'proceed', level: 1, y: 240, v: 33.9, parents: ['R'], exit: true, worth: 33.9 },
    { id: 'Cb', label: 'card', level: 2, y: 10, v: 34.18, parents: ['b'], beam: true, worth: 34.45 },
    { id: 'Mb', label: 'proceed', level: 2, y: 50, v: 34.02, parents: ['b'], exit: true, worth: 34.02 },
    { id: 'X', label: 'same', level: 2, y: 95, v: 34.2, parents: ['b', 'a'], beam: true, worth: 34.22 },
    { id: 'Ca', label: 'card', level: 2, y: 140, v: 34.12, parents: ['a'], worth: 34.12 },
    { id: 'Ma', label: 'proceed', level: 2, y: 180, v: 34.0, parents: ['a'], exit: true, worth: 34.0 },
    { id: 'A', label: 'pickA', level: 3, y: 0, v: 34.45, parents: ['Cb'], worth: 34.45 },
    { id: 'B', label: 'pickB', level: 3, y: 36, v: 34.3, parents: ['Cb'], worth: 34.3 },
    { id: 'S', label: 'skip', level: 3, y: 72, v: null, parents: ['Cb'], back: true },
    { id: 'Cx', label: 'card', level: 3, y: 110, v: 34.22, parents: ['X'], worth: 34.22 },
    { id: 'Mx', label: 'proceed', level: 3, y: 146, v: 34.05, parents: ['X'], exit: true, worth: 34.05 },
  ];
  const BEST = new Set(['R', 'b', 'Cb', 'A']);
  // What each step shows: the levels drawn, whether the beam is marked, whether worths are backed up.
  const SHOW = [
    { level: 0, beam: 0, worth: false },
    { level: 1, beam: 0, worth: false },
    { level: 1, beam: 1, worth: false },
    { level: 2, beam: 1, worth: false },
    { level: 3, beam: 2, worth: false },
    { level: 3, beam: 2, worth: true },
  ];
  let step = $state(0);
  const s = $derived(SHOW[step]);
  const byId = new Map(NODES.map((n) => [n.id, n]));
  const NW = 140;
  const NH = 28;
  const X0 = [0, 170, 340, 510];
  const visible = $derived(NODES.filter((n) => n.level <= s.level));
  const kept = (n: N) => n.beam && n.level <= s.beam;
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.5rem;">
    <button onclick={() => (step = Math.max(0, step - 1))} disabled={step === 0}>{t('back')}</button>
    <button onclick={() => (step = Math.min(SHOW.length - 1, step + 1))} disabled={step === SHOW.length - 1}>{t('step')} ({step + 1}/{SHOW.length})</button>
    <button onclick={() => (step = 0)}>{t('start')}</button>
  </div>
  <p style="min-height: 3em; margin-bottom: 0.5rem;">{STEPS[lang][step]}</p>
  <div class="v-scroll">
    <svg width={X0[3] + NW + 4} height="280" role="img" aria-label={STEPS[lang][step]}>
      {#each visible as n (n.id)}
        {#each n.parents.filter((p) => byId.get(p)!.level < n.level) as p (p)}
          {@const q = byId.get(p)!}
          {@const best = s.worth && BEST.has(n.id) && BEST.has(p)}
          <path
            d="M{X0[q.level] + NW},{q.y + NH / 2 + 10} C{X0[q.level] + NW + 15},{q.y + NH / 2 + 10} {X0[n.level] - 15},{n.y + NH / 2 + 10} {X0[n.level]},{n.y + NH / 2 + 10}"
            fill="none"
            stroke={best ? 'var(--v-s2)' : 'var(--v-axis)'}
            stroke-width={best ? 2.5 : 1}
            stroke-dasharray={n.id === 'X' && p === 'a' ? '4 3' : undefined}
          />
        {/each}
      {/each}
      {#each visible as n (n.id)}
        <g transform="translate({X0[n.level]},{n.y + 10})" opacity={n.back ? 0.55 : 1}>
          <rect
            width={NW}
            height={NH}
            rx="5"
            fill={kept(n) ? 'var(--v-wash)' : 'var(--v-surface)'}
            stroke={s.worth && BEST.has(n.id) ? 'var(--v-s2)' : n.exit || n.back ? 'var(--v-axis)' : 'var(--v-s1)'}
            stroke-width={kept(n) || (s.worth && BEST.has(n.id)) ? 2 : 1}
          />
          <text x="7" y="12" style="font-size: 10.5px;" fill="var(--v-ink)">{L[n.label][lang]}{n.back ? ' ✕' : ''}</text>
          {#if n.v !== null}
            <text x="7" y="24" style="font-size: 10px;" class="v-num">
              V {n.v.toFixed(2)}{s.worth && n.worth !== undefined && n.worth !== n.v ? ` → ${n.worth.toFixed(2)}` : ''}
            </text>
          {/if}
        </g>
      {/each}
    </svg>
  </div>
  <p class="v-caption">{t('illustrative')}</p>
</div>
