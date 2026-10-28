"""离线测试辅助脚本；只造临时目录和模拟CLI响应，不把这些测试当Unity业务通过证据。"""
import contextlib
import importlib.util
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


def load(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class HelpersTests(unittest.TestCase):
    def test_discovery_skips_cache_and_finds_scenario(self):
        discovery = load('discover_tests')
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            project = root / 'Unity'
            (project / 'Assets').mkdir(parents=True)
            (project / 'ProjectSettings').mkdir()
            (project / 'ProjectSettings/EditorBuildSettings.asset').write_text('path: Assets/Start.unity')
            cli = root / 'Revolution.RevCLI'
            cli.mkdir()
            (cli / 'rev.py').touch()
            (cli / 'commands.json').write_text('{}')
            (cli / 'boss.scenario.json').write_text(json.dumps({'name': 'Boss', 'steps': []}))
            hidden = root / 'Library'
            hidden.mkdir()
            (hidden / 'hidden.scenario.json').write_text('{}')
            found = discovery.discover(root, 'Boss')
            self.assertEqual(len(found['projects']), 1)
            self.assertEqual(found['projects'][0]['buildScenes'], ['Assets/Start.unity'])
            self.assertEqual(len(found['automationCliCandidates']), 1)
            self.assertEqual(len(found['scenarios']), 1)
            self.assertEqual(found['scenarios'][0]['score'], 1)

    def run_response(self, data, response_success=True):
        runner = load('run_scenario')
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'rev.py').touch()
            (root / 'scenario.json').write_text('{}')
            argv = ['runner', '--automation', str(root), '--project', str(root), '--scenario',
                    str(root / 'scenario.json'), '--report-dir', str(root / 'report')]
            reply = json.dumps({'success': response_success, 'data': data})
            with patch.object(sys, 'argv', argv), patch.object(runner, 'invoke', side_effect=[
                    (0, '{"success":true}', ''), (0, reply, '')]), contextlib.redirect_stdout(io.StringIO()):
                code = runner.main()
            self.assertTrue((root / 'report/cli-stdout.json').is_file())
            return code

    def test_success_requires_all_evidence(self):
        good = dict(success=True, requested=1, completed=1, failedIterations=0, unityErrors=0, errors=[], cancelled=False)
        self.assertEqual(self.run_response(good), 0)
        for key, value in [('completed', 0), ('unityErrors', 1), ('failedIterations', 1),
                           ('success', False), ('cancelled', True), ('requested', 0), ('errors', ['failed'])]:
            bad = dict(good)
            bad[key] = value
            self.assertNotEqual(self.run_response(bad), 0, key)
        self.assertNotEqual(self.run_response(good, False), 0)


if __name__ == '__main__':
    unittest.main()
