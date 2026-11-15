<script lang="ts">
  // The mod's panel as Panel.cs lays it out: the head, the toolbar with the effort row (and, while
  // auto-play runs, its speed and stops), the strip with V's chance to win, then an answer or a message, and the AI's
  // recent actions. The three answers are real decisions of the book's example seed, from one play of each champion file
  // (src/data/book/panel-examples.json, whose texts were made as Describe and L10n make them). The mod's own words below
  // are L10n.cs's, in the page's language.
  import data from '../../data/book/panel-examples.json';
  import type { Lang } from '../v/names.ts';
  import { w, say } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();

  type Mode = 'sl_combat' | 'nosl_combat' | 'map' | 'message';
  let mode = $state<Mode>('sl_combat');
  let considered = $state<'off' | 'values' | 'bars'>('bars');
  let auto = $state(false);
  let message = $state(0);

  // L10n.cs: the mod's own words, English → Simplified, Traditional Chinese.
  const L: Record<string, [string, string]> = {
    Auto: ['自动', '自動'],
    Pause: ['暂停', '暫停'],
    'Play one': ['打一张', '打一張'],
    Step: ['走一步', '走一步'],
    'Pick for me': ['帮我选', '幫我選'],
    'Finish turn': ['打完回合', '打完回合'],
    'Next room': ['打完下一房', '打完下一房'],
    'Finish room': ['走完房间', '走完房間'],
    'Your move': ['等你操作', '等你操作'],
    'Auto on': ['自动中', '自動中'],
    Effort: ['思考深度', '思考深度'],
    Brain: ['大脑默认', '大腦預設'],
    Low: ['低', '低'],
    Medium: ['中', '中'],
    High: ['高', '高'],
    Max: ['最高', '最高'],
    Speed: ['速度', '速度'],
    Slow: ['慢', '慢'],
    Fast: ['快', '快'],
    'Stop at': ['停在', '停在'],
    'elites, bosses': ['精英、Boss 前', '菁英、頭目前'],
    'win chance -3': ['胜率掉 3 点', '勝率掉 3 點'],
    'Win chance': ['本局胜率', '本局勝率'],
    Next: ['下一步', '下一步'],
    'Route to the boss': ['到 Boss 的路线', '到頭目的路線'],
    Considered: ['候选', '候選'],
    win: ['胜率', '勝率'],
    'HP left': ['剩 HP', '剩 HP'],
    diff: ['差', '差'],
    'value ±CI': ['分数 ±CI', '分數 ±CI'],
    Recent: ['刚才做了', '剛才做了'],
    starting: ['启动中', '啟動中'],
    offline: ['离线', '離線'],
    ready: ['就绪', '就緒'],
    off: ['关闭', '關閉'],
    'off for this run': ['本局关闭', '本局關閉'],
    'run sent': ['已发送本局', '已送出本局'],
    'Connecting to the brain': ['正在连接大脑', '正在連接大腦'],
    'No brain running': ['大脑没有在运行', '大腦沒有在執行'],
    '{0} · trying again every 2 s': ['{0} · 每 2 秒重试', '{0} · 每 2 秒重試'],
    'Waiting for the brain': ['等待大脑', '等待大腦'],
    'it loads the champion for this run': ['正在为本局载入冠军', '正在為本局載入冠軍'],
    'Waiting for a decision': ['等待下一个决定', '等待下一個決定'],
    'open the chest': ['打开宝箱', '打開寶箱'],
    'No SL clone of this room': ['这个房间没有 SL 副本', '這個房間沒有 SL 複本'],
    '{0} · the next room has one': ['{0} · 下个房间会有', '{0} · 下個房間會有'],
    'Thinking…': ['思考中…', '思考中…'],
    'Brain error': ['大脑出错', '大腦出錯'],
    '{0} · back on at the next run': ['{0} · 下一局恢复', '{0} · 下一局恢復'],
    'Off in multiplayer': ['多人模式下关闭', '多人模式下關閉'],
  };
  const T = (en: string, ...args: (string | number)[]) => {
    let line = lang === 'en' ? en : L[en][lang === 'zh-CN' ? 0 : 1];
    args.forEach((a, i) => (line = line.replace(`{${i}}`, String(a))));
    return line;
  };
  const up = (s: string) => s.toUpperCase();

  type Text = Record<Lang, string>;
  type Option = { name: Text; value: number; ci: number; win: number | null; hp: number | null; rooms: (string | null)[] | null };
  type Answer = {
    source: { champion: string; decision: number; env: string };
    status: Text;
    floor: number;
    surface: 'combat' | 'map' | 'other';
    next: Text;
    route: boolean;
    sub: Text | null;
    then: Text[];
    later: Text | null;
    kind: 'fight' | 'run' | 'score';
    options: Option[];
    brain: string;
    seconds: number;
    win: number | null;
    delta: number | null;
    recent: Text[];
  };
  const ANSWERS = { sl_combat: data.sl_combat, nosl_combat: data.nosl_combat, map: data.map_options } as unknown as Record<Exclude<Mode, 'message'>, Answer>;

  const MESSAGES: { status: string; text: string; sub?: [string, string]; tone?: 'warn' | 'error'; env?: boolean; drive?: boolean; champion?: boolean }[] = [
    { status: 'starting', text: 'Connecting to the brain', sub: ['', '…/mods/Sts2Ai/brain/linux-x64/Sts2Ai.SimWorker'], env: true },
    { status: 'offline', text: 'No brain running', sub: ['{0} · trying again every 2 s', 'the brain ended (exit code 134): … (log: ~/.local/state/sts2ai/brain.log)'], tone: 'warn', env: true },
    { status: 'run sent', text: 'Waiting for the brain', sub: ['it loads the champion for this run', ''], env: true },
    { status: 'line', text: 'Waiting for a decision', sub: ['open the chest', ''], env: true, drive: true, champion: true },
    { status: 'line', text: 'No SL clone of this room', sub: ['{0} · the next room has one', 'a potion thrown away in combat'], tone: 'warn', env: true, drive: true, champion: true },
    { status: 'line', text: 'Thinking…', sub: ['', '3.4 s'], env: true, drive: true, champion: true },
    { status: 'off for this run', text: 'Brain error', sub: ['{0} · back on at the next run', 'an SL decision before any node checkpoint'], tone: 'error', env: true, champion: true },
    { status: 'off', text: 'Off in multiplayer', env: false },
  ];
  const msg = $derived(MESSAGES[message]);
  const msgSub = (m: (typeof MESSAGES)[number]) => (!m.sub ? null : m.sub[0] === '' ? m.sub[1] : m.sub[1] === '' ? T(m.sub[0]) : T(m.sub[0], m.sub[1]));

  // A message borrows the SL combat's run for its champion, status line and strip.
  const a = $derived(mode === 'message' ? null : ANSWERS[mode]);
  const base = ANSWERS.sl_combat;
  const champion = $derived(a ? a.source.champion : msg.champion ? base.source.champion : '—');
  const status = $derived(a ? a.status[lang] : msg.status === 'line' ? base.status[lang] : T(msg.status));
  const env = $derived(a ? a.source.env : msg.env ? 'SL' : null);
  const drive = $derived(a !== null || !!msg.drive);
  const run = $derived(a ?? base);
  const surface = $derived(a ? a.surface : 'combat');
  const stepLabel = $derived(surface === 'combat' ? T('Play one') : surface === 'map' ? T('Step') : T('Pick for me'));
  const finishLabel = $derived(surface === 'combat' ? T('Finish turn') : surface === 'map' ? T('Next room') : T('Finish room'));

  const pct = (p: number, whole = false) => (p * 100).toFixed(whole ? 0 : 1) + '%';
  const moreHp = (base.options[0].hp ?? 0) - (base.options[1].hp ?? 0);

  // Panel.Options: the shown value, and one scale for the rows' bars.
  const BAR = 56;
  const shownOf = (o: Option) => (a?.kind === 'fight' ? o.win ?? 0 : o.value);
  const scale = $derived.by(() => {
    if (!a || a.options.length === 0) return { lo: 0, hi: 1, best: 0 };
    const vals = a.options.map(shownOf);
    let lo = Math.min(...a.options.map((o) => shownOf(o) - o.ci));
    let hi = Math.max(...a.options.map((o) => shownOf(o) + o.ci));
    const pad = Math.max((hi - lo) * 0.15, a.kind === 'score' ? 1e-6 : 0.005);
    lo -= pad;
    hi = a.kind === 'fight' ? Math.max(hi, 1) : hi + pad;
    return { lo, hi, best: Math.max(...vals) };
  });
  const at = (v: number) => Math.min(Math.max(((v - scale.lo) / (scale.hi - scale.lo)) * BAR, 0), BAR);
  const heads = $derived(a?.kind === 'fight' ? [T('win'), T('HP left')] : a?.kind === 'run' ? [T('win'), T('diff')] : [T('value ±CI'), '']);
  function cells(o: Option, i: number): [string, string] {
    if (a?.kind === 'fight') return [pct(o.win ?? 0, true), (o.win ?? 0) > 0 && o.hp !== null ? o.hp.toFixed(0) : '—'];
    if (a?.kind === 'run') return [pct(o.value), i === 0 || o.value >= scale.best ? '—' : '-' + ((scale.best - o.value) * 100).toFixed(1)];
    return [`${o.value.toFixed(3)} ±${o.ci.toFixed(3)}`, ''];
  }

  // Panel.Chip: a room of a route, in its type's color, with its legend name's first letter.
  const LEGEND: Record<Lang, Record<string, string>> = {
    en: { Boss: 'Boss', Elite: 'Elite', Monster: 'Enemy', RestSite: 'Rest', Shop: 'Merchant', Treasure: 'Treasure', Unknown: 'Unknown', Ancient: 'Ancient' },
    'zh-CN': { Boss: 'Boss', Elite: '精英', Monster: '敌人', RestSite: '休息', Shop: '商人', Treasure: '宝箱', Unknown: '未知', Ancient: '先古之民' },
    'zh-TW': { Boss: '頭目', Elite: '菁英', Monster: '敵人', RestSite: '休息', Shop: '商人', Treasure: '財寶', Unknown: '未知', Ancient: '遠古之民' },
  };
  const legend = (t: string | null) => (t ? LEGEND[lang][t] ?? t.toLowerCase() : '?');
  const CHIP: Record<string, string> = { Elite: '#f5b544', RestSite: '#5fd18a', Shop: '#8ab4ff', Treasure: '#e8c56a', Boss: '#ff8a70', Unknown: '#b5abc8' };
  const chipColor = (t: string | null, first = false) => CHIP[t ?? ''] ?? (first ? '#b5abc8' : '#5a4d72');
  const chipLetter = (t: string | null) => (t === 'Unknown' ? '?' : [...legend(t)][0].toUpperCase());
  // On the map, when every option is a point with its route, each option is one row of rooms (Panel.Options).
  const routes = $derived(!!a && a.options.length > 0 && a.options.every((o) => o.rooms && o.rooms.length > 0));
  const bars = $derived(considered === 'bars' && !routes);
  const types = $derived(routes && a ? [...new Set(a.options.flatMap((o) => o.rooms ?? []))] : []);

  const EFFORTS = ['Brain', 'Low', 'Medium', 'High', 'Max'];
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.75rem;">
    <div class="v-tabs" style="margin: 0;">
      <button aria-pressed={mode === 'sl_combat'} onclick={() => (mode = 'sl_combat')}>{say(lang, w('SL combat', 'SL 战斗', 'SL 戰鬥'))}</button>
      <button aria-pressed={mode === 'nosl_combat'} onclick={() => (mode = 'nosl_combat')}>{say(lang, w('NoSL combat', 'NoSL 战斗', 'NoSL 戰鬥'))}</button>
      <button aria-pressed={mode === 'map'} onclick={() => (mode = 'map')}>{say(lang, w('Map', '地图', '地圖'))}</button>
      <button aria-pressed={mode === 'message'} onclick={() => (mode = 'message')}>{say(lang, w('A message', '一条消息', '一則訊息'))}</button>
    </div>
    {#if mode === 'message'}
      <select bind:value={message} aria-label={say(lang, w('Message', '消息', '訊息'))}>
        {#each MESSAGES as m, i (i)}<option value={i}>{T(m.text)}</option>{/each}
      </select>
    {:else}
      <label class="v-muted">{say(lang, w('Considered options', '候选项', '候選項'))}
        <select bind:value={considered}>
          <option value="off">{say(lang, w('Off', '关', '關'))}</option>
          <option value="values">{say(lang, w('Values', '数值', '數值'))}</option>
          <option value="bars">{say(lang, w('Values + bars', '数值 + 条', '數值 + 長條'))}</option>
        </select>
      </label>
    {/if}
    {#if drive}
      <label class="v-muted"><input type="checkbox" bind:checked={auto} /> {say(lang, w('Auto-play on', '自动打开着', '自動打開著'))}</label>
    {/if}
  </div>

  <div class="game">
    <div class="card">
      <div class="head">
        <span class="badge fill" style="width: 22px; height: 22px; border-radius: 6px; font-size: 11px;">AI</span>
        <div class="names">
          <div class="clip" style="font-size: 13px;">{champion}</div>
          <div class="clip muted" style="font-size: 11px;">{status}</div>
        </div>
        <div class="switch">
          <span class:on={env === 'SL'}>SL</span><span class:on={env === 'NoSL'}>NoSL</span>
        </div>
        <span class="muted" style="font-size: 11px;">F8</span>
      </div>

      {#if drive}
        <div class="toolbar">
          <span class="tool" class:on={auto}>
            {#if auto}
              <svg width="15" height="15" viewBox="0 0 18 18" aria-hidden="true"><rect x="4" y="3" width="3.5" height="12" fill="#f5b544" /><rect x="10.5" y="3" width="3.5" height="12" fill="#f5b544" /></svg>
            {:else}
              <svg width="15" height="15" viewBox="0 0 18 18" aria-hidden="true"><path d="M5 3 L5 15 L15 9 Z" fill="#5fd18a" /></svg>
            {/if}
            {auto ? T('Pause') : T('Auto')}
          </span>
          <span class="tool" class:off={auto}>
            <svg width="15" height="15" viewBox="0 0 18 18" aria-hidden="true"><path d="M3.4 10 A5.6 5.6 0 0 1 14.6 10" fill="none" stroke="#8ab4ff" stroke-width="2" /><path d="M11.5 8 L17 8 L14.25 12 Z" fill="#8ab4ff" /><circle cx="9" cy="14.5" r="1.8" fill="#8ab4ff" /></svg>
            {stepLabel}
          </span>
          <span class="tool" class:off={auto}>
            <svg width="15" height="15" viewBox="0 0 18 18" aria-hidden="true"><rect x="2" y="8" width="8" height="2" fill="#8ab4ff" /><path d="M9 4.5 L9 13.5 L13 9 Z" fill="#8ab4ff" /><rect x="14" y="3" width="2" height="12" fill="#8ab4ff" /></svg>
            {finishLabel}
          </span>
        </div>
        <div class="row" style="gap: 8px;">
          <span class="muted" style="font-size: 11px;">{T('Effort')}</span>
          <span class="toggles">{#each EFFORTS as e (e)}<span class:on={e === 'Brain'}>{T(e)}</span>{/each}</span>
        </div>
        {#if auto}
          <div class="row" style="gap: 8px;">
            <span class="muted" style="font-size: 11px;">{T('Speed')}</span>
            <span class="toggles">{#each ['Slow', 'Medium', 'Fast'] as s (s)}<span class:on={s === 'Medium'}>{T(s)}</span>{/each}</span>
          </div>
          <div class="row" style="gap: 8px; flex-wrap: wrap;">
            <span class="muted" style="font-size: 11px;">{T('Stop at')}</span>
            <span class="toggles bare"><span class="on">{T('elites, bosses')}</span><span class="on">{T('win chance -3')}</span></span>
          </div>
        {/if}

        <div class="strip">
          <div style="flex: 1;">
            {#if run.win !== null}
              <div class="muted" style="font-size: 11px;">{up(T('Win chance'))}</div>
              <div class="row" style="gap: 6px; align-items: baseline;">
                <span style="font-size: 20px;">{pct(run.win)}</span>
                {#if run.delta !== null && Math.abs(run.delta) >= 0.0005}
                  <span style="font-size: 12px; color: {run.delta > 0 ? '#5fd18a' : '#ff8a70'};">{run.delta > 0 ? '+' : '-'}{(Math.abs(run.delta) * 100).toFixed(1)}</span>
                {/if}
              </div>
            {/if}
          </div>
          <span class="pill"><span class="pilldot" style="background: {auto ? '#5fd18a' : '#f5b544'};"></span>{auto ? T('Auto on') : T('Your move')}</span>
        </div>
      {/if}

      {#if !a}
        <div class="row" style="gap: 10px;">
          <span class="dot" class:warn={msg.tone === 'warn'} class:error={msg.tone === 'error'}></span>
          <div>
            <div style="font-size: 15px;" class:warn-t={msg.tone === 'warn'} class:error-t={msg.tone === 'error'}>{T(msg.text)}</div>
            {#if msgSub(msg)}<div class="muted" style="font-size: 12px; margin-top: 4px;">{msgSub(msg)}</div>{/if}
          </div>
        </div>
      {:else}
        <div class="muted" style="font-size: 11px;">{up(T('Next'))}</div>
        <div class="row" style="gap: 10px;">
          {#if a.route}
            <span class="badge" style="width: 22px; height: 22px; border-radius: 11px; border: 3px solid #bf59ff; box-sizing: border-box;"></span>
          {:else}
            <span class="badge fill" style="width: 24px; height: 24px; border-radius: 12px; font-size: 13px;">1</span>
          {/if}
          <div>
            <div style="font-size: 17px;">{a.next[lang]}</div>
            {#if a.sub}<div class="muted" style="font-size: 12px; margin-top: 2px;">{a.sub[lang]}</div>{/if}
          </div>
        </div>
        {#if a.route}
          {#if a.then.length > 0 && !(considered !== 'off' && routes)}
            <div class="muted" style="font-size: 11px;">{up(T('Route to the boss'))}</div>
            <div style="font-size: 13px;">{a.then.map((t) => t[lang]).join(' · ')}</div>
          {/if}
        {:else}
          {#each a.then as step, i (i)}
            <div class="row" style="gap: 10px; padding-left: 2px;">
              <span class="badge ring" style="width: 20px; height: 20px; border-radius: 10px; font-size: 11px;">{i + 2}</span>
              <span style="font-size: 14px;">{step[lang]}</span>
            </div>
          {/each}
        {/if}
        {#if a.later}<div class="muted" style="font-size: 12px; padding-left: 32px;">{a.later[lang]}</div>{/if}

        {#if considered !== 'off' && a.options.length > 0}
          <div class="rule"></div>
          <div class="grid" class:bars>
            <span class="muted clip" style="font-size: 11px;">{up(T(routes ? 'Route to the boss' : 'Considered'))}</span>
            {#if bars}<span></span>{/if}
            <span class="muted num" style="font-size: 11px;">{heads[0]}</span>
            <span class="muted num" style="font-size: 11px;">{heads[1]}</span>
            {#each a.options as o, i (i)}
              {#if routes}
                <span class="rooms">
                  {#each o.rooms ?? [] as t, j (j)}
                    <span class="chip" style="border-color: {chipColor(t, i === 0)}; background: {t === 'Boss' ? '#ff8a70' : 'transparent'}; color: {t === 'Boss' ? '#14081f' : t === 'Monster' ? '#f3effa' : chipColor(t, i === 0)};">{chipLetter(t)}</span>
                  {/each}
                </span>
              {:else}
                <span class="clip" class:lead={i === 0} style="font-size: 13px;">{o.name[lang]}</span>
              {/if}
              {#if bars}
                <svg width={BAR} height="12" aria-hidden="true" style="overflow: visible;">
                  <rect x="0" y="2" width={BAR} height="8" rx="4" fill="#221b30" />
                  <rect x={at(shownOf(o) - o.ci)} y="2" width={Math.max(at(shownOf(o) + o.ci) - at(shownOf(o) - o.ci), 3)} height="8" rx="4" fill={i === 0 ? '#bf59ff' : '#5a4d72'} />
                  {#if a.kind === 'score'}<rect x={at(shownOf(o)) - 1} y="0" width="2" height="12" fill="#f3effa" />{/if}
                </svg>
              {/if}
              <span class="num" class:lead={i === 0}>{cells(o, i)[0]}</span>
              <span class="num" class:lead={i === 0 && a.kind !== 'run'} class:muted={a.kind === 'run'}>{cells(o, i)[1]}</span>
            {/each}
          </div>
          {#if routes}
            <div class="legend">
              {#each types as t (t)}
                <span class="row" style="gap: 3px;"><span class="chip" style="border-color: {chipColor(t)}; background: {t === 'Boss' ? '#ff8a70' : 'transparent'}; color: {t === 'Boss' ? '#14081f' : t === 'Monster' ? '#f3effa' : chipColor(t)};">{chipLetter(t)}</span><span class="muted" style="font-size: 11px;">{legend(t)}</span></span>
              {/each}
            </div>
          {/if}
        {/if}
        <div class="muted" style="font-size: 11px;">{a.brain} · {a.seconds.toFixed(1)} s</div>
      {/if}

      {#if drive && auto}
        <div class="muted" style="font-size: 11px;">{up(T('Recent'))}</div>
        {#each run.recent as line, i (i)}<div class="clip" style="font-size: 12px;">{line[lang]}</div>{/each}
      {/if}
    </div>
  </div>

  <p class="v-caption">
    {#if mode === 'sl_combat'}
      {say(lang, w(
        `The example run’s seed played in SL by the SL champion, decision ${base.source.decision}: the first decision of the fight on floor ${base.floor}, which the search plans to its end in ${base.seconds} s. Its kept lines began with two first moves; both win, the first with ${moreHp} HP more. The strip holds V’s chance from the map decision before the fight, and the line under the action is about this fight.`,
        `示例对局的种子，在 SL 里由 SL 冠军打的第 ${base.source.decision} 个决策：第 ${base.floor} 层那场战斗的第一个决策，搜索花 ${base.seconds} 秒把整场规划到底。它留下的打法以两个第一步开头；两个都赢，第一个多剩 ${moreHp} HP。横条里是战斗前那个地图决策的 V 胜率，动作下面那一行说的是这场战斗。`,
        `範例對局的種子，在 SL 裡由 SL 冠軍打的第 ${base.source.decision} 個決策：第 ${base.floor} 層那場戰鬥的第一個決策，搜尋花 ${base.seconds} 秒把整場規劃到底。它留下的打法以兩個第一步開頭；兩個都贏，第一個多剩 ${moreHp} HP。橫條裡是戰鬥前那個地圖決策的 V 勝率，動作下面那一行說的是這場戰鬥。`,
      ))}
    {:else if mode === 'nosl_combat'}
      {say(lang, w(
        `The same seed played in NoSL, decision ${ANSWERS.nosl_combat.source.decision}: a turn’s first decision in the act 3 boss fight on floor ${ANSWERS.nosl_combat.floor}. The five best first moves, with their mean scores over the worlds and 95% intervals; NoSL’s search gives no chance to win, so no line sits under the action.`,
        `同一个种子在 NoSL 里打的第 ${ANSWERS.nosl_combat.source.decision} 个决策：第 ${ANSWERS.nosl_combat.floor} 层第三幕 Boss 战里一个回合的第一个决策。最好的五个第一步，附各世界的平均分和 95% 区间；NoSL 的搜索不给胜率，所以动作下面没有那一行。`,
        `同一個種子在 NoSL 裡打的第 ${ANSWERS.nosl_combat.source.decision} 個決策：第 ${ANSWERS.nosl_combat.floor} 層第三幕頭目戰裡一個回合的第一個決策。最好的五個第一步，附各世界的平均分和 95% 區間；NoSL 的搜尋不給勝率，所以動作下面沒有那一行。`,
      ))}
    {:else if mode === 'map'}
      {say(lang, w(
        `Decision ${ANSWERS.map.source.decision} of the SL run, on floor ${ANSWERS.map.floor}: V gives “${ANSWERS.map.options[0].name[lang]}” ${pct(ANSWERS.map.options[0].value)} and “${ANSWERS.map.options[1].name[lang]}” ${pct(ANSWERS.map.options[1].value)}, each with the route the brain would take after it, room by room, to the boss. The strip’s change is from the decision just before. Outside combat the brain answers in a few milliseconds, which the panel prints as 0.0 s.`,
        `SL 那一局第 ${ANSWERS.map.floor} 层的第 ${ANSWERS.map.source.decision} 个决策：V 给“${ANSWERS.map.options[0].name[lang]}” ${pct(ANSWERS.map.options[0].value)}、给“${ANSWERS.map.options[1].name[lang]}” ${pct(ANSWERS.map.options[1].value)}，各附上大脑之后会走的路线，一个个房间直到 Boss。横条里的变化是相对前一个决策。战斗外大脑几毫秒就回答，面板印成 0.0 s。`,
        `SL 那一局第 ${ANSWERS.map.floor} 層的第 ${ANSWERS.map.source.decision} 個決策：V 給「${ANSWERS.map.options[0].name[lang]}」${pct(ANSWERS.map.options[0].value)}、給「${ANSWERS.map.options[1].name[lang]}」${pct(ANSWERS.map.options[1].value)}，各附上大腦之後會走的路線，一個個房間直到頭目。橫條裡的變化是相對前一個決策。戰鬥外大腦幾毫秒就回答，面板印成 0.0 s。`,
      ))}
    {:else}
      {say(lang, w(
        'The messages the panel shows instead of an answer, with their status and quieter line, as the mod writes them; the reasons in the second lines are examples, and come in English. A warning is amber, an error red. Once the brain has taken the run, the toolbar and the strip show above them.',
        '面板在没有回答时显示的消息，连同状态和较淡的第二行，照 Mod 的写法；第二行里的原因是举例，原样是英文。警告是琥珀色，错误是红色。大脑接下这一局之后，工具列和横条会显示在上面。',
        '面板在沒有回答時顯示的訊息，連同狀態和較淡的第二行，照 Mod 的寫法；第二行裡的原因是舉例，原樣是英文。警告是琥珀色，錯誤是紅色。大腦接下這一局之後，工具列和橫條會顯示在上面。',
      ))}
    {/if}
    {say(lang, w(
      ' With auto-play on, the recent actions are the four decisions before, as if the AI had played them. The words are the mod’s own as it shows them with the game in this page’s language, and the names the game’s.',
      '打开自动打时，“刚才做了”是之前的四个决策，当作是 AI 打的。文字是游戏设成本页语言时 Mod 自己显示的样子，名称是游戏的。',
      '打開自動打時，「剛才做了」是之前的四個決策，當作是 AI 打的。文字是遊戲設成本頁語言時 Mod 自己顯示的樣子，名稱是遊戲的。',
    ))}
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
    width: 340px;
    max-width: 100%;
    box-sizing: border-box;
    background: rgba(16, 12, 24, 0.92);
    border: 1px solid #2e2540;
    border-radius: 14px;
    box-shadow: 0 0 12px rgba(0, 0, 0, 0.4);
    padding: 12px 14px;
    color: #f3effa;
    display: flex;
    flex-direction: column;
    gap: 8px;
    font-family: system-ui, sans-serif;
    line-height: 1.25;
  }
  .head {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 2px;
  }
  .names {
    flex: 1;
    min-width: 0;
  }
  .clip {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    min-width: 0;
  }
  .muted {
    color: #b5abc8;
  }
  .lead {
    color: #d9a6ff;
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
  .switch,
  .toggles {
    display: inline-flex;
    gap: 2px;
    border: 1px solid #3a3050;
    border-radius: 10px;
    padding: 3px;
    font-size: 12px;
  }
  .switch span,
  .toggles span {
    display: inline-flex;
    align-items: center;
    height: 24px;
    padding: 0 8px;
    border-radius: 6px;
    color: #b5abc8;
    white-space: nowrap;
  }
  .toggles.bare {
    border-color: transparent;
    gap: 8px;
    padding: 0;
  }
  .switch span.on,
  .toggles span.on {
    background: #bf59ff;
    color: #14081f;
  }
  .toolbar {
    display: flex;
    gap: 2px;
    background: #0b0812;
    border: 1px solid #2e2540;
    border-radius: 10px;
    padding: 3px;
  }
  .tool {
    flex: 1;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 5px;
    height: 32px;
    border-radius: 7px;
    font-size: 13px;
    white-space: nowrap;
  }
  .tool.on {
    background: #2a2238;
  }
  .tool.off {
    color: #5a4d72;
  }
  .tool.off svg {
    opacity: 0.35;
  }
  .strip {
    display: flex;
    align-items: center;
    gap: 10px;
    background: #17111f;
    border-radius: 10px;
    padding: 8px;
  }
  .pill {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    background: #221b30;
    border-radius: 10px;
    padding: 6px;
    font-size: 12px;
    white-space: nowrap;
  }
  .pilldot {
    width: 8px;
    height: 8px;
    border-radius: 4px;
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
    margin-top: 6px;
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
    grid-template-columns: minmax(0, 1fr) minmax(42px, auto) minmax(42px, auto);
    gap: 5px 8px;
    align-items: center;
  }
  .grid.bars {
    grid-template-columns: minmax(0, 1fr) 56px minmax(42px, auto) minmax(42px, auto);
  }
  .num {
    font-size: 13px;
    text-align: right;
    white-space: nowrap;
  }
  .rooms {
    display: flex;
    gap: 2px;
    flex-wrap: wrap;
  }
  .chip {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 16px;
    height: 16px;
    box-sizing: border-box;
    border: 1px solid;
    border-radius: 3px;
    font-size: 9px;
    line-height: 1;
    flex: none;
  }
  .legend {
    display: flex;
    flex-wrap: wrap;
    gap: 3px 10px;
  }
</style>
