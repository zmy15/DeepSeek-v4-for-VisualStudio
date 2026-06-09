"""Detailed comparison of Ask last call vs Edit first handoff call."""
import json, os

dump_dir = r'C:\Users\周明阳\AppData\Local\Temp\DeepSeekCacheDumps'

ask_fn = 'req_0017_20260609_234233_004.json'
edit_fn = 'req_0019_20260609_234243_710.json'

def load_dump(fn):
    fp = os.path.join(dump_dir, fn)
    with open(fp, 'r', encoding='utf-8-sig') as f:
        data = json.load(f)
    req = data.get('request', {})
    full = req.get('full_request', '')
    if isinstance(full, str):
        return json.loads(full)
    return full

ask = load_dump(ask_fn)
edit = load_dump(edit_fn)

# ============================================================
# CRITICAL: Check Edit's extra messages [82]-[85]
# ============================================================
print('=' * 70)
print('EDIT EXTRA MESSAGES [81]-[85]')
print('=' * 70)
for i in [81, 82, 83, 84, 85]:
    m = edit['messages'][i]
    role = m.get('role')
    content = m.get('content', '') or ''
    tcs = m.get('tool_calls')
    tcid = m.get('tool_call_id', '')
    name = m.get('name', '')
    rc = m.get('reasoning_content', '')
    print(f'[{i}] role={role} clen={len(content)} tcid={tcid[:30]} name={name} rclen={len(rc or "")}')
    if tcs:
        for j, tc in enumerate(tcs):
            fid = tc.get('id', '?')
            fname = tc.get('function', {}).get('name', '?')
            print(f'     tc[{j}] id={fid} name={fname}')
    if content:
        print(f'     content: {content[:100]}')
    print()

# ============================================================
# Check ALL tool_call_ids in both dumps to find orphans
# ============================================================
print('=' * 70)
print('ORPHAN TOOL MESSAGE ANALYSIS')
print('=' * 70)

def find_orphans(msgs):
    """Find tool messages whose tool_call_id doesn't match any assistant's tool_calls."""
    assistant_tc_ids = set()
    for m in msgs:
        if m.get('role') == 'assistant':
            for tc in (m.get('tool_calls') or []):
                assistant_tc_ids.add(tc.get('id', ''))
    
    orphans = []
    for i, m in enumerate(msgs):
        if m.get('role') == 'tool':
            tcid = m.get('tool_call_id', '')
            if tcid and tcid not in assistant_tc_ids:
                orphans.append((i, tcid, m.get('name', ''), len(m.get('content', '') or '')))
    return orphans

ask_orphans = find_orphans(ask['messages'])
edit_orphans = find_orphans(edit['messages'])

print(f'Ask orphans: {len(ask_orphans)}')
for idx, tcid, name, clen in ask_orphans:
    print(f'  [{idx}] tcid={tcid[:40]} name={name} clen={clen}')
print()
print(f'Edit orphans: {len(edit_orphans)}')
for idx, tcid, name, clen in edit_orphans:
    print(f'  [{idx}] tcid={tcid[:40]} name={name} clen={clen}')

# Check: are the orphans at the SAME positions in the shared 82 messages?
print()
print('Orphan comparison (shared first 82 msgs):')
ask_orphan_set = set((idx, tcid) for idx, tcid, _, _ in ask_orphans if idx < 82)
edit_orphan_set = set((idx, tcid) for idx, tcid, _, _ in edit_orphans if idx < 82)
if ask_orphan_set == edit_orphan_set:
    print('  IDENTICAL orphans in shared range - cleaning should produce same result')
else:
    only_ask = ask_orphan_set - edit_orphan_set
    only_edit = edit_orphan_set - ask_orphan_set
    if only_ask:
        print(f'  Only Ask orphans: {only_ask}')
    if only_edit:
        print(f'  Only Edit orphans: {only_edit}')

# ============================================================
# Simulate cleaning and compare cleaned message lists
# ============================================================
print()
print('=' * 70)
print('SIMULATED CLEANING COMPARISON')
print('=' * 70)

def simulate_clean(msgs):
    """Simulate orphan tool message removal."""
    # Build assistant tc_ids set
    assistant_tc_ids = set()
    for m in msgs:
        if m.get('role') == 'assistant':
            for tc in (m.get('tool_calls') or []):
                assistant_tc_ids.add(tc.get('id', ''))
    
    cleaned = []
    for m in msgs:
        if m.get('role') == 'tool':
            tcid = m.get('tool_call_id', '')
            if tcid and tcid not in assistant_tc_ids:
                continue  # Skip orphan
        cleaned.append(m)
    return cleaned

ask_cleaned = simulate_clean(ask['messages'])
edit_cleaned = simulate_clean(edit['messages'])

print(f'Ask: {len(ask["messages"])} -> {len(ask_cleaned)} after cleaning')
print(f'Edit: {len(edit["messages"])} -> {len(edit_cleaned)} after cleaning')

# Compare cleaned message lists
min_len = min(len(ask_cleaned), len(edit_cleaned))
cleaned_same = 0
cleaned_diff = 0
first_diff_idx = -1
for i in range(min_len):
    aj = json.dumps(ask_cleaned[i], ensure_ascii=False, sort_keys=True)
    ej = json.dumps(edit_cleaned[i], ensure_ascii=False, sort_keys=True)
    if aj == ej:
        cleaned_same += 1
    else:
        cleaned_diff += 1
        if first_diff_idx < 0:
            first_diff_idx = i

print(f'Cleaned messages: SAME={cleaned_same} DIFF={cleaned_diff}')
if first_diff_idx >= 0:
    print(f'First cleaned diff at index {first_diff_idx}')
    am = ask_cleaned[first_diff_idx]
    em = edit_cleaned[first_diff_idx]
    print(f'  Ask [{first_diff_idx}]: role={am.get("role")} clen={len(am.get("content") or "")}')
    print(f'  Edit [{first_diff_idx}]: role={em.get("role")} clen={len(em.get("content") or "")}')
else:
    print('ALL cleaned messages byte-identical in shared range!')
