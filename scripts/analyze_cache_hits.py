import json, os, glob

dump_dir = r'C:\Users\周明阳\AppData\Local\Temp\DeepSeekCacheDumps'
files = sorted(glob.glob(os.path.join(dump_dir, 'req_*.json')))

print(f'Total dump files: {len(files)}')
print()
print(f'{"Seq":>4s} {"Time":>8s} {"Error":>12s} {"Msgs":>5s} {"HitTokens":>10s} {"MissTokens":>10s} {"Hit%":>7s} {"Cacheable":>10s} {"SizeKB":>7s} {"Flag":>6s}')
print('-' * 90)

total_hit = 0
total_miss = 0
total_cacheable = 0
green_count = 0
yellow_count = 0
red_count = 0
low_hit_pairs = []  # consecutive pairs with drastic drops

prev_cacheable = 0
prev_hit = 0
prev_seq = 0

for fp in files:
    with open(fp, 'r', encoding='utf-8-sig') as f:
        data = json.load(f)
    seq = data.get('sequence', 0)
    raw_ts = data.get('timestamp', '')
    if raw_ts and len(raw_ts) >= 19:
        ts = raw_ts[11:19]  # HH:MM:SS
    else:
        ts = '?'
    err = (data.get('error') or '(pre-send)')[:12]
    req = data.get('request', {}) or {}
    msgs = (req.get('messages_summary') or [])
    msg_count = len(msgs)
    size = req.get('size_kb', 0)
    cache = data.get('cache') or {}
    hit = cache.get('hit_tokens', 0) or 0
    miss = cache.get('miss_tokens', 0) or 0
    rate = cache.get('hit_rate_pct', 0)
    if rate is None:
        rate = 0.0
    elif isinstance(rate, str):
        rate = float(rate.replace('%', ''))
    else:
        rate = float(rate)
    cacheable = cache.get('cacheable_tokens', 0) or 0
    
    if rate < 30:
        flag = 'RED'
        red_count += 1
    elif rate < 85:
        flag = 'YEL'
        yellow_count += 1
    else:
        flag = 'GRN'
        green_count += 1
    
    total_hit += hit
    total_miss += miss
    total_cacheable += cacheable
    
    # Detect drastic drops (previous was high, current is low)
    drop = ''
    if prev_seq > 0 and prev_cacheable > 0:
        prev_rate = prev_hit / prev_cacheable * 100
        if prev_rate > 70 and rate < 30 and cacheable > 0:
            drop = f' << DROP {prev_rate:.0f}%->{rate:.0f}%'
    
    print(f'{seq:4d} {ts:>8s} {err:>12s} {msg_count:5d} {hit:10,d} {miss:10,d} {rate:6.1f}% {cacheable:10,d} {size:7.0f} {flag:>6s}{drop}')
    
    prev_cacheable = cacheable
    prev_hit = hit
    prev_seq = seq

print()
print('=' * 90)
print(f'SUMMARY: Total hit={total_hit:,}  Total miss={total_miss:,}  Overall rate={total_hit/total_cacheable*100:.1f}%' if total_cacheable > 0 else 'SUMMARY: No data')
print(f'Green(>85%): {green_count}  Yellow(30-85%): {yellow_count}  Red(<30%): {red_count}')
print(f'Total cacheable tokens: {total_cacheable:,}')

# Identify the problematic requests
print()
print('=== RED (<30% hit) requests detail ===')
for fp in files:
    with open(fp, 'r', encoding='utf-8-sig') as f:
        data = json.load(f)
    seq = data.get('sequence', 0)
    cache = data.get('cache') or {}
    rate = cache.get('hit_rate_pct', 0) or 0
    if isinstance(rate, (int, float)) and rate < 30:
        ts = data.get('timestamp', '')[:19] if data.get('timestamp') else '?'
        req = data.get('request', {}) or {}
        msgs = (req.get('messages_summary') or [])
        hit = cache.get('hit_tokens', 0) or 0
        miss = cache.get('miss_tokens', 0) or 0
        cacheable = cache.get('cacheable_tokens', 0) or 0
        
        # Show first 3 messages to understand context
        print(f'\n  Seq#{seq} at {ts}: hit={hit:,} miss={miss:,} rate={rate:.1f}% cacheable={cacheable:,}')
        print(f'  Messages ({len(msgs)}):')
        for i, m in enumerate(msgs[:4]):
            role = m.get('role', '?')
            clen = m.get('content_length', 0)
            preview = (m.get('content_preview') or '')[:80]
            has_tc = m.get('has_tool_calls', False)
            tc_mark = ' [TC]' if has_tc else ''
            print(f'    [{i}] {role:12s} len={clen:5d}{tc_mark} | {preview}')
        if len(msgs) > 4:
            print(f'    ... ({len(msgs)-4} more)')
