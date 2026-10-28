#!/usr/bin/env python3
"""只读发现 Unity 工程、Automation CLI、启动场景和候选测试；不启动 Unity、不执行候选。

关键词匹配仅用于推荐。Agent 必须读候选前置条件、断言与清理后才决定执行。
"""
import argparse
import json
import re
from pathlib import Path

SKIP = {'.git', 'Library', 'Temp', 'Logs', 'obj', 'bin', 'node_modules', 'results', '__pycache__'}


def walk(root, depth):
    yield root
    if depth <= 0:
        return
    try:
        children = list(root.iterdir())
    except OSError:
        return
    for child in children:
        if child.is_dir() and child.name not in SKIP:
            yield from walk(child, depth - 1)


def discover(root, query='', depth=6):
    directories = list(walk(root, depth))
    tools = [str(d / 'rev.py') for d in directories if (d / 'rev.py').is_file() and (d / 'commands.json').is_file()]
    projects = []
    terms = re.findall(r'[a-z0-9]+|[\u4e00-\u9fff]+', query.lower())
    for d in directories:
        if not (d / 'Assets').is_dir() or not (d / 'ProjectSettings').is_dir():
            continue
        endpoint = None
        try:
            endpoint = json.loads((d / 'Library/RevolutionAutomation/endpoint.json').read_text(encoding='utf-8-sig'))
        except (OSError, ValueError):
            pass
        settings = d / 'ProjectSettings/EditorBuildSettings.asset'
        scenes = []
        if settings.exists():
            scenes = re.findall(r'path:\s*(Assets/[^\r\n]+\.unity)', settings.read_text(encoding='utf-8-sig'))
        projects.append({'project': str(d), 'bridgeInstalled': (d / 'Assets/Revolution/Editor/RevAutomation').is_dir(),
                         'endpointHint': endpoint, 'buildScenes': scenes})
    scenarios = []
    for directory in directories:
        try:
            files = list(directory.glob('*.scenario.json'))
        except OSError:
            continue
        for p in files:
            try:
                data = json.loads(p.read_text(encoding='utf-8-sig'))
                searchable = (p.name + ' ' + json.dumps(data, ensure_ascii=False)).lower()
                score = sum(term in searchable for term in terms)
                commands = [s.get('command') for phase in ('setup', 'steps', 'cleanup', 'teardown')
                            for s in data.get(phase, []) if isinstance(s, dict) and s.get('command')]
                scenarios.append({'file': str(p), 'name': data.get('name'), 'score': score,
                                  'repeat': data.get('repeat', 1), 'warmup': data.get('warmup', 0),
                                  'commands': list(dict.fromkeys(commands)),
                                  'hasCleanup': bool(data.get('cleanup')), 'hasTeardown': bool(data.get('teardown'))})
            except (OSError, ValueError, TypeError):
                continue
    return {'root': str(root), 'query': query, 'automationCliCandidates': tools,
            'projects': projects, 'scenarios': sorted(scenarios, key=lambda s: (-s['score'], s['file'])),
            'warning': 'endpoint仅是线索，不保证在线；候选匹配不是执行许可，必须核对真实业务与运行期GM。'}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root', type=Path, default=Path.cwd())
    p.add_argument('--query', default='')
    p.add_argument('--depth', type=int, default=6)
    args = p.parse_args()
    print(json.dumps(discover(args.root.resolve(), args.query, args.depth), ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
