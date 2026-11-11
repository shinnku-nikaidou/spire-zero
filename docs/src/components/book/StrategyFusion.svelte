<script lang="ts">
  // Strategy fusion below the root, on a made-up decision with two worlds. Safe is worth
  // the same in both; Gamble asks the player to keep one of two cards before the hidden top card is drawn.
  import type { Lang } from '../v/names.ts';
  import { w, say, words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  const SAFE = 0.7;
  const MATCH = 1.0; // the kept card works with the card drawn
  const MISS = 0.2; // it does not

  let p = $state(0.5); // the chance that the top card is a Strike
  // Each world's own search knows the top card, so after Gamble it keeps the right card.
  const worldS = { safe: SAFE, gamble: MATCH };
  const worldD = { safe: SAFE, gamble: MATCH };
  const pimc = $derived({ safe: SAFE, gamble: p * worldS.gamble + (1 - p) * worldD.gamble });
  // A player who cannot see the card must keep one before the draw.
  const keepS = $derived(p * MATCH + (1 - p) * MISS);
  const keepD = $derived(p * MISS + (1 - p) * MATCH);
  const truth = $derived({ safe: SAFE, gamble: Math.max(keepS, keepD) });
  const best = (v: { safe: number; gamble: number }) => (v.gamble > v.safe ? 'gamble' : 'safe');
  const f = (x: number) => x.toFixed(2);

  const NAME = {
    safe: w('Safe: play a block card, end the turn', '稳妥：打一张格挡牌，结束回合', '穩妥：打一張格擋牌，結束回合'),
    gamble: w('Gamble: discard one of two cards, then draw the top card', '赌一把：从两张牌里弃一张，再抽最上面那张', '賭一把：從兩張牌裡棄一張，再抽最上面那張'),
  };
  const ROWS = ['safe', 'gamble'] as const;
</script>

<div class="vfig">
  <svg viewBox="0 0 640 170" width="100%" role="img" aria-label="decision tree">
    <circle cx="40" cy="85" r="6" fill="var(--v-ink-2)" />
    <text x="40" y="70" text-anchor="middle">{say(lang, w('now', '现在', '現在'))}</text>
    <line x1="46" y1="82" x2="190" y2="35" stroke="var(--v-axis)" />
    <line x1="46" y1="88" x2="190" y2="125" stroke="var(--v-axis)" />
    <text x="200" y="39">{say(lang, w('Safe', '稳妥', '穩妥'))} → {f(SAFE)} {say(lang, w('in both worlds', '两个世界都一样', '兩個世界都一樣'))}</text>
    <text x="200" y="129">{say(lang, w('Gamble', '赌一把', '賭一把'))}</text>
    <line x1="250" y1="125" x2="330" y2="100" stroke="var(--v-axis)" />
    <line x1="250" y1="125" x2="330" y2="150" stroke="var(--v-axis)" />
    <text x="336" y="104">{say(lang, w('keep the Strike partner', '留下配打击的牌', '留下配打擊的牌'))}</text>
    <text x="336" y="154">{say(lang, w('keep the Defend partner', '留下配防御的牌', '留下配防禦的牌'))}</text>
    <text x="500" y="104" fill="var(--v-ink)">S: {f(MATCH)} · D: {f(MISS)}</text>
    <text x="500" y="154" fill="var(--v-ink)">S: {f(MISS)} · D: {f(MATCH)}</text>
    <text x="336" y="128" class="v-muted">{say(lang, w('…then the top card is drawn', '……然后才抽最上面那张', '……然後才抽最上面那張'))}</text>
  </svg>

  <div class="v-row" style="margin: 0.5rem 0;">
    <label>
      {say(lang, w('Chance the top card is a Strike (world S)', '最上面是打击的概率（世界 S）', '最上面是打擊的機率（世界 S）'))}
      <input type="range" min="0" max="1" step="0.05" bind:value={p} />
      <span class="v-num">{f(p)}</span>
    </label>
  </div>

  <div class="v-scroll">
    <table>
      <thead>
        <tr>
          <th></th>
          <th>{say(lang, w('world S’s search', '世界 S 的搜索', '世界 S 的搜尋'))}</th>
          <th>{say(lang, w('world D’s search', '世界 D 的搜索', '世界 D 的搜尋'))}</th>
          <th>{t('pimc')}</th>
          <th>{t('truth')}</th>
        </tr>
      </thead>
      <tbody class="v-num">
        {#each ROWS as r (r)}
          <tr>
            <td>{say(lang, NAME[r])}</td>
            <td>{f(worldS[r])}</td>
            <td>{f(worldD[r])}</td>
            <td class:pick={best(pimc) === r}>{f(pimc[r])}</td>
            <td class:pick={best(truth) === r}>{f(truth[r])}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
  <p class="v-ink2" style="margin-top: 0.5rem;">
    {#if best(pimc) !== best(truth)}
      {say(lang, w(
        `The average over worlds gives Gamble ${f(pimc.gamble)} and picks it, but a player who must keep a card before the draw gets only ${f(truth.gamble)}: each world’s search kept the right card because it already knew the top card. That is strategy fusion after the first move.`,
        `取平均给赌一把 ${f(pimc.gamble)}，选了它，但必须在抽牌前留牌的玩家只拿得到 ${f(truth.gamble)}：每个世界的搜索都留对了牌，因为它早就知道最上面是什么。这就是第一步之后的策略融合。`,
        `取平均給賭一把 ${f(pimc.gamble)}，選了它，但必須在抽牌前留牌的玩家只拿得到 ${f(truth.gamble)}：每個世界的搜尋都留對了牌，因為它早就知道最上面是什麼。這就是第一步之後的策略融合。`,
      ))}
    {:else}
      {say(lang, w(
        `Here both pick ${best(truth) === 'gamble' ? 'Gamble' : 'Safe'}: one world is likely enough that keeping its card is worth the risk. The average still overvalues Gamble (${f(pimc.gamble)} against ${f(truth.gamble)}).`,
        `这里两者都选${best(truth) === 'gamble' ? '赌一把' : '稳妥'}：其中一个世界够可能，留下它的牌值得冒险。取平均仍然高估了赌一把（${f(pimc.gamble)} 对 ${f(truth.gamble)}）。`,
        `這裡兩者都選${best(truth) === 'gamble' ? '賭一把' : '穩妥'}：其中一個世界夠可能，留下它的牌值得冒險。取平均仍然高估了賭一把（${f(pimc.gamble)} 對 ${f(truth.gamble)}）。`,
      ))}
    {/if}
  </p>
  <p class="v-caption">{t('illustrative')} {say(lang, w('Values are made up: higher is better.', '数值是虚构的，越高越好。', '數值是虛構的，越高越好。'))}</p>
</div>

<style>
  table {
    width: 100%;
    border-collapse: collapse;
  }
  th,
  td {
    padding: 0.3rem 0.45rem;
    border-top: 1px solid var(--v-grid);
    text-align: right;
  }
  th:first-child,
  td:first-child {
    text-align: left;
  }
  th {
    color: var(--v-ink-2);
    font-weight: 500;
  }
  td.pick {
    font-weight: 700;
    color: var(--v-s1);
  }
</style>
