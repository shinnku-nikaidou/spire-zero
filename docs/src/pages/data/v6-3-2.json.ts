// The figures' net, served at build time from the repository's net file: its vocabulary, state scaling, tensor
// layout and test runs (docs: macro-brain/network §2).
import type { APIRoute } from 'astro';
import { packed } from '../../lib/v/serve.ts';

export const GET: APIRoute = () => Response.json(packed().meta);
