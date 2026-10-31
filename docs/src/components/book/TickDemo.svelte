<script lang="ts">
  // The mod's per-frame loop in assist mode (docs: mod/loop §2–§3), frame by frame: the rules of Assist.Tick run on
  // a made-up SL combat. A state counts once it has held for 3 frames; then the node's new steps go first, then the
  // decision. A new state while a decision is open sends cancel and waits for that answer, which is dropped. The
  // brain's answers come back after a few frames here, much sooner than in the game.
  import type { Lang } from '../v/names.ts';
  import { w, say, words, type Words } from './words.ts';

  let { lang = 'en' }: { lang?: Lang } = $props();
  const t = $derived(words(lang));

  const IDLE = 3;

  // The script: what the screen asks on each frame (null: something still moves), the steps the player takes
  // (read where the game commits them), and the frames the brain's answers arrive on.
  const SCREEN: (string | null)[] = [
    null, null, null, 'A', 'A', 'A', 'A', 'A', 'A', 'A', 'A', 'A', 'A',
    null, null, null, 'B', 'B', 'B', 'B', 'B', null, null, 'C', 'C', 'C', 'C', 'C', 'C', 'C', 'C', 'C', 'C', 'C', 'C',
  ];
  const STEP: Record<number, string> = { 13: 'combat play:1:1', 21: 'combat play:2:1' };
  const ARRIVE: Record<number, number> = { 9: 0, 26: 1, 32: 2 };

  type Out = 'checkpoint' | 'action' | 'decision' | 'cancel';
  interface Frame {
    screen: string | null;
    stable: number;
    sent: { kind: Out; text: string }[];
    got?: { id: number; shown: boolean };
    step?: string;
    panel: 'waiting' | 'held' | 'thinking' | 'answer';
    panelOf?: string;
    note: Words;
  }

  function simulate(): Frame[] {
    const frames: Frame[] = [];
    let key: string | null = null;
    let stable = 0;
    let next = 0;
    let asked: { id: number; key: string; cancelled: boolean } | null = null;
    let askedKey: string | null = null;
    let answer: { id: number; key: string } | null = null;
    let panel: Frame['panel'] = 'waiting';
    let panelOf: string | undefined;
    const steps: string[] = [];
    let sentSteps = 0;
    let checkpointSent = false;
    for (let f = 0; f < SCREEN.length; f++) {
      const sent: Frame['sent'] = [];
      let got: Frame['got'];
      let note: Words;
      if (STEP[f]) steps.push(STEP[f]);
      // The inbox first: an answer clears the open decision, and is kept only if it was not cancelled.
      const id = ARRIVE[f];
      if (id !== undefined && asked?.id === id) {
        got = { id, shown: !asked.cancelled };
        if (!asked.cancelled) answer = { id, key: asked.key };
        asked = null;
      }
      const s = SCREEN[f];
      if (s === null) {
        key = null;
        stable = 0;
        panel = 'waiting';
        panelOf = undefined;
        note = STEP[f]
          ? w(`The player acts; the hook records the step “${STEP[f]}”. Cards and enemies move: no decision on screen.`,
            `玩家动手；钩子记下这一步“${STEP[f]}”。卡牌和敌人在动：画面上没有决策。`,
            `玩家動手；鉤子記下這一步「${STEP[f]}」。卡牌和敵人在動：畫面上沒有決策。`)
          : w('Something still moves (the action queue is not empty): no decision on screen. The panel says it waits for one.',
            '还有东西在动（动作队列不空）：画面上没有决策。面板显示正在等待决策。',
            '還有東西在動（動作佇列不空）：畫面上沒有決策。面板顯示正在等待決策。');
        frames.push({ screen: s, stable, sent, got, step: STEP[f], panel, note });
        continue;
      }
      if (s !== key) {
        key = s;
        stable = 0;
      }
      stable++;
      if (stable < IDLE) {
        note = w(`State ${s} has held for ${stable} frame${stable > 1 ? 's' : ''}; it counts at ${IDLE}. The panel keeps what it showed.`,
          `状态 ${s} 已持续 ${stable} 帧；满 ${IDLE} 帧才算数。面板保持原样。`,
          `狀態 ${s} 已持續 ${stable} 幀；滿 ${IDLE} 幀才算數。面板保持原樣。`);
        frames.push({ screen: s, stable, sent, got, panel: 'held', panelOf, note });
        continue;
      }
      if (answer && answer.key === key) {
        panel = 'answer';
        panelOf = key;
        note = got?.shown
          ? w(`The answer to decision ${answer.id} arrives, for state ${key}, which still holds: the panel shows it.`,
            `决策 ${answer.id} 的回答到了，它对应的状态 ${key} 还在：面板显示它。`,
            `決策 ${answer.id} 的回答到了，它對應的狀態 ${key} 還在：面板顯示它。`)
          : w(`The answer for state ${key} stays on the panel while the state lasts.`,
            `只要状态 ${key} 还在，面板就一直显示这个回答。`,
            `只要狀態 ${key} 還在，面板就一直顯示這個回答。`);
      } else if (key === askedKey && asked) {
        panel = 'thinking';
        note = w(`Decision ${asked.id} is open for this state: the panel says “Thinking…” with the seconds so far.`,
          `这个状态的决策 ${asked.id} 还没回答：面板显示“Thinking…”和已经过的秒数。`,
          `這個狀態的決策 ${asked.id} 還沒回答：面板顯示「Thinking…」和已經過的秒數。`);
      } else if (asked) {
        panel = 'thinking';
        if (!asked.cancelled) {
          sent.push({ kind: 'cancel', text: `cancel ${asked.id}` });
          asked.cancelled = true;
          note = w(`State ${key} counts, but decision ${asked.id} is still open: the mod sends cancel and waits for its answer before it asks again.`,
            `状态 ${key} 算数了，但决策 ${asked.id} 还没回答：Mod 送出 cancel，等它的回答回来才再问。`,
            `狀態 ${key} 算數了，但決策 ${asked.id} 還沒回答：Mod 送出 cancel，等它的回答回來才再問。`);
        } else {
          note = w(`Still waiting for the answer to the cancelled decision ${asked.id}; one decision is open at a time.`,
            `仍在等已取消的决策 ${asked.id} 的回答；同一时间只有一个决策在途中。`,
            `仍在等已取消的決策 ${asked.id} 的回答；同一時間只有一個決策在途中。`);
        }
      } else {
        panel = 'thinking';
        if (!checkpointSent) {
          sent.push({ kind: 'checkpoint', text: 'node_checkpoint 0' });
          checkpointSent = true;
        }
        for (; sentSteps < steps.length; sentSteps++) sent.push({ kind: 'action', text: `node_action 0:${sentSteps}` });
        sent.push({ kind: 'decision', text: `decision ${next}` });
        asked = { id: next, key, cancelled: false };
        askedKey = key;
        next++;
        const dropped = got && !got.shown
          ? w(`The answer to the cancelled decision ${got.id} arrives and is dropped. `, `已取消的决策 ${got.id} 的回答到了，被丢掉。`, `已取消的決策 ${got.id} 的回答到了，被丟掉。`)
          : w('', '', '');
        note = w(`${dropped.en}State ${key} has held for ${IDLE} frames or more and nothing is open: in SL the node's checkpoint (once) and its new steps go first, then the decision.`,
          `${dropped['zh-CN']}状态 ${key} 已持续至少 ${IDLE} 帧，也没有决策在途中：SL 里先送节点的检查点（只送一次）和新的步骤，再送决策。`,
          `${dropped['zh-TW']}狀態 ${key} 已持續至少 ${IDLE} 幀，也沒有決策在途中：SL 裡先送節點的檢查點（只送一次）和新的步驟，再送決策。`);
      }
      frames.push({ screen: s, stable, sent, got, panel, panelOf, note });
    }
    return frames;
  }

  const FRAMES = simulate();
  let at = $state(0);
  const cur = $derived(FRAMES[at]);
  let playing = $state(false);
  $effect(() => {
    if (!playing) return;
    const timer = setInterval(() => {
      if (at >= FRAMES.length - 1) playing = false;
      else at++;
    }, 450);
    return () => clearInterval(timer);
  });

  const W = 680;
  const L = 88;
  const CW = (W - L - 6) / FRAMES.length;
  const ROW = 26;
  const LANES = [
    w('screen', '画面', '畫面'),
    w('held for', '持续帧数', '持續幀數'),
    w('to brain', '送往大脑', '送往大腦'),
    w('from brain', '大脑回答', '大腦回答'),
    w('panel', '面板', '面板'),
  ];
  const H = LANES.length * ROW + 22;
  const fill: Record<string, string> = { A: 'var(--v-s1)', B: 'var(--v-s2)', C: 'var(--v-s7)' };
  const OUT: Record<Out, string> = { checkpoint: 'var(--v-s6)', action: 'var(--v-s4)', decision: 'var(--v-s1)', cancel: 'var(--v-neg)' };
  const PANEL: Record<Frame['panel'], Words> = {
    waiting: w('Waiting for a decision', '等待决策', '等待決策'),
    held: w('(unchanged)', '（不变）', '（不變）'),
    thinking: w('Thinking…', 'Thinking…', 'Thinking…'),
    answer: w('the answer', '回答', '回答'),
  };
</script>

<div class="vfig">
  <div class="v-row" style="margin-bottom: 0.6rem;">
    <button onclick={() => (playing = !playing)}>{playing ? say(lang, w('Pause', '暂停', '暫停')) : say(lang, w('Play', '播放', '播放'))}</button>
    <button onclick={() => (at = Math.max(0, at - 1))} disabled={at === 0}>{t('back')}</button>
    <button onclick={() => (at = Math.min(FRAMES.length - 1, at + 1))} disabled={at >= FRAMES.length - 1}>{t('next')}</button>
    <input type="range" min="0" max={FRAMES.length - 1} bind:value={at} aria-label={say(lang, w('Frame', '帧', '幀'))} style="flex: 1; min-width: 8rem;" />
    <span class="v-muted v-num">{say(lang, w('frame', '第', '第'))} {at}{say(lang, w('', ' 帧', ' 幀'))}</span>
  </div>
  <svg viewBox="0 0 {W} {H}" role="img" aria-label={say(lang, w('The mod’s loop, frame by frame', 'Mod 的逐帧循环', 'Mod 的逐幀迴圈'))}>
    {#each LANES as lane, r (r)}
      <text x={L - 8} y={r * ROW + ROW / 2 + 4} text-anchor="end" class="lane">{say(lang, lane)}</text>
      <line x1={L} x2={W - 6} y1={(r + 1) * ROW} y2={(r + 1) * ROW} stroke="var(--v-grid)" />
    {/each}
    <rect x={L + at * CW} y="0" width={CW} height={LANES.length * ROW} fill="var(--v-wash)" />
    {#each FRAMES as f, i (i)}
      {@const x = L + i * CW}
      <g role="presentation" onpointerenter={() => (at = i)}>
        <rect {x} y="0" width={CW} height={LANES.length * ROW} fill="transparent" />
        <rect x={x + 1} y="5" width={CW - 2} height={ROW - 10} rx="2"
          fill={f.screen ? fill[f.screen] : 'var(--v-grid)'} opacity={f.screen && f.stable >= IDLE ? 1 : 0.45} />
        {#if f.screen}<text x={x + CW / 2} y={ROW / 2 + 4} text-anchor="middle" class="glyph">{f.screen}</text>{/if}
        {#if f.screen}<text x={x + CW / 2} y={ROW + ROW / 2 + 4} text-anchor="middle" class="num" fill={f.stable >= IDLE ? 'var(--v-ink)' : 'var(--v-muted)'}>{Math.min(f.stable, 9)}</text>{/if}
        {#each f.sent as s, k (k)}
          <rect x={x + 2} y={2 * ROW + 4 + k * 6} width={CW - 4} height="5" rx="1" fill={OUT[s.kind]} />
        {/each}
        {#if f.got}
          <path d="M{x + CW / 2 - 5},{3 * ROW + 7} L{x + CW / 2 + 5},{3 * ROW + 7} L{x + CW / 2},{3 * ROW + ROW - 7} Z"
            fill={f.got.shown ? 'var(--v-s3)' : 'var(--v-muted)'} />
        {/if}
        {#if f.step}<circle cx={x + CW / 2} cy={ROW / 2} r="3" fill="var(--v-ink)" />{/if}
        <rect x={x + 1} y={4 * ROW + 7} width={CW - 2} height={ROW - 14} rx="2"
          fill={f.panel === 'answer' ? 'var(--v-s3)' : f.panel === 'thinking' ? 'var(--v-s5)' : f.panel === 'waiting' ? 'var(--v-grid)' : 'transparent'}
          opacity={f.panel === 'held' ? 0 : 0.8} />
      </g>
    {/each}
    {#each [0, 10, 20, 30] as fr (fr)}
      <text x={L + fr * CW + CW / 2} y={H - 6} text-anchor="middle" class="tick">{fr}</text>
    {/each}
  </svg>
  <div class="legend">
    <span><i style="background: var(--v-s6)"></i>node_checkpoint</span>
    <span><i style="background: var(--v-s4)"></i>node_action</span>
    <span><i style="background: var(--v-s1)"></i>decision</span>
    <span><i style="background: var(--v-neg)"></i>cancel</span>
    <span><i style="background: var(--v-s3)"></i>{say(lang, w('answer shown', '回答已显示', '回答已顯示'))}</span>
    <span><i style="background: var(--v-muted)"></i>{say(lang, w('answer dropped', '回答被丢掉', '回答被丟掉'))}</span>
    <span><i style="background: var(--v-s5)"></i>Thinking…</span>
    <span><i class="dot"></i>{say(lang, w('the player acts', '玩家动手', '玩家動手'))}</span>
  </div>
  <div class="now">
    <div class="v-num">
      {#if cur.sent.length}<strong>{say(lang, w('sends', '送出', '送出'))}:</strong> {cur.sent.map((s) => s.text).join(', ')} · {/if}
      <strong>{say(lang, w('panel', '面板', '面板'))}:</strong>
      {cur.panel === 'answer' ? say(lang, w(`the answer for state ${cur.panelOf}`, `状态 ${cur.panelOf} 的回答`, `狀態 ${cur.panelOf} 的回答`)) : say(lang, PANEL[cur.panel])}
    </div>
    <p class="v-ink2">{say(lang, cur.note)}</p>
  </div>
  <p class="v-caption">
    {say(lang, w(
      'Assist.Tick’s rules on a made-up SL combat: states A, B and C are three observations with their legal actions, and the player plays two cards. In the game a frame is about 17 ms and an answer takes from milliseconds to seconds; here the answers come back within a few frames.',
      'Assist.Tick 的规则，套在一场虚构的 SL 战斗上：状态 A、B、C 是三个观测（连同合法动作），玩家打了两张牌。游戏里一帧约 17 ms，一个回答要几毫秒到几秒；这里的回答几帧就回来。',
      'Assist.Tick 的規則，套在一場虛構的 SL 戰鬥上：狀態 A、B、C 是三個觀測（連同合法動作），玩家打了兩張牌。遊戲裡一幀約 17 ms，一個回答要幾毫秒到幾秒；這裡的回答幾幀就回來。',
    ))}
  </p>
</div>

<style>
  svg {
    width: 100%;
    height: auto;
    display: block;
  }
  .lane {
    font-size: 11px;
    fill: var(--v-ink-2);
  }
  .glyph {
    font-size: 10px;
    font-weight: 600;
    fill: var(--v-surface);
  }
  .num {
    font-size: 10px;
  }
  .tick {
    font-size: 10px;
    fill: var(--v-muted);
  }
  .legend {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem 0.9rem;
    font-size: 0.75rem;
    margin-top: 0.4rem;
  }
  .legend i {
    display: inline-block;
    width: 0.7rem;
    height: 0.7rem;
    margin-right: 0.3rem;
    vertical-align: -0.05rem;
  }
  .legend i.dot {
    border-radius: 50%;
    background: var(--v-ink);
    width: 0.45rem;
    height: 0.45rem;
  }
  .now {
    min-height: 4.6em;
    margin-top: 0.5rem;
  }
  .now p {
    margin: 0.25rem 0 0;
  }
</style>
