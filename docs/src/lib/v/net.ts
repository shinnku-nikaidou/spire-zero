// Pure V's forward pass (learn/v/net.py), step by step, so the figures can show every layer
// (docs: macro-brain/network §2). RunNet runs the same graph with ONNX Runtime; `pnpm check` gives this one the net
// file's test runs and expects the trainer's values.
import { BAGS, STATE, tokens, state, type RunView, type Tokens } from './encode.ts';
import { weights, type Tensor } from './onnx.ts';

export interface NetFile {
  name: string;
  objective: 'floor' | 'progress' | 'win';
  floors: number;
  vocab: Record<string, string[]>;
  state: { names: string[]; mean: number[]; std: number[] };
  onnx: string;
  tests: { run: RunView; value: number }[];
}

/** What the browser loads: the net file less its graph, and its weights as one Float32Array. */
export interface Meta {
  name: string;
  objective: NetFile['objective'];
  floors: number;
  vocab: Record<string, string[]>;
  state: NetFile['state'];
  tensors: { name: string; shape: number[]; offset: number }[];
  tests: { run: RunView; value: number }[];
}

export const SUM_SCALE = 1 / 16; // the deck's sum next to its mean, scaled to about a mean's size

export interface CardTrace {
  token: Tokens['cards'][number];
  index: number; // its id's row in the card table, 0 for an id the net never saw
  x: Float64Array; // id vector + up × upgrade vector + modifiers' vectors (32)
  hidden: Float64Array; // phi's first layer (128)
  h: Float64Array; // phi's output (128)
}

export interface Trace {
  tokens: Tokens;
  cards: CardTrace[];
  mean: Float64Array; // the deck's mean of h (128)
  sum: Float64Array; // the deck's sum of h × 1/16 (128)
  bags: Record<(typeof BAGS)[number], { indexes: number[]; vector: Float64Array }>;
  raw: number[]; // the six numbers
  scaled: Float64Array; // standardized
  z: Float64Array; // the body's input (390)
  body1: Float64Array;
  body2: Float64Array; // what the heads read (256)
  hazards: Float64Array; // per floor, the chance to die there if alive there
  shares: Float64Array | null; // per floor, the share of the enemies' HP taken in a combat the run ends in there
}

export interface Value {
  from: number; // the floor the value counts from
  survive: Float64Array; // per floor g ≥ from, the chance to be alive after it (1 before from)
  end: Float64Array; // per floor, the chance the run ends there
  expectedFloor: number;
  progress: number | null;
  win: number; // the chance to survive every floor
  value: number; // what RunNet.Value returns for the net's objective
}

const sigmoid = (x: number) => 1 / (1 + Math.exp(-x));

/** y = W x + b for a Linear's weight [out, in]; relu if asked. */
function linear(w: Tensor, b: Tensor, x: ArrayLike<number>, relu: boolean): Float64Array {
  const [rows, cols] = w.shape;
  const y = new Float64Array(rows);
  for (let o = 0; o < rows; o++) {
    let s = b.data[o];
    const base = o * cols;
    for (let i = 0; i < cols; i++) s += w.data[base + i] * x[i];
    y[o] = relu && s < 0 ? 0 : s;
  }
  return y;
}

export class V {
  readonly name: string;
  readonly objective: NetFile['objective'];
  readonly floors: number;
  readonly vocab: Record<string, string[]>;
  readonly index: Record<string, Map<string, number>>;
  readonly mean: number[];
  readonly std: number[];
  readonly w: Map<string, Tensor>;
  readonly dim: number;

  constructor(meta: Omit<Meta, 'tensors' | 'tests'>, w: Map<string, Tensor>) {
    if (meta.state.names.join() !== STATE.join()) throw new Error(`net ${meta.name} reads the state ${meta.state.names}`);
    this.name = meta.name;
    this.objective = meta.objective;
    this.floors = meta.floors;
    this.vocab = meta.vocab;
    this.index = Object.fromEntries(Object.entries(meta.vocab).map(([t, ids]) => [t, new Map(ids.map((id, i) => [id, i]))]));
    this.mean = meta.state.mean;
    this.std = meta.state.std;
    this.w = w;
    this.dim = w.get('cards.weight')!.shape[1];
  }

  /** From a net file as learn/v/export.py writes it. */
  static fromFile(file: NetFile): V {
    return new V(file, weights(decodeBase64(file.onnx)));
  }

  /** From what pack() wrote. */
  static fromPacked(meta: Meta, floats: Float32Array): V {
    const w = new Map<string, Tensor>();
    for (const t of meta.tensors) {
      const size = t.shape.reduce((a, b) => a * b, 1);
      w.set(t.name, { shape: t.shape, data: floats.subarray(t.offset, t.offset + size) });
    }
    return new V(meta, w);
  }

  private row(table: string, i: number): Float32Array {
    const t = this.w.get(table)!;
    return t.data.subarray(i * this.dim, (i + 1) * this.dim);
  }

  private id(table: string, id: string): number {
    return this.index[table].get(id) ?? 0;
  }

  trace(view: RunView): Trace {
    const tk = tokens(view);
    const d = this.dim;
    const g = (n: string) => this.w.get(n)!;
    const cards: CardTrace[] = tk.cards.map((token) => {
      const [id, up, mods] = token;
      const index = this.id('cards', id);
      const x = new Float64Array(d);
      const e = this.row('cards.weight', index);
      const u = this.row('card_up.weight', index);
      for (let i = 0; i < d; i++) x[i] = e[i] + up * u[i];
      for (const m of mods) {
        const v = this.row('modifiers.weight', this.id('modifiers', m));
        for (let i = 0; i < d; i++) x[i] += v[i];
      }
      const hidden = linear(g('phi.0.weight'), g('phi.0.bias'), x, true);
      const h = linear(g('phi.2.weight'), g('phi.2.bias'), hidden, true);
      return { token, index, x, hidden, h };
    });
    const width = g('phi.2.bias').shape[0];
    const total = new Float64Array(width);
    for (const c of cards) for (let i = 0; i < width; i++) total[i] += c.h[i];
    const n = Math.max(1, cards.length);
    const mean = total.map((v) => v / n);
    const sum = total.map((v) => v * SUM_SCALE);
    const bags = {} as Trace['bags'];
    for (const t of BAGS) {
      const indexes = tk[t].map((id) => this.id(t, id));
      const vector = new Float64Array(d);
      for (const i of indexes) {
        const v = this.row(`bags.${t}.weight`, i);
        for (let k = 0; k < d; k++) vector[k] += v[k];
      }
      bags[t] = { indexes, vector };
    }
    const raw = state(view);
    const scaled = Float64Array.from(raw, (v, i) => (v - this.mean[i]) / this.std[i]);
    const z = Float64Array.from([...mean, ...sum, ...BAGS.flatMap((t) => [...bags[t].vector]), ...scaled]);
    const body1 = linear(g('body.0.weight'), g('body.0.bias'), z, true);
    const body2 = linear(g('body.2.weight'), g('body.2.bias'), body1, true);
    const hazards = linear(g('head.weight'), g('head.bias'), body2, false).map(sigmoid);
    const shares = this.w.has('share_head.weight')
      ? linear(g('share_head.weight'), g('share_head.bias'), body2, false).map(sigmoid)
      : null;
    return { tokens: tk, cards, mean, sum, bags, raw, scaled, z, body1, body2, hazards, shares };
  }

  /** RunNet.Value: from the run's floor on, the chance to survive each floor, and what the objective reads from it. */
  value(trace: Trace, floor: number): Value {
    const { hazards: h, shares: s } = trace;
    const from = Math.min(Math.max(floor, 0), this.floors - 1);
    const survive = new Float64Array(this.floors).fill(1);
    const end = new Float64Array(this.floors);
    let alive = 1;
    let expected = from;
    let taken = 0;
    for (let g = from; g < this.floors; g++) {
      end[g] = alive * h[g];
      if (s) taken += alive * h[g] * s[g];
      alive *= 1 - h[g];
      survive[g] = alive;
      expected += alive;
    }
    const progress = s ? expected + taken : null;
    const value = this.objective === 'win' ? alive : this.objective === 'progress' ? progress! : expected;
    return { from, survive, end, expectedFloor: expected, progress, win: alive, value };
  }

  /** V's value of a run view, as RunNet.Value gives it. */
  of(view: RunView): number {
    return this.value(this.trace(view), view.floor).value;
  }
}

/** The net file as the browser loads it: its meta (the test runs' views stripped to what the net and figures read)
 * and its weights. */
export function pack(file: NetFile, strip: (view: RunView) => RunView): { meta: Meta; floats: Float32Array } {
  const w = weights(decodeBase64(file.onnx));
  const tensors: Meta['tensors'] = [];
  let offset = 0;
  for (const [name, t] of w) {
    tensors.push({ name, shape: t.shape, offset });
    offset += t.data.length;
  }
  const floats = new Float32Array(offset);
  for (const t of tensors) floats.set(w.get(t.name)!.data, t.offset);
  const { name, objective, floors, vocab, state } = file;
  const tests = file.tests.map((t) => ({ run: strip(t.run), value: t.value }));
  return { meta: { name, objective, floors, vocab, state, tensors, tests }, floats };
}

function decodeBase64(s: string): Uint8Array {
  const bin = atob(s);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}
