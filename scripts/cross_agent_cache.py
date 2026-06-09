"""Cross-agent cache analysis: compare message structure across handoff boundaries."""
import json, os, glob

dump_dir = r'C:\Users\周明阳\AppData\Local\Temp\DeepSeekCacheDumps'

def load_msgs(fn_pattern):
    matches = glob.glob(os.path.join(dump_dir, fn_pattern))
    if not matches:
        return None, None, None
    fp = matches[0]
    with open(fp, 'r', encoding='utf-8-sig') as f:
        d = json.load(f)
    seq = d.get('sequence')
    req = d.get('request', {})
    full = req.get('full_request', '')
    rj = json.loads(full) if isinstance(full, str) else full
    msgs = rj.get('messages', [])
    c = d.get('cache') or {}
    rate = c.get('hit_rate_pct', '-')
    hit = c.get('hit_tokens', '-')
    return seq, msgs, (rate, hit)

# === Boundary 1: Ask -> Plan ===
seq1, pre, (rate1, hit1) = load_msgs('req_0002_*.json')
seq2, post, (rate2, hit2) = load_msgs('req_0003_*.json')

print('=' * 70)
print(f'BOUNDARY 1: Ask(seq={seq1}, {rate1}%) -> Plan(seq={seq2}, {rate2}%)')
print(f'  PRE: {len(pre)} msgs, POST: {len(post)} msgs')
print('=' * 70)

# Compare messages index by index
min_len = min(len(pre), len(post))
same = 0
first_diff = -1
for i in range(min_len):
    pj = json.dumps(pre[i], ensure_ascii=False, sort_keys=True)
    nj = json.dumps(post[i], ensure_ascii=False, sort_keys=True)
    if pj == nj:
        same += 1
    elif first_diff < 0:
        first_diff = i

print(f'  Same messages: {same}/{min_len}')
if first_diff >= 0:
    print(f'  First diff at index {first_diff}:')
    for tag, m in [('PRE', pre[first_diff]), ('POST', post[first_diff])]:
        role = m.get('role', '?')
        c = m.get('content', '') or ''
        tc = m.get('tool_calls')
        tcid = m.get('tool_call_id', '')
        extra = f' TC:{len(tc)}' if tc else ''
        extra += f' tcid:{tcid[:20]}' if tcid else ''
        print(f'    {tag} [{first_diff}]: role={role} clen={len(c)}{extra} | {c[:60]}')
    
    # Show surrounding context
    for ctx_i in range(max(0, first_diff-2), min(min_len, first_diff+3)):
        pm = pre[ctx_i]
        nm = post[ctx_i]
        pr = pm.get('role', '?')
        nr = nm.get('role', '?')
        pc = (pm.get('content', '') or '')[:40]
        nc = (nm.get('content', '') or '')[:40]
        mark = ' <<<' if ctx_i == first_diff else ''
        print(f'    [{ctx_i}] PRE:{pr:10s} | POST:{nr:10s} {mark}')

if len(post) > len(pre):
    print(f'  POST has {len(post)-len(pre)} extra messages at end:')
    for i in range(len(pre), len(post)):
        m = post[i]
        role = m.get('role', '?')
        c = m.get('content', '') or ''
        print(f'    [{i}] {role:10s} clen={len(c):5d} | {c[:80]}')

# === Boundary 2: Plan alignment -> design ===
print()
seq3, pre2, (rate3, hit3) = load_msgs('req_0054_*.json')
seq4, post2, (rate4, hit4) = load_msgs('req_0055_*.json')

print('=' * 70)
print(f'BOUNDARY 2: Plan align(seq={seq3}, {rate3}%) -> Plan design(seq={seq4}, {rate4}%)')
print(f'  PRE: {len(pre2)} msgs, POST: {len(post2)} msgs')
print('=' * 70)

min_len2 = min(len(pre2), len(post2))
same2 = 0
first_diff2 = -1
for i in range(min_len2):
    pj = json.dumps(pre2[i], ensure_ascii=False, sort_keys=True)
    nj = json.dumps(post2[i], ensure_ascii=False, sort_keys=True)
    if pj == nj:
        same2 += 1
    elif first_diff2 < 0:
        first_diff2 = i

print(f'  Same messages: {same2}/{min_len2}')
if first_diff2 >= 0:
    print(f'  First diff at index {first_diff2}:')
    for tag, m in [('PRE', pre2[first_diff2]), ('POST', post2[first_diff2])]:
        role = m.get('role', '?')
        c = m.get('content', '') or ''
        tc = m.get('tool_calls')
        extra = f' TC:{len(tc)}' if tc else ''
        print(f'    {tag} [{first_diff2}]: role={role} clen={len(c)}{extra} | {c[:60]}')

if len(post2) > len(pre2):
    print(f'  POST has {len(post2)-len(pre2)} extra messages at end:')
    for i in range(len(pre2), len(post2)):
        m = post2[i]
        role = m.get('role', '?')
        c = m.get('content', '') or ''
        print(f'    [{i}] {role:10s} clen={len(c):5d} | {c[:80]}')

# === Key finding: system message position change ===
print()
print('=' * 70)
print('SYSTEM MESSAGE POSITION ANALYSIS')
print('=' * 70)
for label, pre_msgs, post_msgs in [
    ('Ask->Plan', pre, post),
    ('Plan align->design', pre2, post2),
]:
    pre_sys = [(i, m) for i, m in enumerate(pre_msgs) if m.get('role')=='system']
    post_sys = [(i, m) for i, m in enumerate(post_msgs) if m.get('role')=='system']
    print(f'\n{label}: sys {len(pre_sys)} -> {len(post_sys)}')
    # Check: are all PRE system messages at the SAME positions in POST?
    for pi, pm in pre_sys:
        pc = (pm.get('content','') or '')
        found_at = -1
        for qi, qm in post_sys:
            qc = (qm.get('content','') or '')
            if pc == qc:
                found_at = qi
                break
        if found_at >= 0 and found_at == pi:
            print(f'  [{pi}] SAME position: {pc[:50]}')
        elif found_at >= 0:
            print(f'  [{pi}] MOVED {pi}->{found_at}: {pc[:50]}')
        else:
            print(f'  [{pi}] NOT FOUND: {pc[:50]}')
    # Check: any NEW system messages in POST?
    for qi, qm in post_sys:
        qc = (qm.get('content','') or '')
        found = any((pm.get('content','') or '') == qc for _, pm in pre_sys)
        if not found:
            print(f'  [{qi}] NEW (POST only): {qc[:80]}')
