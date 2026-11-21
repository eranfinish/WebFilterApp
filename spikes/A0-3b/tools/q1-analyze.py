# Q1 analysis: reads one run's server-log slice and its host timestamps, prints the enforcement bound.
# Usage: python tools/q1-analyze.py <run-number>   (reads evidence/q1-run<N>.txt and its .server.tmp)
import sys, json, re, datetime, os
run = sys.argv[1]
ev = os.path.join(os.path.dirname(__file__), '..', 'evidence')
out_path = os.path.join(ev, f'q1-run{run}.txt')
srv_path = out_path + '.server.tmp'
hb = ha = None
for line in open(out_path, encoding='utf-8'):
    m = re.search(r'host_before_broadcast_ms=(\d+) host_after_broadcast_ms=(\d+)', line)
    if m:
        hb, ha = int(m.group(1)), int(m.group(2))
hits = []
for line in open(srv_path, encoding='utf-8'):
    try:
        j = json.loads(line)
    except Exception:
        continue
    if j.get('port') != 8130 or j.get('path') != '/blocked.html':
        continue
    m = re.search(r'[?&]run=([^&]+)&n=(\d+)', j.get('query', ''))
    if not m or m.group(1) != run:
        continue
    t = datetime.datetime.fromisoformat(j['ts'].replace('Z', '+00:00')).timestamp() * 1000
    hits.append((int(m.group(2)), t))
print(f'run={run} allowed_attempts_logged={len(hits)} host_before={hb} host_after={ha}')
if hits:
    last_n, last_t = hits[-1]
    print(f'last_allowed_n={last_n} last_allowed_host_ms={last_t:.0f}')
    print(f'first_allowed_n={hits[0][0]} (run started polling before the rule)')
    # Enforcement lies after the last allowed attempt and before the next one (~500 ms later).
    # The DPC call itself began at host_before and ended at host_after, so the latency from the call is:
    lo = last_t - ha           # the effect cannot have started before the call began, so this is a bound, not a value
    hi = last_t + 500 - hb
    print(f'latency_from_call_start_ms in [0, {hi:.0f}] (last allowed attempt was {last_t - hb:.0f} ms after call start)')
else:
    print('no allowed attempts logged for this run')
