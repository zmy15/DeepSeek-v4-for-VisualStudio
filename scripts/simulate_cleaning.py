"""Simulate exact ChatStreamAsync cleaning rules on Edit messages to find 4 orphans."""
import json, os

dump_dir = r'C:\Users\周明阳\AppData\Local\Temp\DeepSeekCacheDumps'

# Load Edit dump
fp = os.path.join(dump_dir, 'req_0019_20260609_234243_710.json')
with open(fp, 'r', encoding='utf-8-sig') as f:
    data = json.load(f)
req = data.get('request', {})
full = req.get('full_request', '')
if isinstance(full, str):
    rj = json.loads(full)
else:
    rj = full
msgs = rj.get('messages', [])

print(f'Total messages: {len(msgs)}')

# Simulate Rules 1-4 (first pass cleaning)
# Rule 1: remove tool without tool_call_id
# Rule 2: remove assistant without content AND without tool_calls
# Rule 3: add empty reasoning_content to assistant with tool_calls
# Rule 4: merge consecutive same-role (user-user, assistant-assistant)

class Msg:
    def __init__(self, m):
        self.role = m.get('role', '')
        self.content = m.get('content', '') or ''
        self.tool_calls = m.get('tool_calls')
        self.tool_call_id = m.get('tool_call_id', '')
        self.name = m.get('name', '')
        self.reasoning_content = m.get('reasoning_content', '')
    
    def to_dict(self):
        d = {'role': self.role, 'content': self.content}
        if self.tool_calls is not None:
            d['tool_calls'] = self.tool_calls
        if self.tool_call_id:
            d['tool_call_id'] = self.tool_call_id
        if self.name:
            d['name'] = self.name
        if self.reasoning_content is not None:
            d['reasoning_content'] = self.reasoning_content
        return d

cleaned = []
last_role = None
removed_count = 0
merged_count = 0

for m in msgs:
    msg = Msg(m)
    
    # Rule 1: tool without tool_call_id
    if msg.role == 'tool' and not msg.tool_call_id:
        print(f'  RULE1 remove: [{len(cleaned)}] tool no tool_call_id')
        removed_count += 1
        continue
    
    # Rule 2: assistant without content AND without tool_calls
    if msg.role == 'assistant' and not msg.content and (not msg.tool_calls or len(msg.tool_calls) == 0):
        print(f'  RULE2 remove: [{len(cleaned)}] assistant empty')
        removed_count += 1
        continue
    
    # Rule 3: add empty reasoning_content
    if msg.role == 'assistant' and msg.tool_calls and len(msg.tool_calls) > 0 and msg.reasoning_content is None:
        msg.reasoning_content = ''
    
    # Rule 4: merge consecutive same-role
    if last_role and msg.role == last_role and msg.role in ('user', 'assistant'):
        if cleaned:
            last_msg = cleaned[-1]
            if msg.content:
                last_msg.content = (last_msg.content + '\n\n---\n\n' + msg.content) if last_msg.content else msg.content
            if msg.reasoning_content:
                last_msg.reasoning_content = msg.reasoning_content
            if msg.tool_calls and len(msg.tool_calls) > 0:
                last_msg.tool_calls = msg.tool_calls
            merged_count += 1
            print(f'  RULE4 merge: [{len(cleaned)-1}] {msg.role} merged')
            continue
    
    cleaned.append(msg)
    last_role = msg.role

print(f'\nAfter Rules 1-4: {len(cleaned)} msgs (removed={removed_count} merged={merged_count})')
print()

# Rule 5: orphan assistant-with-tool_calls detection
print('=== RULE 5: Orphan assistant-with-tool_calls ===')
stripped_count = 0
for i, msg in enumerate(cleaned):
    if msg.role == 'assistant' and msg.tool_calls and len(msg.tool_calls) > 0:
        expected_ids = set(tc.get('id', '') for tc in msg.tool_calls)
        has_match = False
        for j in range(i + 1, len(cleaned)):
            nxt = cleaned[j]
            if nxt.role == 'tool' and nxt.tool_call_id and nxt.tool_call_id in expected_ids:
                has_match = True
                break
            # STOP at non-tool message!
            if nxt.role != 'tool':
                break
        
        if not has_match:
            print(f'  ORPHAN assistant [{i}]: tool_calls={[tc.get("function",{}).get("name","?") for tc in msg.tool_calls]}')
            print(f'    Expected IDs: {expected_ids}')
            # Show what follows
            for j in range(i + 1, min(i + 5, len(cleaned))):
                nxt = cleaned[j]
                print(f'    Next [{j}]: role={nxt.role} tcid={nxt.tool_call_id[:30] if nxt.role=="tool" else "N/A"}')
                if nxt.role != 'tool':
                    print(f'    -> STOP at non-tool [{j}]')
                    break
            msg.tool_calls = None
            msg.reasoning_content = None
            stripped_count += 1
            if not msg.content:
                print(f'    -> Also empty content, will be removed')
            print()

# Remove empty assistants after stripping
before_remove = len(cleaned)
cleaned = [m for m in cleaned if not (m.role == 'assistant' and not m.content and (not m.tool_calls or len(m.tool_calls) == 0))]
removed_empty = before_remove - len(cleaned)
print(f'Rule 5 result: stripped={stripped_count} removed_empty={removed_empty} remaining={len(cleaned)}')
print()

# Rule 6: remove orphan tool messages
print('=== RULE 6: Orphan tool messages ===')
valid_tc_ids = set()
for m in cleaned:
    if m.role == 'assistant' and m.tool_calls:
        for tc in m.tool_calls:
            if tc.get('id'):
                valid_tc_ids.add(tc['id'])

orphan_tools = []
for i, m in enumerate(cleaned):
    if m.role == 'tool' and m.tool_call_id and m.tool_call_id not in valid_tc_ids:
        orphan_tools.append((i, m.tool_call_id, m.name))

print(f'Valid tool_call_ids: {len(valid_tc_ids)}')
print(f'Orphan tools: {len(orphan_tools)}')
for idx, tcid, name in orphan_tools:
    print(f'  [{idx}] tcid={tcid[:40]} name={name}')

if orphan_tools:
    # Remove orphans (reverse to maintain indices)
    for idx, _, _ in reversed(orphan_tools):
        cleaned.pop(idx)

print(f'\nFinal after all rules: {len(cleaned)} messages')
print(f'Removed total: {len(msgs) - len(cleaned)}')
