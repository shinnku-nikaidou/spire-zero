<script lang="ts">
  // The mod's panel as Panel.cs lays it out (docs: output §2): the head, then an answer or a message. The combat
  // answer is a real PIMC decision from a NoSL run's decision log, the map answer the example run's floor 43
  // (src/data/book/panel-examples.json). Names come from the game's tables; values print as the panel prints them.
  import data from '../../data/book/panel-examples.json';
  import { loadNames, type Lang, type Names } from '../v/names.ts';
  import { w, say } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();

  let names = $state<Names | null>(null);
  $effect(() => {
    loadNames(lang).then((n) => (names = n));
  });

  type Mode = 'combat' | 'map' | 'message';
  let mode = $state<Mode>('combat');
  let considered = $state<'off' | 'values' | 'bars'>('bars');
  let message = $state(0);

  const C = data.combat;
  const M = data.map;
  const card = (uid: number) => C.hand.find((h) => h.uid === uid)?.id ?? '?';

  // Describe.Combat: the card's name and its target; one living enemy needs no number.
  function combat(text: string): string {
    if (!names) return text;
    if (text === 'end_turn') return say(lang, w('end turn', 'end turn', 'end turn'));
    const [, c, t] = text.split(':');
    const target = t !== undefined ? ' → ' + names.name('monster', C.enemies.find((e) => e.uid === +t)!.id) : '';
    return names.name('card', card(+c)) + target;
  }
  const room = (a: string) => {
    const [col, row] = a.slice(4).split(',').map(Number);
    const type = M.points.find((p) => p.col === col && p.row === row)?.type ?? '?';
    // Describe.Room: the map legend's name; a type the legend has no entry for (the boss) is shown lower-cased.
    const legend = ['Monster', 'Elite', 'Unknown', 'RestSite', 'Shop', 'Treasure'].includes(type);
    return legend && names ? names.name('map', type) : type.toLowerCase();
  };

  // Describe.Turn: the plan up to and including the turn's end.
  const turn = $derived.by(() => {
    const all = [C.chosen, ...C.plan];
    const end = all.indexOf('end_turn');
    return end < 0 ? all : all.slice(0, end + 1);
  });
  const top = C.candidates.slice(0, 3);
  const lo0 = Math.min(...top.map((o) => o.value - o.ci));
  const hi0 = Math.max(...top.map((o) => o.value + o.ci));
  const pad = Math.max((hi0 - lo0) * 0.1, 1e-6);
  const [lo, hi] = [lo0 - pad, hi0 + pad];
  const at = (v: number) => ((v - lo) / (hi - lo)) * 96;

  const MESSAGES: { status: string; text: string; sub?: string; tone?: 'warn' | 'error'; env?: boolean }[] = [
    { status: 'starting', text: 'Connecting to the brain', sub: '~/.local/state/sts2ai/brain.sock', env: true },
    { status: 'offline', text: 'No brain running', sub: 'Connection refused · trying again every 2 s', tone: 'warn', env: true },
    { status: 'run sent', text: 'Waiting for the brain', sub: 'it loads the champion for this run', env: true },
    { status: 'ready', text: 'Waiting for a decision', sub: 'open the chest', env: true },
    { status: 'ready', text: 'No SL clone of this room', sub: 'a potion thrown away in combat · the next room has one', tone: 'warn', env: true },
    { status: 'ready', text: 'Thinking…', sub: '3.4 s', env: true },
    { status: 'off for this run', text: 'Brain error', sub: 'an SL decision before any node checkpoint · back on at the next run', tone: 'error', env: true },
    { status: 'off', text: 'Off in multiplayer', env: false },
  ];
  const msg = $derived(MESSAGES[message]);
  const champion = $derived(mode === 'combat' ? C.source.champion : M.source.champion);
  const env = $derived(mode === 'combat' ? 'NoSL' : mode === 'map' ? 'SL' : msg.env ? 'SL' : null);
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.75rem;">
    <div class="v-tabs" style="margin: 0;">
      <button aria-pressed={mode === 'combat'} onclick={() => (mode = 'combat')}>{say(lang, w('Combat answer', '战斗的回答', '戰鬥的回答'))}</button>
      <button aria-pressed={mode === 'map'} onclick={() => (mode = 'map')}>{say(lang, w('Map answer', '地图的回答', '地圖的回答'))}</button>
      <button aria-pressed={mode === 'message'} onclick={() => (mode = 'message')}>{say(lang, w('A message', '一条消息', '一則訊息'))}</button>
    </div>
    {#if mode === 'combat'}
      <label class="v-muted">{say(lang, w('Considered options', 'Considered options', 'Considered options'))}
        <select bind:value={considered}>
          <option value="off">Off</option>
          <option value="values">Values</option>
          <option value="bars">Values + bars</option>
        </select>
      </label>
    {:else if mode === 'message'}
      <select bind:value={message} aria-label={say(lang, w('Message', '消息', '訊息'))}>
        {#each MESSAGES as m, i (i)}<option value={i}>{m.text}</option>{/each}
      </select>
    {/if}
  </div>

  <div class="game">
    <div class="card">
      <div class="head">
        <span class="badge fill" style="width: 28px; height: 28px; border-radius: 8px; font-size: 12px;">AI</span>
        <div class="names">
          <div class="clip" style="font-size: 17px;">{mode === 'message' && message < 3 ? '—' : champion}</div>
          <div class="clip muted" style="font-size: 13px;">{mode === 'message' ? msg.status : 'ready'}</div>
        </div>
        <div class="switch">
          <span class:on={env === 'SL'}>SL</span><span class:on={env === 'NoSL'}>NoSL</span>
        </div>
        <span class="muted" style="font-size: 12px;">F8</span>
      </div>

      {#if mode === 'message'}
        <div class="row" style="gap: 10px;">
          <span class="dot" class:warn={msg.tone === 'warn'} class:error={msg.tone === 'error'}></span>
          <div>
            <div style="font-size: 19px;" class:warn-t={msg.tone === 'warn'} class:error-t={msg.tone === 'error'}>{msg.text}</div>
            {#if msg.sub}<div class="muted" style="font-size: 14px; margin-top: 4px;">{msg.sub}</div>{/if}
          </div>
        </div>
      {:else if mode === 'combat'}
        <div class="row" style="gap: 12px;">
          <span class="badge fill" style="width: 32px; height: 32px; border-radius: 16px; font-size: 16px;">1</span>
          <span style="font-size: 24px;">{combat(turn[0])}</span>
        </div>
        {#each turn.slice(1) as step, i (i)}
          <div class="row" style="gap: 12px; padding-left: 4px;">
            <span class="badge ring" style="width: 24px; height: 24px; border-radius: 12px; font-size: 12px;">{i + 2}</span>
            <span style="font-size: 17px;">{combat(step)}</span>
          </div>
        {/each}
        {#if considered !== 'off'}
          <div class="rule"></div>
          <div class="row muted" style="font-size: 12px; justify-content: space-between;"><span>CONSIDERED</span><span>value ±CI</span></div>
          <div class="grid" class:bars={considered === 'bars'}>
            {#each top as o, i (o.action)}
              <span class="clip" class:accent={i === 0} style="font-size: 15px;">{combat(o.action)}</span>
              <span class="value" class:accent={i === 0}>{o.value.toFixed(1)} ±{o.ci.toFixed(1)}</span>
              {#if considered === 'bars'}
                <svg width="96" height="16" aria-hidden="true">
                  <rect x="0" y="3" width="96" height="10" rx="5" fill="#221b30" />
                  <rect x={at(o.value - o.ci)} y="3" width={Math.max(at(o.value + o.ci) - at(o.value - o.ci), 3)} height="10" rx="5" fill={i === 0 ? '#bf59ff' : '#5a4d72'} />
                  <rect x={at(o.value) - 1} y="0" width="2" height="16" fill="#f3effa" />
                </svg>
              {/if}
            {/each}
          </div>
        {/if}
        <div class="muted" style="font-size: 13px;">{C.brain} · {(C.elapsed_ms / 1000).toFixed(1)} s</div>
      {:else}
        <div class="row" style="gap: 12px;">
          <span class="badge" style="width: 32px; height: 32px; border-radius: 16px; border: 4px solid #bf59ff; box-sizing: border-box;"></span>
          <span style="font-size: 24px;">go to {room(M.chosen)}</span>
        </div>
        <div class="muted" style="font-size: 12px;">ROUTE TO THE BOSS</div>
        <div style="font-size: 15px;">{M.route.map(room).join('  ·  ')}</div>
        <div class="muted" style="font-size: 13px;">{M.brain} · 0.0 s</div>
      {/if}
    </div>
  </div>

  <p class="v-caption">
    {#if mode === 'combat'}
      {say(lang, w(
        `A NoSL elite fight against the Terror Eel, decision ${'112'} of a NoSL champion's run (seed ${C.source.seed}, floor ${C.source.floor}): PIMC's pick, the rest of its turn in the first world, and its three best candidates. The panel prints values to one decimal, so these three read the same; the bars show them apart. The foot's time is the decision's search time; the mod times the whole round trip.`,
        `NoSL 冠军一局里对恐怖鳗的精英战（种子 ${C.source.seed}，第 ${C.source.floor} 层）的第 112 个决策：PIMC 的选择、它在第一个世界里这回合剩下的步骤，以及最好的三个候选。面板只印到小数点后一位，所以这三个读起来一样；条形图才分得出来。底行的时间是这个决策的搜索时间；Mod 实际计的是整趟往返。`,
        `NoSL 冠軍一局裡對恐怖鰻的菁英戰（種子 ${C.source.seed}，第 ${C.source.floor} 層）的第 112 個決策：PIMC 的選擇、它在第一個世界裡這回合剩下的步驟，以及最好的三個候選。面板只印到小數點後一位，所以這三個讀起來一樣；長條圖才分得出來。底行的時間是這個決策的搜尋時間；Mod 實際計的是整趟往返。`,
      ))}
    {:else if mode === 'map'}
      {say(lang, w(
        `The book’s example run on floor ${M.source.floor} (act 3, ${M.source.hp} of ${M.source.max_hp} HP): L0 goes to the monster rather than the rest site. The route is the one the run then took; L0 plans it again at each point. The macro brain gives no candidates, so the panel shows none. Outside combat the brain answers in under 20 ms, which the panel prints as 0.0 s.`,
        `贯穿全书那一局的第 ${M.source.floor} 层（第三幕，HP ${M.source.hp}/${M.source.max_hp}）：L0 选了普通战斗而不是休息处。路线是这一局后来实际走的；L0 在每个点都会重新规划。宏观大脑不给候选，所以面板不显示。战斗外大脑 20 ms 内就回答，面板印成 0.0 s。`,
        `貫穿全書那一局的第 ${M.source.floor} 層（第三幕，HP ${M.source.hp}/${M.source.max_hp}）：L0 選了普通戰鬥而不是休息處。路線是這一局後來實際走的；L0 在每個點都會重新規劃。宏觀大腦不給候選，所以面板不顯示。戰鬥外大腦 20 ms 內就回答，面板印成 0.0 s。`,
      ))}
    {:else}
      {say(lang, w(
        'The messages the panel shows instead of an answer, with their status and quieter line, as Assist.cs writes them; the reasons in the second lines are examples. A warning is amber, an error red.',
        '面板在没有回答时显示的消息，连同状态字和较淡的第二行，照 Assist.cs 的写法；第二行里的原因是举例。警告是琥珀色，错误是红色。',
        '面板在沒有回答時顯示的訊息，連同狀態字和較淡的第二行，照 Assist.cs 的寫法；第二行裡的原因是舉例。警告是琥珀色，錯誤是紅色。',
      ))}
    {/if}
    {say(lang, w(' The texts are the mod’s own, in English, with the game’s names in this page’s language.', '文字是 Mod 自己的英文，游戏名称用本页的语言。', '文字是 Mod 自己的英文，遊戲名稱用本頁的語言。'))}
  </p>
</div>

<style>
  .game {
    background: repeating-linear-gradient(135deg, #2b3a2f 0 14px, #26342a 14px 28px);
    border-radius: 10px;
    padding: 18px;
    overflow-x: auto;
  }
  .card {
    width: 420px;
    max-width: 100%;
    box-sizing: border-box;
    background: rgba(16, 12, 24, 0.92);
    border: 1px solid #2e2540;
    border-radius: 14px;
    box-shadow: 0 0 12px rgba(0, 0, 0, 0.4);
    padding: 16px 18px;
    color: #f3effa;
    display: flex;
    flex-direction: column;
    gap: 10px;
    font-family: system-ui, sans-serif;
    line-height: 1.25;
  }
  .head {
    display: flex;
    align-items: center;
    gap: 10px;
    margin-bottom: 4px;
  }
  .names {
    flex: 1;
    min-width: 0;
  }
  .clip {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .muted {
    color: #b5abc8;
  }
  .accent {
    color: #bf59ff;
  }
  .badge {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    flex: none;
  }
  .badge.fill {
    background: #bf59ff;
    color: #14081f;
  }
  .badge.ring {
    border: 2px solid #bf59ff;
    box-sizing: border-box;
  }
  .switch {
    display: flex;
    gap: 2px;
    border: 1px solid #3a3050;
    border-radius: 10px;
    padding: 3px;
    font-size: 14px;
  }
  .switch span {
    padding: 6px 14px;
    border-radius: 7px;
    color: #b5abc8;
  }
  .switch span.on {
    background: #bf59ff;
    color: #14081f;
  }
  .row {
    display: flex;
    align-items: center;
  }
  .dot {
    width: 10px;
    height: 10px;
    border-radius: 5px;
    background: #bf59ff;
    flex: none;
    align-self: flex-start;
    margin-top: 8px;
  }
  .dot.warn {
    background: #f5b544;
  }
  .dot.error {
    background: #ff8a70;
  }
  .warn-t {
    color: #f5b544;
  }
  .error-t {
    color: #ff8a70;
  }
  .rule {
    height: 1px;
    background: #2e2540;
  }
  .grid {
    display: grid;
    grid-template-columns: 1fr 92px;
    gap: 6px 10px;
    align-items: center;
  }
  .grid.bars {
    grid-template-columns: 1fr 92px 96px;
  }
  .value {
    font-size: 15px;
    text-align: right;
  }
</style>
