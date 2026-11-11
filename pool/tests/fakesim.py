"""fakesim.py REV run [OPTION...] --start I --n N --log FILE --records FILE: the pool tests' stand-in for the simulator
(localpool.py packs it into each build bundle; the bundle's sim-REV.sh starts it as worker.py starts the real one).

For each of the chunk's N runs it writes a record line and a decision log line, as the real `run` does, then the summary
line worker.py looks for. A record says which build (REV), tag and inputs played it: the tests read them back.

  --tag T      a job's label, copied into its records
  --sleep S    seconds before each run
  --runs K     writes only the chunk's first K runs, then exits 1: a chunk with fewer runs than dealt

The environment, set per worker: FAKESIM_HOLD=FILE, a run there starts only once FILE exists; FAKESIM_FAIL_TAG=T, a
chunk of a job tagged T there writes no run and exits 1.
"""
import argparse
import json
import os
import signal
import sys
import time


def main():
    signal.alarm(120)  # a run its worker lost never outlives the tests by long
    rev, command = sys.argv[1:3]
    if command != "run":
        sys.exit(f"fakesim: unknown command {command}")
    p = argparse.ArgumentParser()
    p.add_argument("--tag", default="")
    p.add_argument("--sleep", type=float, default=0.0)
    p.add_argument("--runs", type=int)
    p.add_argument("--start", type=int, required=True)
    p.add_argument("--n", type=int, required=True)
    p.add_argument("--log", required=True)
    p.add_argument("--records", required=True)
    args = p.parse_args(sys.argv[3:])
    if args.tag and os.environ.get("FAKESIM_FAIL_TAG") == args.tag:
        print(f"fakesim: told to fail the jobs tagged {args.tag}", flush=True)
        sys.exit(1)
    # the worker starts a run in the job's inputs: say which inputs these are
    try:
        with open("eval/marker.txt") as f:
            inputs = f.read().strip()
    except OSError:
        inputs = None
    hold = os.environ.get("FAKESIM_HOLD")
    with open(args.records, "w") as records, open(args.log, "w") as log:
        for k in range(args.n):
            if args.runs is not None and k >= args.runs:
                print(f"fakesim: told to stop after {args.runs} runs", flush=True)
                sys.exit(1)
            while hold and not os.path.exists(hold):
                time.sleep(0.02)
            time.sleep(args.sleep)
            index = args.start + k
            record = {"index": index, "seed": f"SEED{index}", "outcome": "won" if index % 3 == 0 else "lost",
                      "act": 1 + index % 3, "floor": 10 + index % 40, "rev": rev, "tag": args.tag, "inputs": inputs}
            records.write(json.dumps(record, separators=(",", ":")) + "\n")
            records.flush()
            log.write(json.dumps({"index": index, "decisions": []}, separators=(",", ":")) + "\n")
            log.flush()
            print(f"#{index} SEED{index} {record['outcome']}", flush=True)
    print("game Log.Error lines: 0", flush=True)


if __name__ == "__main__":
    main()
