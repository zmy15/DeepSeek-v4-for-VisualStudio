import json, os

dump_dir = r'C:\Users\周明阳\AppData\Local\Temp\DeepSeekCacheDumps'

# Known file names for the Ask->Edit handoff boundary
ask_last_fn = 'req_0017_20260609_234233_004.json'
edit_first_fn = 'req_0019_20260609_234243_710.json'

for label, fn in [('Ask (last call)', ask_last_fn), ('Edit (first call, handoff)', edit_first_fn)]:
    fp = os.path.join(dump_dir, fn)
    with open(fp, 'r', encoding='utf-8-sig') as fh:
        data = json.load(fh)
    req = data.get('request', {})
    full = req.get('full_request', '')
    if isinstance(full, str):
        rj = json.loads(full)
    else:
        rj = full
    msgs = rj.get('messages', [])
    
    print(f'=== {label} ===')
    m2 = msgs[2]
    print(f'  messages[2] role={m2.get("role")} content={repr(m2.get("content"))}')
    tcs = m2.get('tool_calls', [])
    print(f'  tool_calls count: {len(tcs)}')
    for j, tc in enumerate(tcs[:3]):
        fid = tc.get('id', '?')
        fname = tc.get('function', {}).get('name', '?')
        fargs = tc.get('function', {}).get('arguments', '')[:80]
        print(f'    tc[{j}] id={fid} name={fname} args={fargs}')
    
    # Check messages[3] (tool result matching tc[0])
    if len(msgs) > 3:
        m3 = msgs[3]
        print(f'  messages[3] role={m3.get("role")} tool_call_id={m3.get("tool_call_id","")[:40]}')
    print()
