<script lang="ts">
  // Picks one of the run states of states.ts.
  import type { Picked } from './states.ts';
  interface Props {
    states: Picked[];
    value: string;
    label: string;
    onpick: (id: string) => void;
  }
  let { states, value, label, onpick }: Props = $props();
</script>

<label>
  <span class="v-ink2">{label}</span>
  <select {value} onchange={(e) => onpick((e.currentTarget as HTMLSelectElement).value)}>
    {#each states as s (s.id)}
      <option value={s.id}>{s.label}</option>
    {/each}
  </select>
</label>
