// Pure V's weights from a net file's ONNX graph (learn/v/export.py writes it), by the PyTorch names of learn/v/net.py,
// each in PyTorch's layout (a Linear's weight is [out, in]). Reads the few protobuf fields it needs: the graph's
// nodes and initializers. The Linear layers the export turned into MatMul + Add have unnamed weights, stored [in, out];
// each is named here by its Add's bias, as export.py's weights() does.

export interface Tensor {
  shape: number[];
  data: Float32Array;
}

interface Node {
  op: string;
  inputs: string[];
  outputs: string[];
}

class Reader {
  buf: Uint8Array;
  pos: number;
  end: number;
  constructor(buf: Uint8Array, start = 0, end = buf.length) {
    this.buf = buf;
    this.pos = start;
    this.end = end;
  }
  varint(): number {
    let result = 0;
    let scale = 1;
    for (;;) {
      const b = this.buf[this.pos++];
      result += (b & 0x7f) * scale;
      if (b < 0x80) return result;
      scale *= 128;
    }
  }
  /** Each field of the message: its number, wire type, and the reader or value of its content. */
  *fields(): Generator<[number, number, Reader | number]> {
    while (this.pos < this.end) {
      const tag = this.varint();
      const field = Math.floor(tag / 8);
      const wire = tag & 7;
      if (wire === 0) yield [field, wire, this.varint()];
      else if (wire === 2) {
        const len = this.varint();
        const sub = new Reader(this.buf, this.pos, this.pos + len);
        this.pos += len;
        yield [field, wire, sub];
      } else if (wire === 5) {
        this.pos += 4;
        yield [field, wire, 0];
      } else if (wire === 1) {
        this.pos += 8;
        yield [field, wire, 0];
      } else throw new Error(`protobuf wire type ${wire} is not read here`);
    }
  }
  string(): string {
    return new TextDecoder().decode(this.buf.subarray(this.pos, this.end));
  }
  bytes(): Uint8Array {
    return this.buf.subarray(this.pos, this.end);
  }
  /** A packed repeated varint field's values. */
  *varints(): Generator<number> {
    while (this.pos < this.end) yield this.varint();
  }
}

// ModelProto.graph = 7; GraphProto.node = 1, initializer = 5; NodeProto.input = 1, output = 2, op_type = 4;
// TensorProto.dims = 1, data_type = 2, float_data = 4, name = 8, raw_data = 9 (FLOAT = 1).
function tensor(r: Reader): [string, Tensor] {
  let name = '';
  let type = 0;
  const shape: number[] = [];
  let data: Float32Array | null = null;
  for (const [field, wire, v] of r.fields()) {
    if (field === 1 && wire === 0) shape.push(v as number);
    else if (field === 1) for (const d of (v as Reader).varints()) shape.push(d);
    else if (field === 2) type = v as number;
    else if (field === 8) name = (v as Reader).string();
    // raw_data, or float_data packed: either is the floats' little-endian bytes
    else if (field === 9 || (field === 4 && wire === 2)) data = new Float32Array((v as Reader).bytes().slice().buffer);
  }
  return [name, { shape, data: type === 1 && data ? data : new Float32Array(0) }];
}

function node(r: Reader): Node {
  const n: Node = { op: '', inputs: [], outputs: [] };
  for (const [field, , v] of r.fields()) {
    if (field === 1) n.inputs.push((v as Reader).string());
    else if (field === 2) n.outputs.push((v as Reader).string());
    else if (field === 4) n.op = (v as Reader).string();
  }
  return n;
}

const transpose = (t: Tensor): Tensor => {
  const [rows, cols] = t.shape;
  const data = new Float32Array(rows * cols);
  for (let i = 0; i < rows; i++) for (let j = 0; j < cols; j++) data[j * rows + i] = t.data[i * cols + j];
  return { shape: [cols, rows], data };
};

/** The graph's weights by learn/v/net.py's parameter names ("phi.0.weight", "body.2.bias", ...). */
export function weights(onnx: Uint8Array): Map<string, Tensor> {
  let graph: Reader | null = null;
  for (const [field, , v] of new Reader(onnx).fields()) if (field === 7) graph = v as Reader;
  if (!graph) throw new Error('the ONNX model has no graph');
  const inits = new Map<string, Tensor>();
  const nodes: Node[] = [];
  for (const [field, , v] of graph.fields()) {
    if (field === 5) inits.set(...tensor(v as Reader));
    else if (field === 1) nodes.push(node(v as Reader));
  }
  const out = new Map<string, Tensor>();
  for (const [name, t] of inits) if (name.startsWith('net.')) out.set(name.slice(4), t);
  const adds = new Map<string, Node>();
  for (const n of nodes) if (n.op === 'Add') for (const i of n.inputs) adds.set(i, n);
  for (const n of nodes) {
    if (n.op !== 'MatMul' || !inits.has(n.inputs[1]) || n.inputs[1].startsWith('net.')) continue;
    const bias = adds.get(n.outputs[0])?.inputs.find((i) => i.startsWith('net.'));
    if (!bias) throw new Error(`MatMul ${n.inputs[1]} has no bias`);
    out.set(bias.slice(4).replace(/\.bias$/, '.weight'), transpose(inits.get(n.inputs[1])!));
  }
  return out;
}
