<script lang="ts">
  // A fork on the map and the map bag of each option: the point entered reads as "+0" (encode.map_tokens), and a room
  // already played does not. An illustrative map, no weights.
  import type { Lang } from './names.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const L = {
    en: {
      options: ['Choose the elite', 'Choose the monster', 'After the elite is won'],
      notes: [
        'V values this option before the fight. Its samples are real map moves, labelled with how those runs ended, deaths in that elite included.',
        'The points after it are the same, so without its own tag this option would read exactly like the elite.',
        'A run view logged in the room comes after its combat, and a run that died there logs none. So a room already played is not made a tag: what it gave is in the deck, relics, HP and gold.',
      ],
      you: 'you',
      points: { Elite: 'elite', Monster: 'monster', RestSite: 'rest', Boss: 'boss' },
    },
    'zh-CN': {
      options: ['选精英', '选普通怪', '打赢精英之后'],
      notes: [
        'V 在开打之前为这个选项打分。它的样本是真实的选路，标签是那些局最后的结局，死在这场精英战的也算在内。',
        '后面的点相同，所以没有自己的标记时，这个选项读起来和精英完全一样。',
        '房间里记录的局面都在战斗之后，死在里面的局留不下记录。所以打过的房间不做成标记：它带来的东西都在牌组、遗物、HP 和金币里。',
      ],
      you: '你',
      points: { Elite: '精英', Monster: '怪物', RestSite: '休息', Boss: 'Boss' },
    },
    'zh-TW': {
      options: ['選菁英', '選普通怪', '打贏菁英之後'],
      notes: [
        'V 在開打之前為這個選項打分。它的樣本是真實的選路，標籤是那些局最後的結局，死在這場菁英戰的也算在內。',
        '後面的點相同，所以沒有自己的標記時，這個選項讀起來和菁英完全一樣。',
        '房間裡記錄的局面都在戰鬥之後，死在裡面的局留不下記錄。所以打過的房間不做成標記：它帶來的東西都在牌組、遺物、HP 和金幣裡。',
      ],
      you: '你',
      points: { Elite: '菁英', Monster: '怪物', RestSite: '休息', Boss: '頭目' },
    },
  }[lang];
  const BAGS = [
    ['Elite+0', 'RestSite+1', 'Boss+2'],
    ['Monster+0', 'RestSite+1', 'Boss+2'],
    ['RestSite+1', 'Boss+2'],
  ];
  let pick = $state(0);
  const on = (key: string) => (key === 'Elite' ? pick !== 1 : key === 'Monster' ? pick !== 0 && pick !== 2 : true);
  const NODES: [string, number, number][] = [
    ['Elite', 130, 30],
    ['Monster', 130, 90],
    ['RestSite', 230, 60],
    ['Boss', 330, 60],
  ];
</script>

<div class="vfig">
  <div class="v-row buttons">
    {#each L.options as o, i (i)}
      <button type="button" class:hot={pick === i} aria-pressed={pick === i} onclick={() => (pick = i)}>{o}</button>
    {/each}
  </div>
  <div class="v-scroll">
    <svg viewBox="0 0 380 120" style="width: 100%; min-width: 320px; max-width: 460px;" role="img" aria-label={BAGS[pick].join(', ')}>
      <path d="M40,60L130,30M40,60L130,90M130,30L230,60M130,90L230,60M230,60L330,60" fill="none" stroke="var(--v-ink-2)" stroke-width="1.5" />
      <circle cx="130" cy={pick === 1 ? 90 : 30} r="24" fill="none" stroke="var(--v-ink)" stroke-width="1.5" stroke-dasharray={pick === 2 ? undefined : '4 3'} />
      <circle cx="40" cy="60" r="18" fill="var(--v-surface)" stroke="var(--v-ink-2)" stroke-width="1.5" />
      <text x="40" y="64" text-anchor="middle" class="label">{L.you}</text>
      {#each NODES as [key, x, y] (key)}
        <circle cx={x} cy={y} r="19" fill="var(--v-surface)" stroke="var(--v-s1)" stroke-width="1.75" opacity={on(key) ? 1 : 0.35} />
        <text x={x} y={y + 4} text-anchor="middle" class="label" opacity={on(key) ? 1 : 0.35}>{L.points[key as keyof typeof L.points]}</text>
      {/each}
    </svg>
  </div>
  <div class="v-row chips">
    {#each BAGS[pick] as t (t)}
      <code class:zero={t.includes('+0')}>{t}</code>
    {/each}
  </div>
  <p class="v-caption" style="min-height: 3.6em;">{L.notes[pick]}</p>
</div>

<style>
  .buttons { display: flex; flex-wrap: wrap; gap: 0.4rem; margin-bottom: 0.5rem; }
  button { font: inherit; font-size: 0.8125rem; padding: 0.2rem 0.6rem; border: 1px solid var(--v-ink-2); border-radius: 5px; background: var(--v-surface); color: var(--v-ink); cursor: pointer; }
  button.hot { background: var(--v-ink); color: var(--v-surface); }
  .chips { display: flex; flex-wrap: wrap; gap: 0.35rem; margin-top: 0.4rem; }
  code { font-size: 0.8125rem; }
  code.zero { outline: 2px solid var(--v-s3); }
  .label { font-size: 11px; fill: var(--v-ink); }
</style>
