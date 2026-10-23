# -*- coding: utf-8 -*-
"""
AI 玩家的"手"：只负责把决策执行成真实指针操作，本身不做任何策略判断。

子命令：
  state                     读出完整局面（面板/战斗状态/手牌/双方棋子/每张牌的合法落点）
  plan "<json>"             执行我给出的落子计划，然后开战、等结算、点继续、领奖励，返回结果
  click <target>            点击按钮
  refresh                   重开一局（回主界面 → 选关 → 第一章）

为什么决策不写在这里：
  这一轮要求是"AI 实时决策"，落点必须由外部逐步判断后下发，脚本只做"按指令点屏幕"，
  这样报告里每一条落子都能追溯到我当时的判断依据，而不是一条写死的启发式规则。
"""
import json
import os
import subprocess
import sys
import tempfile
import time

REV_MCP = r'g:\RevRoot\MyRevGame\RevGame_Client\UnityProject\Revolution.RevMCP\rev_mcp.py'
PROJECT = r'g:\RevRoot\MyRevGame\RevGame_Client\UnityProject\RevGame.Unity'
TMP = tempfile.mkdtemp(prefix='aiplayer_')


def call(action, args=None):
    cmd = [sys.executable, REV_MCP, '--project', PROJECT, 'call', 'gm', action]
    if args:
        path = os.path.join(TMP, 'a.json')
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(args, f, ensure_ascii=False)
        cmd += ['--json', path]
    proc = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8')
    try:
        res = json.loads(proc.stdout)
    except json.JSONDecodeError:
        return {'success': False, 'text': '非 JSON: ' + (proc.stdout or proc.stderr)[:200]}
    text = res.get('message', '')
    for key in ('response', 'data', 'result'):
        v = res.get(key)
        if isinstance(v, dict):
            for inner in ('message', 'text', 'result'):
                if isinstance(v.get(inner), str):
                    text = v[inner]
                    break
        elif isinstance(v, str):
            text = v
            break
    res['text'] = text
    return res


def text_of(action, args=None):
    res = call(action, args)
    return res['text'] if res.get('success') else None


def parse_pieces(raw):
    out = []
    if not raw or raw == 'none':
        return out
    for part in raw.split(';'):
        head, _, rest = part.partition('/')
        if len(head) < 3:
            continue
        side, pid = head[0], int(head[2:])
        kind, _, rest = rest.partition('/')
        star, _, rest = rest.partition('@')
        pos, _, rest = rest.partition('/')
        size, _, hp = rest.partition('/')
        cur, _, mx = hp.partition('-')
        x, y = (int(v) for v in pos.split(','))
        out.append({'side': side, 'id': pid, 'kind': kind, 'star': star, 'x': x, 'y': y,
                    'size': int(size), 'hp': int(cur), 'max': int(mx.split('/')[0]),
                    'king': 'king' in part, 'dead': 'dead' in part})
    return out


def parse_hand(raw):
    info = {'quota': 0, 'selected': 0, 'cards': []}
    if not raw:
        return info
    head, _, payload = raw.partition('牌=')
    for seg in head.split(','):
        seg = seg.strip()
        if seg.startswith('额度='):
            info['quota'] = int(seg.split('=', 1)[1])
        elif seg.startswith('选中='):
            info['selected'] = int(seg.split('=', 1)[1])
    payload = payload.strip()
    if payload and payload != 'none':
        for card in payload.split(','):
            cid, kind, star = card.split(':')
            info['cards'].append({'id': int(cid), 'kind': kind, 'star': star})
    return info


def parse_cells(raw):
    if not raw or ':' not in raw:
        return []
    body = raw.split(':', 1)[1]
    return [] if body == 'none' else [tuple(int(v) for v in c.split(',')) for c in body.split(';')]


def snapshot(with_cells=True):
    panel = text_of('RevGame/面板') or ''
    state = text_of('RevGame/战斗状态') or ''
    hand = parse_hand(text_of('RevGame/手牌') or '')
    pieces = parse_pieces(text_of('RevGame/棋子') or '')
    data = {
        'panel': panel,
        'state': state,
        'hand': hand,
        'mine': [p for p in pieces if p['side'] == 'P' and not p['dead']],
        'enemies': [p for p in pieces if p['side'] == 'E' and not p['dead']],
        'rewards': text_of('RevGame/奖励') or '',
    }
    if with_cells:
        data['legal'] = {}
        for card in hand['cards']:
            if call('RevGame/选牌', {'arg0': card['id']}).get('success'):
                data['legal'][str(card['id'])] = parse_cells(text_of('RevGame/落点') or '')
        call('RevGame/选牌', {'arg0': 0})
    return data


def place(card_id, x, y):
    before = snapshot(with_cells=False)
    if not call('RevGame/选牌', {'arg0': card_id}).get('success'):
        return False, '选牌失败'
    res = call('RevGame/点格子', {'arg0': x, 'arg1': y})
    if not res.get('success'):
        return False, res['text']
    after = snapshot(with_cells=False)
    ok = after['hand']['quota'] < before['hand']['quota'] and len(after['mine']) > len(before['mine'])
    return ok, '额度 %d->%d 我方 %d->%d' % (
        before['hand']['quota'], after['hand']['quota'], len(before['mine']), len(after['mine']))


def wait_panel(expect, timeout):
    end = time.time() + timeout
    while time.time() < end:
        if expect in (text_of('RevGame/面板') or ''):
            return True
        time.sleep(1.2)
    return False


cmd = sys.argv[1] if len(sys.argv) > 1 else 'state'

if cmd == 'state':
    print(json.dumps(snapshot(), ensure_ascii=False))

elif cmd == 'brief':
    # 精简局面：只打印决策需要的信息，避免整页合法落点刷屏
    d = snapshot()
    print('STATE', d['state'])
    print('HAND ', 'quota=%d' % d['hand']['quota'],
          ' '.join('#%d:%s' % (c['id'], c['kind']) for c in d['hand']['cards']) or 'none')
    print('MINE ', ' '.join('#%d:%s@%d,%d(%d)' % (p['id'], p['kind'], p['x'], p['y'], p['hp']) for p in d['mine']) or 'none')
    print('ENEMY', ' '.join('#%d:%s@%d,%d(%d)%s' % (p['id'], p['kind'], p['x'], p['y'], p['hp'], 'KING' if p['king'] else '') for p in d['enemies']) or 'none')
    print('LEGAL', {k: len(v) for k, v in d['legal'].items()})

elif cmd == 'plan':
    plan = json.loads(sys.argv[2])
    results = []
    for item in plan:
        card_id, x, y = item[0], item[1], item[2]
        ok, detail = place(card_id, x, y)
        results.append({'card': card_id, 'cell': [x, y], 'ok': ok, 'detail': detail})
        print('落 #%d -> (%d,%d) %s %s' % (card_id, x, y, 'OK' if ok else 'FAIL', detail), flush=True)
        if not ok:
            break
    call('RevGame/选牌', {'arg0': 0})

    res = call('RevGame/点击', {'arg0': 'PreparePanel/btnReady'})
    print('开战 -> ' + res['text'], flush=True)

    settle = wait_panel('SettlePanel', 240)
    after = {'panel': text_of('RevGame/面板') or '', 'state': text_of('RevGame/战斗状态') or ''}
    print('结算面板=%s 状态=%s' % (settle, after['state']), flush=True)

    cont = call('RevGame/点击', {'arg0': 'SettlePanel/btnContinue'})
    print('继续 -> ' + cont['text'], flush=True)
    time.sleep(1.2)

    panel = text_of('RevGame/面板') or ''
    reward = ''
    if 'RewardPanel' in panel:
        reward = text_of('RevGame/奖励') or ''
        call('RevGame/点击', {'arg0': 'RewardPanel/choice0'})
        print('奖励 -> ' + reward, flush=True)

    print(json.dumps({'placements': results, 'after': after, 'reward': reward,
                      'panel': text_of('RevGame/面板') or '',
                      'state': text_of('RevGame/战斗状态') or ''}, ensure_ascii=False))

elif cmd == 'has':
    # 用法：has <cardId> <x> <y> —— 落子前确认这个格子对这张牌真的合法
    card_id, x, y = int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4])
    call('RevGame/选牌', {'arg0': card_id})
    cells = parse_cells(text_of('RevGame/落点') or '')
    call('RevGame/选牌', {'arg0': 0})
    print('YES' if (x, y) in cells else 'NO')

elif cmd == 'zone':
    # 用法：zone <cardId> <x0> <y0> <x1> <y1> —— 只列出这个矩形内的合法落点
    card_id = int(sys.argv[2])
    x0, y0, x1, y1 = (int(v) for v in sys.argv[3:7])
    call('RevGame/选牌', {'arg0': card_id})
    cells = parse_cells(text_of('RevGame/落点') or '')
    call('RevGame/选牌', {'arg0': 0})
    print(' '.join('%d,%d' % (x, y) for x, y in cells if x0 <= x <= x1 and y0 <= y <= y1) or 'none')

elif cmd == 'shot':
    print(call('RevGame/截图', {'arg0': sys.argv[2]})['text'])

elif cmd == 'click':
    print(call('RevGame/点击', {'arg0': sys.argv[2]})['text'])

elif cmd == 'refresh':
    print(call('RevGame/返回主界面')['text'])
    wait_panel('HomePanel', 60)
    print(call('RevGame/点击', {'arg0': 'HomePanel/btnPlay'})['text'])
    wait_panel('LevelSelectPanel', 30)
    print(call('RevGame/点击', {'arg0': 'LevelSelectPanel/chapter1'})['text'])
    print(text_of('RevGame/战斗状态') or '')
