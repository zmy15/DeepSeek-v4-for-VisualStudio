import json, os
dump_dir = r'C:\Users\周明阳\AppData\Local\Temp\DeepSeekCacheDumps'

targets = [
    ('Ask (standalone)', 'req_0002_20260610_020059_429.json'),
    ('Edit (Ask->Edit handoff)', 'req_0027_20260610_022641_870.json'),
]

for label, fn in targets:
    fp = os.path.join(dump_dir, fn)
    if not os.path.exists(fp):
        print(f'{fn}: NOT FOUND')
        continue
    with open(fp, 'r', encoding='utf-8-sig') as f:
        d = json.load(f)
    req = d.get('request',{})
    full = req.get('full_request','')
    rj = json.loads(full) if isinstance(full, str) else full
    msgs = rj.get('messages',[])
    
    sys_idx = {i for i, m in enumerate(msgs) if m.get('role')=='system'}
    
    print(f'=== {label}: {len(msgs)} msgs, {len(sys_idx)} system at {sorted(sys_idx)} ===')
    
    # Show first 5 and last 5 entries
    entries_shown = 0
    for i, m in enumerate(msgs):
        if i in sys_idx:
            # Show system msgs briefly
            c = m.get('content','') or ''
            print(f'  [{i:3d}] SYSTEM    len={len(c):5d} | {c[:50]}')
            continue
        
        show = i < 5 or i >= len(msgs)-5
        if show:
            r = m.get('role','?')
            c = m.get('content','') or ''
            tc = m.get('tool_calls')
            tcid = m.get('tool_call_id','')
            name = m.get('name','')
            extra = ''
            if tc: extra += ' TC={}'.format(len(tc))
            if tcid: extra += ' tcid={}'.format(tcid[:25])
            if name: extra += ' name={}'.format(name)
            preview = c[:70].replace('\n',' ').replace('\r','')
            print(f'  [{i:3d}] {r:10s} len={len(c):5d}{extra} | {preview}')
            entries_shown += 1
    
    # Summary
    roles = {}
    tc_count = 0
    tool_count = 0
    for i, m in enumerate(msgs):
        if i in sys_idx: continue
        r = m.get('role','?')
        roles[r] = roles.get(r, 0) + 1
        if m.get('tool_calls'):
            tc_count += len(m['tool_calls'])
        if r == 'tool':
            tool_count += 1
    
    total_entries = sum(roles.values())
    print(f'\n  Entries: {total_entries} total = {roles}')
    print(f'  Tool calls: {tc_count}, Tool results: {tool_count}')
    print(f'  Turns: user={roles.get("user",0)}, assistant={roles.get("assistant",0)}')
    print()
