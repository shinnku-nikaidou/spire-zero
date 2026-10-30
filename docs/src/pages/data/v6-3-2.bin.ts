// The figures' net's weights, as one little-endian Float32Array laid out as v6-3-2.json's tensors say.
import type { APIRoute } from 'astro';
import { packed } from '../../lib/v/serve.ts';

export const GET: APIRoute = () => new Response(packed().floats.slice().buffer, { headers: { 'Content-Type': 'application/octet-stream' } });
