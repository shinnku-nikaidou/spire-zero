<script lang="ts">
  // The lineage of every V and combat net (docs: learning/experiments §1): by the data each learned from, an arrow from
  // the brain that played that data's newest part, a solid one from the net whose weights it went on from. Data:
  // src/data/book/learning-lineage.json (the ledger, the gauntlet leaderboard, the reports and the weights files'
  // `fit`; its source block names them). Pick a net for its card.
  import type { Lang } from '../v/names.ts';
  import { fmt } from '../v/i18n.ts';
  import { w, say, type Words } from './words.ts';
  import data from '../../data/book/learning-lineage.json';

  let { lang = 'en' }: { lang?: Lang } = $props();

  interface Report {
    report: string; env: string; set: string; n: number; floor: number; se: number; won: number; commit: string;
    bosses: number[][]; elites: number; s_per_run: number;
  }
  interface Gate {
    kind: string; verdict?: string; against?: string; pairs?: number; diff?: number; diff_se?: number; lower?: number;
    all200?: number; all200_se?: number; vs?: string; not_worse?: boolean;
  }
  interface Net {
    id: string; kind: string; col: string; data: string | null; by: string | null; init?: string; planned?: boolean;
    gate: Gate; roles?: { as: string; from: string; to: string | null }[]; reports: Report[];
    gauntlet?: { progress: number; se: number; won: number; vs?: string; diff?: number; diff_se?: number };
    whole_paired?: { vs: string; diff: number; diff_se: number };
    fit?: Record<string, number | string | null>;
  }
  const nets = data.nets as unknown as Net[];
  const columns = data.columns;
  const byId = new Map(nets.map((n) => [n.id, n]));

  // Rows: the combat nets above, the V nets below, each line of champions on one row.
  const ROW: Record<string, number> = {
    LeafValue: 0, v0: 2, c2: 0, c1: 1, c3: 0, c4: 1, 'n2-b0.03': 2, 'n2-b0.1': 3, n1: 4, 'n2-b0.01': 5,
    'pure-1-g': 2, 'pure-1-d': 3, 'pure-1-b': 4, 'pure-1': 5, 'n3-free-ctl': 6, 'n3-ctl': 7,
    'v6-1': 2, 'v6-2': 2, 'v6-3-2': 2, 'v6-3-1': 3, 'v7-1': 2, 'v7-1e': 3,
  };
  const W = 760;
  const COLW = 92;
  const NODEW = 80;
  const NODEH = 22;
  const ROWH = 30;
  const TOP = 44;
  const H = TOP + 8 * ROWH + 8;
  const colX = (id: string) => 8 + columns.findIndex((c) => c.id === id) * COLW;
  const pos = (n: Net) => ({ x: colX(n.col), y: TOP + ROW[n.id] * ROWH });

  const VERDICT: Record<string, { colour: string; words: Words }> = {
    pass: { colour: 'var(--v-s3)', words: w('passed its gate', '通过晋级门槛', '通過晉級門檻') },
    pass_not_promoted: { colour: 'var(--v-s3)', words: w('passed its gate', '通过晋级门槛', '通過晉級門檻') },
    fail: { colour: 'var(--v-neg)', words: w('failed its gate', '没通过晋级门槛', '沒通過晉級門檻') },
    void: { colour: 'var(--v-s4)', words: w('void or stopped', '作废或中止', '作廢或中止') },
    stopped: { colour: 'var(--v-s4)', words: w('void or stopped', '作废或中止', '作廢或中止') },
    report: { colour: 'var(--v-s7)', words: w('judged by its report, no gate', '只看报告，没有晋级门槛', '只看報告，沒有晉級門檻') },
    first: { colour: 'var(--v-s7)', words: w('judged by its report, no gate', '只看报告，没有晋级门槛', '只看報告，沒有晉級門檻') },
    none: { colour: 'var(--v-muted)', words: w('not gated', '没有过晋级门槛', '沒有過晉級門檻') },
    planned: { colour: 'var(--v-muted)', words: w('planned', '计划中', '計畫中') },
  };
  const verdictOf = (n: Net) => (n.gate.kind === 'none' ? 'none' : (n.gate.verdict ?? 'none'));
  const isChampion = (n: Net) => (n.roles ?? []).some((r) => r.as !== 'gauntlet' && r.to === null);
  const wasChampion = (n: Net) => (n.roles ?? []).length > 0;

  const edges = nets.flatMap((n) => {
    const out: { from: Net; to: Net; init: boolean }[] = [];
    if (n.init && byId.has(n.init)) out.push({ from: byId.get(n.init)!, to: n, init: true });
    if (n.by && byId.has(n.by) && n.by !== n.init) out.push({ from: byId.get(n.by)!, to: n, init: false });
    return out;
  });
  const edgePath = (a: Net, b: Net) => {
    const p = pos(a), q = pos(b);
    const x1 = p.x + NODEW, y1 = p.y + NODEH / 2, x2 = q.x, y2 = q.y + NODEH / 2;
    const mx = (x1 + x2) / 2;
    return `M${x1},${y1} C${mx},${y1} ${mx},${y2} ${x2 - 3},${y2}`;
  };

  let picked = $state('v6-3-2');
  let hovered = $state<string | null>(null);
  const shown = $derived(byId.get(hovered ?? picked)!);
  const related = $derived(new Set([shown.id, shown.by, shown.init].filter(Boolean)));

  const KIND: Record<string, Words> = {
    v0: w('V v0: hand-written features times weights', 'V v0：手写特征乘以权重', 'V v0：手寫特徵乘以權重'),
    leaf: w('LeafValue: the hand-made combat leaf', 'LeafValue：手写的战斗叶节点', 'LeafValue：手寫的戰鬥葉節點'),
    combat: w('combat net', '战斗网络', '戰鬥網路'),
    v2: w('V v2 net (also reads V v0’s features)', 'V v2 网络（也读 V v0 的特征）', 'V v2 網路（也讀 V v0 的特徵）'),
    pure: w('pure V: ids and raw state only', '纯 V：只读 id 和原始状态', '純 V：只讀 id 和原始狀態'),
    gauntlet: w('pure V of the elite gauntlet (progress)', '精英连战的纯 V（progress）', '菁英連戰的純 V（progress）'),
    v7: w('plan v7: pure V by P(win)', '计划 v7：按 P(win) 的纯 V', '計畫 v7：按 P(win) 的純 V'),
    v7e: w('plan v7: the encoders’ net by P(win)', '计划 v7：按 P(win) 的编码器网络', '計畫 v7：按 P(win) 的編碼器網路'),
  };
  const ROLE: Record<string, Words> = {
    'SL V': w('the SL champion’s V', 'SL 冠军的 V', 'SL 冠軍的 V'),
    'NoSL V': w('the NoSL champion’s V', 'NoSL 冠军的 V', 'NoSL 冠軍的 V'),
    'SL leaf': w('the SL champion’s leaf', 'SL 冠军的叶节点', 'SL 冠軍的葉節點'),
    'NoSL leaf': w('the NoSL champion’s leaf', 'NoSL 冠军的叶节点', 'NoSL 冠軍的葉節點'),
    gauntlet: w('the elite gauntlet’s champion', '精英连战的冠军', '菁英連戰的冠軍'),
  };
  const sign = (x: number, d = 2) => (x > 0 ? '+' : x < 0 ? '−' : '') + fmt(lang, Math.abs(x), d);
  const num = (x: unknown, d = 4) => (typeof x === 'number' ? (Number.isInteger(x) ? x.toLocaleString(lang) : fmt(lang, x, d)) : '–');

  function gateText(n: Net): string {
    const g = n.gate;
    const all = g.all200 !== undefined ? say(lang, w(`; on all 200 seeds ${sign(g.all200)} ± ${fmt(lang, g.all200_se!, 2)} floors`, `；200 个种子全部：${sign(g.all200)} ± ${fmt(lang, g.all200_se!, 2)} 层`, `；200 個種子全部：${sign(g.all200)} ± ${fmt(lang, g.all200_se!, 2)} 層`)) : '';
    if (g.kind === 'none') return say(lang, w('Not gated.', '没有过晋级门槛。', '沒有過晉級門檻。'));
    if (n.id === 'LeafValue') return say(lang, w(`PIMC with it passed the SPRT gate against the play placeholder after ${g.pairs} pairs, ${sign(g.diff!)} floors.`, `用它的 PIMC 通过对 play 占位策略的 SPRT 晋级门槛，${g.pairs} 对后 ${sign(g.diff!)} 层。`, `用它的 PIMC 通過對 play 佔位策略的 SPRT 晉級門檻，${g.pairs} 對後 ${sign(g.diff!)} 層。`));
    if (g.kind === 'sprt') {
      if (g.verdict === 'pass') return say(lang, w(`Passed the simulator’s SPRT gate against ${g.against} after ${g.pairs} pairs, ${sign(g.diff!)} floors`, `通过模拟器的 SPRT 晋级门槛（对手 ${g.against}），${g.pairs} 对后 ${sign(g.diff!)} 层`, `通過模擬器的 SPRT 晉級門檻（對手 ${g.against}），${g.pairs} 對後 ${sign(g.diff!)} 層`)) + all + '.';
      if (g.verdict === 'fail') return say(lang, w(`Failed the SPRT gate against ${g.against}${g.not_worse ? ' (non-inferiority, δ 1.5 floors)' : ''} after ${g.pairs} pairs${g.diff !== undefined ? `, ${sign(g.diff)} floors` : ''}`, `没通过 SPRT 晋级门槛（对手 ${g.against}${g.not_worse ? '，非劣性检验，δ 1.5 层' : ''}），${g.pairs} 对后${g.diff !== undefined ? ` ${sign(g.diff)} 层` : ''}`, `沒通過 SPRT 晉級門檻（對手 ${g.against}${g.not_worse ? '，非劣性檢定，δ 1.5 層' : ''}），${g.pairs} 對後${g.diff !== undefined ? ` ${sign(g.diff)} 層` : ''}`)) + all + '.';
      if (g.verdict === 'void') return say(lang, w(`Void at ${g.pairs} of 200 pairs against ${g.against} (${sign(g.diff!)} floors, open): a run was truncated by a harness bug since fixed.`, `对 ${g.against} 的晋级门槛在 200 对中的第 ${g.pairs} 对作废（${sign(g.diff!)} 层，未决）：一局因 harness 的 bug 被截断，已修复。`, `對 ${g.against} 的晉級門檻在 200 對中的第 ${g.pairs} 對作廢（${sign(g.diff!)} 層，未決）：一局因 harness 的 bug 被截斷，已修復。`));
      return say(lang, w('Its gate was stopped halfway.', '它的晋级门槛中途停止。', '它的晉級門檻中途停止。'));
    }
    if (g.kind === 'report') {
      const vs = g.vs === 'random' ? say(lang, w('random macro decisions', '随机宏观决策', '隨機宏觀決策')) : g.vs;
      const diff = g.diff !== undefined ? say(lang, w(` Paired ${sign(g.diff)} ± ${fmt(lang, g.diff_se!, 2)} floors against ${vs}.`, ` 与 ${vs} 配对：${sign(g.diff)} ± ${fmt(lang, g.diff_se!, 2)} 层。`, ` 與 ${vs} 配對：${sign(g.diff)} ± ${fmt(lang, g.diff_se!, 2)} 層。`)) : '';
      if (n.id === 'v0') return say(lang, w('The first champions’ V, set by hand; no gate.', '最早几任冠军的 V，手工设定；没有晋级门槛。', '最早幾任冠軍的 V，手工設定；沒有晉級門檻。'));
      if (g.verdict === 'first') return say(lang, w('Set by its report with no gate, the first of a new line.', '凭报告设定，没有晋级门槛，是新一条路线的第一个。', '憑報告設定，沒有晉級門檻，是新一條路線的第一個。')) + diff;
      return say(lang, w('Judged by its report; not gated.', '只看报告；没有过晋级门槛。', '只看報告；沒有過晉級門檻。')) + diff;
    }
    if (g.kind === 'gauntlet') {
      const base = say(lang, w(`Passed the elite gauntlet’s gate against ${g.against}: ${sign(g.diff!)} ± ${fmt(lang, g.diff_se!, 2)} progress, 95% lower bound ${sign(g.lower!)}.`, `通过精英连战对 ${g.against} 的晋级门槛：进度 ${sign(g.diff!)} ± ${fmt(lang, g.diff_se!, 2)}，95% 下界 ${sign(g.lower!)}。`, `通過菁英連戰對 ${g.against} 的晉級門檻：進度 ${sign(g.diff!)} ± ${fmt(lang, g.diff_se!, 2)}，95% 下界 ${sign(g.lower!)}。`));
      return g.verdict === 'pass_not_promoted' ? base + say(lang, w(' Not promoted: v6-3-2’s mean was higher.', ' 没有晋升：v6-3-2 的平均更高。', ' 沒有晉升：v6-3-2 的平均更高。')) : base;
    }
    return say(lang, w(`Planned: the gate on paired wins, on promotion-1-500, against ${g.against}.`, `计划中：在 promotion-1-500 上与 ${g.against} 配对比胜场的晋级门槛。`, `計畫中：在 promotion-1-500 上與 ${g.against} 配對比勝場的晉級門檻。`));
  }
</script>

<div class="vfig" style="position: relative;">
  <div class="v-scroll">
    <svg viewBox="0 0 {W} {H}" width="100%" style="min-width: 560px;" role="img" aria-label="lineage">
      {#each columns as c, i (c.id)}
        <text x={8 + i * COLW} y={14} style="font-weight: 600; fill: var(--v-ink);">{c.data === 'built in' ? say(lang, w('built in', '内建', '內建')) : c.data}</text>
        <text x={8 + i * COLW} y={28}>{c.date}</text>
      {/each}
      <line x1={8} x2={W - 8} y1={TOP + 1.5 * ROWH + NODEH / 2 + 2} y2={TOP + 1.5 * ROWH + NODEH / 2 + 2} stroke="var(--v-grid)" stroke-dasharray="2 4" />
      {#each edges as e (e.from.id + '>' + e.to.id)}
        <path d={edgePath(e.from, e.to)} fill="none" stroke={related.has(e.to.id) && related.has(e.from.id) ? 'var(--v-ink-2)' : 'var(--v-axis)'} stroke-width={e.init ? 2 : 1.2} stroke-dasharray={e.init ? null : '3 3'} />
      {/each}
      {#each nets as n (n.id)}
        {@const p = pos(n)}
        {@const v = VERDICT[verdictOf(n)]}
        <g role="button" tabindex="0" aria-label={n.id} style="cursor: pointer;" onclick={() => (picked = n.id)} onkeydown={(e) => (e.key === 'Enter' || e.key === ' ') && (picked = n.id)} onpointerenter={() => (hovered = n.id)} onpointerleave={() => (hovered = null)}>
          <rect x={p.x} y={p.y} width={NODEW} height={NODEH} rx="4" fill={shown.id === n.id ? 'var(--v-wash)' : 'var(--v-surface)'} stroke={v.colour} stroke-width={isChampion(n) ? 3 : 1.5} stroke-dasharray={n.planned ? '4 3' : null} />
          <text x={p.x + NODEW / 2} y={p.y + 15} text-anchor="middle" style="fill: var(--v-ink); font-weight: {wasChampion(n) ? 600 : 400};">{n.id}</text>
        </g>
      {/each}
    </svg>
  </div>
  <div class="v-row" style="margin-top: 0.25rem;">
    {#each ['pass', 'fail', 'void', 'report', 'none'] as k (k)}
      <span><span class="v-key" style="background: {VERDICT[k].colour};"></span> {say(lang, VERDICT[k].words)}</span>
    {/each}
    <span class="v-muted">{say(lang, w('thick: a champion now; bold: a champion once; dashed box: planned; dashed arrow: played its data; solid: went on from its weights', '粗框：现任冠军；粗体：当过冠军；虚线框：计划中；虚线箭头：数据由它打出；实线：接着它的权重训练', '粗框：現任冠軍；粗體：當過冠軍；虛線框：計畫中；虛線箭頭：資料由它打出；實線：接著它的權重訓練'))}</span>
  </div>

  <div style="margin-top: 0.75rem; border-top: 1px solid var(--v-grid); padding-top: 0.5rem;">
    <p style="margin: 0;"><strong style="font-size: 1rem;">{shown.id}</strong> · {say(lang, KIND[shown.kind])}</p>
    <p style="margin: 0.25rem 0 0;">
      {#if shown.data}
        {say(lang, w('Learned from', '训练数据', '訓練資料'))}: {shown.data}{#if shown.by}; {say(lang, w('newest data played by', '最新数据由', '最新資料由'))} {shown.by}{say(lang, w('', ' 打出', ' 打出'))}{/if}{#if shown.init}; {say(lang, w('went on from', '接着训练自', '接著訓練自'))} {shown.init}{/if}.
      {:else}
        {say(lang, w('Not learned: set by hand.', '不是学来的：手工设定。', '不是學來的：手工設定。'))}
      {/if}
    </p>
    {#if shown.fit}
      {@const f = shown.fit}
      <p class="v-num" style="margin: 0.25rem 0 0;">
        {#if shown.kind === 'combat'}
          {say(lang, w('Training report', '训练报告', '訓練報告'))}: {num(f.runs)} {say(lang, w('runs', '局', '局'))}, {num(f.samples)} {say(lang, w('round starts', '个回合开始', '個回合開始'))} ({num(f.held_out_runs)} {say(lang, w('runs held out', '局留出', '局留出'))}); {say(lang, w('held-out cross-entropy', '留出交叉熵', '留出交叉熵'))} {num(f.held_out_ce)} ({say(lang, w('prior', '先验', '先驗'))} {num(f.held_out_ce_prior)}); {say(lang, w('value’s correlation with the ending', '价值与结局的相关', '價值與結局的相關'))} {num(f.value_corr, 3)} (LeafValue {num(f.value_corr_leaf0, 3)}); {num(f.params)} {say(lang, w('weights', '个权重', '個權重'))}.
        {:else}
          {say(lang, w('Training report', '训练报告', '訓練報告'))}: {num(f.runs)} {say(lang, w('runs', '局', '局'))}, {num(f.samples)} {say(lang, w('decisions', '个决策', '個決策'))} ({num(f.held_out_runs)} {say(lang, w('runs held out', '局留出', '局留出'))}{#if f.won}; {num(f.won)} {say(lang, w('won', '局获胜', '局獲勝'))}{/if}); {say(lang, w('held-out loss', '留出损失', '留出損失'))} {num(f.held_out_loss)} ({say(lang, w('baseline', '基线', '基線'))} {num(f.held_out_loss_floor_only)}), R² {num(f.r2_held_out, 3)} ({num(f.r2_held_out_floor_only, 3)}); objective {f.objective}; {num(f.params)} {say(lang, w('weights', '个权重', '個權重'))}.
        {/if}
      </p>
    {/if}
    <p style="margin: 0.25rem 0 0;">
      <span class="v-key" style="background: {VERDICT[verdictOf(shown)].colour};"></span> {gateText(shown)}
    </p>
    {#if shown.gauntlet}
      {@const g = shown.gauntlet}
      <p class="v-num" style="margin: 0.25rem 0 0;">
        {say(lang, w('Elite gauntlet, 500 evaluation seeds', '精英连战，500 个评估种子', '菁英連戰，500 個評估種子'))}: {say(lang, w('progress', '进度', '進度'))} {fmt(lang, g.progress, 2)} ± {fmt(lang, g.se, 2)}, {g.won} {say(lang, w('won', '局获胜', '局獲勝'))}{#if g.vs}; {sign(g.diff ?? 0)} ± {fmt(lang, g.diff_se ?? 0, 2)} {say(lang, w('against', '对', '對'))} {g.vs}{/if}.
      </p>
    {/if}
    {#each shown.reports as r (r.report)}
      <p class="v-num" style="margin: 0.25rem 0 0;">
        {r.env} {r.set}: {say(lang, w('floor', '楼层', '樓層'))} {fmt(lang, r.floor, 2)} ± {fmt(lang, r.se, 2)}, {say(lang, w('won', '获胜', '獲勝'))} {r.won}/{r.n}; {say(lang, w('act bosses beaten of reached', '各幕 Boss 击败/到达', '各幕頭目擊敗/到達'))} {r.bosses.map((b) => `${b[1]}/${b[0]}`).join(', ')}; {fmt(lang, r.elites, 2)} {say(lang, w('elites a run', '场精英/局', '場菁英/局'))}; {fmt(lang, r.s_per_run, 0)} s/{say(lang, w('run', '局', '局'))}
        <br /><span class="v-muted">{r.report} · {r.commit}</span>
      </p>
    {/each}
    {#if shown.whole_paired}
      <p class="v-num" style="margin: 0.25rem 0 0;">{say(lang, w('Whole runs, paired against', '整局，配对于', '整局，配對於'))} {shown.whole_paired.vs}: {sign(shown.whole_paired.diff)} ± {fmt(lang, shown.whole_paired.diff_se, 2)} {say(lang, w('floors', '层', '層'))}.</p>
    {/if}
    {#if shown.roles?.length}
      <p style="margin: 0.25rem 0 0;">
        {say(lang, w('Played as', '担任', '擔任'))} {#each shown.roles as r, i (i)}{i ? '; ' : ''}{say(lang, ROLE[r.as])} {r.from} → {r.to ?? say(lang, w('now', '至今', '至今'))}{/each}.
      </p>
    {/if}
  </div>
  <p class="v-caption">
    {say(lang, w(
      'Sources: eval/silent/ledger.md and gauntlet-leaderboard.md (data, gates, roles), the reports in eval/silent/reports/ (floor mean and standard error over finished runs, wins, bosses, elites, time), and each weights file’s fit (training report; weights counted from its ONNX graph). Whole-run reports are 200 runs on promotion-1 or 300 on promotion-1-300; V v0’s and LeafValue’s are of the champions they played in.',
      '来源：eval/silent/ledger.md 和 gauntlet-leaderboard.md（数据、晋级门槛、角色），eval/silent/reports/ 里的报告（已完成各局的平均楼层及其标准误、胜场、Boss、精英、耗时），以及每个权重文件的 fit（训练报告；权重数从其 ONNX 图数出）。整局报告是 promotion-1 上的 200 局或 promotion-1-300 上的 300 局；V v0 和 LeafValue 的报告是它们所在冠军的报告。',
      '來源：eval/silent/ledger.md 和 gauntlet-leaderboard.md（資料、晉級門檻、角色），eval/silent/reports/ 裡的報告（已完成各局的平均樓層及其標準誤、勝場、頭目、菁英、耗時），以及每個權重檔的 fit（訓練報告；權重數從其 ONNX 圖數出）。整局報告是 promotion-1 上的 200 局或 promotion-1-300 上的 300 局；V v0 和 LeafValue 的報告是它們所在冠軍的報告。',
    ))}
  </p>
</div>
