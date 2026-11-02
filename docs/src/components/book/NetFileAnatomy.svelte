<script lang="ts">
  // The two champions' net files field by field (docs: learning/reference §7): weights/silent/v8-3.json (V) and
  // c8-3.json (the combat net), as src/data/book/learning-netfiles.json extracted them: each field's type and size, the
  // token tables, the state scaling, the training report, the ONNX graph's inputs, outputs, initializer shapes and
  // operators, and the test inputs. No weights.
  import type { Lang } from '../v/names.ts';
  import { fmt } from '../v/i18n.ts';
  import { w, say, type Words } from './words.ts';
  import data from '../../data/book/learning-netfiles.json';

  let { lang = 'en' }: { lang?: Lang } = $props();

  interface Tensor { name: string; dtype: string; shape: (string | number)[] }
  interface Field {
    key: string; type: string; value?: unknown; size?: number;
    tables?: Record<string, { size: number; first: string[] }>;
    names?: string[]; mean?: number[]; std?: number[];
    values?: unknown;
    onnx?: { opset: number; producer: string; inputs: Tensor[]; outputs: Tensor[]; initializers: { name: string; shape: number[] }[]; params: number; ops: Record<string, number> };
    entry?: Record<string, unknown>;
  }
  interface NetDoc { file: string; bytes: number; fields: Field[] }
  const FILES: Record<string, NetDoc> = { "v8-3": data["v8-3"] as unknown as NetDoc, "c8-3": data["c8-3"] as unknown as NetDoc };
  let which = $state('v8-3');
  const entries = (v: unknown) => Object.entries((v ?? {}) as Record<string, unknown>);
  const list = (v: unknown) => (Array.isArray(v) ? '[' + v.join(', ') + ']' : String(v));
  const numbers = (v: unknown) => ((v ?? []) as number[]).map((x) => fmt(lang, x, 2)).join(', ');
  const at = (v: number[] | undefined, i: number) => (v ? v[i] : '');
  const file = $derived(FILES[which]);

  const NOTE: Record<string, Words> = {
    name: w('The net’s name, as the champion shows it (l0 v8-3, plan c8-3).', '网络的名字，冠军名称里就这样显示（l0 v8-3、plan c8-3）。', '網路的名字，冠軍名稱裡就這樣顯示（l0 v8-3、plan c8-3）。'),
    character: w('The character whose runs trained it. C# does not read it.', '训练它的那些局所用的角色。C# 不读。', '訓練它的那些局所用的角色。C# 不讀。'),
    objective: w('What RunNet.Value returns: floor, win, or progress (a net with a share head).', 'RunNet.Value 返回什么：floor、win，或 progress（带比例输出头的网络）。', 'RunNet.Value 回傳什麼：floor、win，或 progress（帶比例輸出頭的網路）。'),
    floors: w('The hazard head’s floors, from floor 0.', '风险值输出头覆盖的楼层数，从第 0 层起。', '風險值輸出頭涵蓋的樓層數，從第 0 層起。'),
    vocab: w('Each token table’s ids, every id of the game build; entry 0 is <unk>, which reads any other id.', '每张 token 表的 id，即游戏这个版本的全部 id；第 0 项是 <unk>，读其他任何 id。', '每張 token 表的 id，即遊戲這個版本的全部 id；第 0 項是 <unk>，讀其他任何 id。'),
    state: w('The six raw numbers V reads, with the training data’s mean and standard deviation that standardize them.', 'V 读的六个原始数字，以及用来标准化它们的训练数据均值和标准差。', 'V 讀的六個原始數字，以及用來標準化它們的訓練資料平均值和標準差。'),
    fit: w('The trainer’s report, kept in the file. C# does not read it.', '训练器的报告，存在文件里。C# 不读。', '訓練器的報告，存在檔案裡。C# 不讀。'),
    onnx: w('The net itself: an ONNX graph, base64. C# feeds it by input name.', '网络本身：ONNX 图，base64 编码。C# 按输入名称喂数据。', '網路本身：ONNX 圖，base64 編碼。C# 按輸入名稱餵資料。'),
    tests: w('Held-out inputs with the trainer’s outputs; loading the file computes them again and refuses it if they differ.', '留出的输入及训练器给出的输出；载入文件时重新计算，不一致就拒绝。', '留出的輸入及訓練器給出的輸出；載入檔案時重新計算，不一致就拒絕。'),
    buckets: w('Buckets of the HP share still to be lost after a win.', '获胜后还会损失的 HP 比例分成几个桶。', '獲勝後還會損失的 HP 比例分成幾個桶。'),
    loss_min: w('The lowest bucket’s bottom: −0.25, a fight that heals.', '最低一个桶的下限：−0.25，即会回血的战斗。', '最低一個桶的下限：−0.25，即會回血的戰鬥。'),
    lags: w('How many earlier rounds of each enemy’s intents it reads.', '读每个敌人之前几个回合的意图。', '讀每個敵人之前幾個回合的意圖。'),
  };
  // The JSON types, as the extraction's Python names them.
  const TYPE: Record<string, string> = { str: 'string', int: 'number', float: 'number', bool: 'boolean' };
  const kb = (n: number) => (n >= 1e6 ? `${fmt(lang, n / 1e6, 2)} MB` : `${fmt(lang, n / 1e3, 1)} KB`);
  const shape = (s: (string | number)[]) => `[${s.join(', ')}]`;
  const compact = (v: unknown) => (typeof v === 'number' ? (Number.isInteger(v) ? v.toLocaleString(lang) : String(v)) : JSON.stringify(v));
  const summary = (f: Field) => {
    if (f.key === 'onnx') return `${kb(f.size!)} base64 · ${f.onnx!.inputs.length} ${say(lang, w('inputs', '个输入', '個輸入'))} · ${f.onnx!.params.toLocaleString(lang)} ${say(lang, w('weights', '个权重', '個權重'))}`;
    if (f.tables) return `${Object.keys(f.tables).length} ${say(lang, w('tables', '张表', '張表'))}`;
    if (f.names) return f.names.join(', ');
    if (f.key === 'fit') return `${entries(f.values).length} ${say(lang, w('fields', '个字段', '個欄位'))}`;
    if (f.key === 'tests') return `${f.size} ${say(lang, w('entries', '项', '項'))}`;
    return JSON.stringify(f.value);
  };
</script>

<div class="vfig">
  <div class="v-tabs" role="group">
    {#each Object.keys(FILES) as k (k)}
      <button aria-pressed={which === k} onclick={() => (which = k)}>{k}.json</button>
    {/each}
  </div>
  <p class="v-muted" style="margin: 0 0 0.5rem;">{file.file} · {kb(file.bytes)}</p>
  {#each file.fields as f (which + f.key)}
    <details style="border-top: 1px solid var(--v-grid); padding: 0.3rem 0;">
      <summary style="cursor: pointer;">
        <code>{f.key}</code> <span class="v-muted">{TYPE[f.type] ?? f.type}</span> · <span class="v-num">{summary(f)}</span>
        <br /><span class="v-ink2" style="font-size: 0.8125rem; font-weight: 400;">{NOTE[f.key] ? say(lang, NOTE[f.key]) : ''}</span>
      </summary>
      <div class="v-scroll" style="margin: 0.4rem 0 0.2rem 1rem;">
        {#if f.tables}
          <table>
            <thead><tr><th>{say(lang, w('table', '表', '表'))}</th><th>{say(lang, w('ids', 'id 数', 'id 數'))}</th><th>{say(lang, w('first after <unk>', '<unk> 之后的前几个', '<unk> 之後的前幾個'))}</th></tr></thead>
            <tbody>
              {#each Object.entries(f.tables) as [t, v] (t)}
                <tr><td><code>{t}</code></td><td class="v-num">{v.size}</td><td><code>{v.first.join(', ')}</code></td></tr>
              {/each}
            </tbody>
          </table>
        {:else if f.names}
          <table>
            <thead><tr><th>{say(lang, w('number', '数字', '數字'))}</th><th>{say(lang, w('mean', '均值', '平均值'))}</th><th>{say(lang, w('std', '标准差', '標準差'))}</th></tr></thead>
            <tbody>
              {#each f.names as n, i (n)}
                <tr><td><code>{n}</code></td><td class="v-num">{at(f.mean, i)}</td><td class="v-num">{at(f.std, i)}</td></tr>
              {/each}
            </tbody>
          </table>
          <p class="v-muted" style="margin: 0.25rem 0 0;">{say(lang, w('No run is at an ascension above 0 in the data, so its standard deviation reads as 1.', '数据里没有进阶高于 0 的局，所以它的标准差读作 1。', '資料裡沒有晉階高於 0 的局，所以它的標準差讀作 1。'))}</p>
        {:else if f.key === 'fit'}
          <table>
            <tbody>
              {#each entries(f.values) as [k, v] (k)}
                <tr><td><code>{k}</code></td><td class="v-num" style="word-break: break-all;">{compact(v)}</td></tr>
              {/each}
            </tbody>
          </table>
        {:else if f.onnx}
          {@const o = f.onnx}
          <p style="margin: 0 0 0.3rem;">opset {o.opset} · {o.producer} · {o.initializers.length} {say(lang, w('initializers', '个初始值张量', '個初始值張量'))}, {o.params.toLocaleString(lang)} {say(lang, w('numbers', '个数', '個數'))}</p>
          <table>
            <thead><tr><th>{say(lang, w('input', '输入', '輸入'))}</th><th>dtype</th><th>{say(lang, w('shape', '形状', '形狀'))}</th></tr></thead>
            <tbody>
              {#each o.inputs as t (t.name)}
                <tr><td><code>{t.name}</code></td><td>{t.dtype}</td><td class="v-num">{shape(t.shape)}</td></tr>
              {/each}
              {#each o.outputs as t (t.name)}
                <tr><td><code>{t.name}</code> <span class="v-muted">{say(lang, w('(output)', '（输出）', '（輸出）'))}</span></td><td>{t.dtype}</td><td class="v-num">{shape(t.shape)}</td></tr>
              {/each}
            </tbody>
          </table>
          <details style="margin-top: 0.4rem;">
            <summary style="cursor: pointer;">{say(lang, w('Initializers: the weight tensors, by shape', '初始值张量：权重张量，只列形状', '初始值張量：權重張量，只列形狀'))}</summary>
            <table>
              <tbody>
                {#each o.initializers as t (t.name)}
                  <tr><td><code>{t.name}</code></td><td class="v-num">{shape(t.shape)}</td></tr>
                {/each}
              </tbody>
            </table>
            <p class="v-muted" style="margin: 0.25rem 0 0;">{say(lang, w('The export turned the small MLPs’ Linear layers into MatMul + Add, so their weight matrices carry generated names (onnx::MatMul_…), transposed; learn/v/export.py reads them back by their Add’s bias.', '导出时把小 MLP 的 Linear 层变成 MatMul + Add，所以它们的权重矩阵是自动生成的名字（onnx::MatMul_…），而且是转置的；learn/v/export.py 按对应 Add 的偏置把它们读回来。', '匯出時把小 MLP 的 Linear 層變成 MatMul + Add，所以它們的權重矩陣是自動產生的名字（onnx::MatMul_…），而且是轉置的；learn/v/export.py 按對應 Add 的偏置把它們讀回來。'))}</p>
          </details>
          <p style="margin: 0.4rem 0 0;">{say(lang, w('Operators', '算子', '算子'))}: <span class="v-num">{Object.entries(o.ops).map(([k, n]) => `${k} ${n}`).join(', ')}</span></p>
        {:else if f.key === 'tests'}
          <p style="margin: 0;">{say(lang, w('Each entry', '每一项', '每一項'))}: {entries(f.entry).map(([k, v]) => `${k}: ${list(v)}`).join(' · ')}</p>
          {#if f.values}
            <p class="v-num" style="margin: 0.25rem 0 0;">{say(lang, w('The trainer’s values (chance to win)', '训练器给出的值（获胜机率）', '訓練器給出的值（獲勝機率）'))}: {numbers(f.values)}</p>
          {/if}
        {:else}
          <p style="margin: 0;"><code>{JSON.stringify(f.value)}</code></p>
        {/if}
      </div>
    </details>
  {/each}
  <p class="v-caption">
    {say(lang, w(
      'Extracted from weights/silent/v8-3.json and c8-3.json at the page’s commit: field names, types, sizes and shapes, and the small fields’ values; the weights themselves are left out.',
      '从页面所在提交的 weights/silent/v8-3.json 和 c8-3.json 提取：字段名、类型、大小和形状，以及小字段的值；权重本身没有列出。',
      '從頁面所在提交的 weights/silent/v8-3.json 和 c8-3.json 擷取：欄位名、型別、大小和形狀，以及小欄位的值；權重本身沒有列出。',
    ))}
  </p>
</div>
