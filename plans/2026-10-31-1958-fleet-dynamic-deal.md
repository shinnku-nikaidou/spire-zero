# Fleet: dynamic deal

Agreed with the user 2026-10-31 (Taiwan time 19:58). The refactor came first (67670f9); the user said to start this the same evening. Done: see the fleet skill's references/boxes.md.

## Why

v7-1f's test (100 medium + 200 low, 51 slots, 19:05-19:42 UTC+8): each host got its share in advance, by a speed table,
and stopped when it was done. 40,145 of 104,862 slot-seconds idle (38.3 %), counted from each slot's first log line to
the last slot's end. The table says ds6754 runs at 0.65; it ran at 0.35 (medium, 633 s a run against pushqkzh's 223) and
0.42 (low, 253 s against 106). Recomputed at the same slots with no fixed shares: medium about 19 min, low about 20 min,
the box over at about 19:30 instead of 19:42.

Only jobs that run out of queue gain (gates, reports). Self-play plays to its deadline and has no such idle time.

## Design

One queue per job on the hub. A slot asks for its next chunk over HTTP when it is free. No shares, no speed table.

**The server** (the user: serve HTTP on the hub, the slot's script makes a request with some parameters). A small
Python `http.server` on the hub (`STS2_HUB` in `.env`) under systemd, one thread, so requests are served one at a time and need no
other lock. It keeps its state in files, so a restart loses nothing:

```
/root/sts2-store/JOB/deal/queue.txt     "INDEX N" lines, written once at launch by box.sh (rsync, as the manifest goes)
                          pos           the next line to give
                          held/SHARD    what SHARD holds now
                          claims.log    "EPOCH SHARD INDEX", one line per chunk given
```

**The request.** `curl -fsS --max-time 20 "http://HUB:PORT/claim?job=JOB&shard=SHARD&last=LAST&token=TOKEN"`

- Answer `INDEX N`, or 204 when the queue is used up. LAST is the index the slot last received (none at first).
- A lost answer: if `held/SHARD` differs from LAST the slot never got it, and the same chunk is given again. So a lost
  answer leaves no hole and a retry deals nothing twice.
- The port is open to the internet, so a request without the job's token gets 403 and JOB and SHARD are checked
  against a strict pattern. The token is written at launch and shipped with the job's inputs.

**The slot.** `slot.sh`'s `while read` over `queue-HOST.txt` and `mkdir claim/INDEX` become: check the deadline, then
claim, then play. A failed request is tried again every 30 s until the deadline; a run being played is not touched.

## What stays

| guarantee | after |
| --- | --- |
| `prefix` | better: all hosts take from the front, so the ragged edge is at most the runs in flight |
| manifest | no speed, chunks, runs_queued; `box.sh fetch` counts runs per host from `done-` and keeps `claims.log` |
| deadline file | the same; checked before a claim, so the deadline burns no seed |
| host pushes, then deletes its folder | the same |
| `AFTER=` | the same: the queue leaves out what those boxes finished |
| `box.sh start` | simpler: a late host just starts asking |
| `stop.sh`, `tasks.sh` | the same |
| no seed twice | one pointer, one thread; the test checks `done-` for repeats |

The repeat on cshinnku (v7-1's gate, index 2, slots 0 and 1, both started 01:57:20) is not explained: `mkdir` should have
stopped it, and it is the only repeat in the box jobs here. The new path does not use that lock; that is not a proof of
the cause.

## Not doing

- Slots moving between jobs. In v7-1f the two jobs would end about a minute apart, in v7-1's gate 15 min apart of about
  4 h. Each job keeps its own slots and queue.
- Raising limits now. At about 2.3 cores a slot, pushqkzh's 13 are about 30 busy threads (24 count) and pusheen's 10 about
  23 (16 count). Instead each host records busy % once a minute into `cpu-HOST.txt`, which goes with the push; the user
  decides limits from that. Lab manners and relay rules are unchanged.

## Cost

About 0.25 requests a second at v7-1f's load, one round trip each, against runs of 100-600 s. Gauntlet runs stay at
CHUNK 100, one request per about 70 s a slot. Not measured yet: the hub's own load (it is a relay), so step 2 measures it
with `relay-load.sh`.

## Steps (about 1-2 h)

1. The server on the hub (installing it and opening the port need the user's OK, as they change the hub). 50 parallel
   clients, 2,000 claims: nothing repeated, nothing missing; a lost answer; a restart mid-way.
2. A small box on all six hosts, low budget, about 10 min: request times from each host (the lab pair have no public IP,
   they call out), the hub with `relay-load.sh`, a cut connection, a moved deadline, `stop.sh`.
3. `SKILL.md`, then the next real box uses it; compare idle slot time with the 38.3 %.
