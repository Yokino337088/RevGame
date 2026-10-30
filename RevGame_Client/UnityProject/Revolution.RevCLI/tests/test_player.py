import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from revautomation.cli import parser, payload_of, validate_action
from revautomation.client import RevError, RevClient
from revautomation import player


class PlayerTests(unittest.TestCase):
    def test_completed_build_waits_for_fresh_heartbeat_without_resubmission(self):
        client = object.__new__(RevClient)
        client.mode, client.timeout = 'live', 2
        fresh = dict(sessionId='new')
        with patch.object(client, 'endpoint', side_effect=[None, None, fresh]), patch.object(client, '_editor_open', return_value=True), patch('revautomation.client.time.sleep'):
            self.assertEqual(client.ready_endpoint(), fresh)

    def test_build_cli_preserves_unicode_output_and_profile(self):
        args = parser().parse_args(['build', 'player', '--target', 'StandaloneWindows64', '--path', 'D:/测试包/a.exe',
                                   '--backend', 'il2cpp', '--automation', '--frame-timing'])
        payload = payload_of(args)
        validate_action('build.player', payload)
        self.assertEqual(payload['path'], 'D:/测试包/a.exe')
        self.assertTrue(payload['automation'])
        self.assertTrue(payload['frameTiming'])
        self.assertNotIn('development', payload)

    def test_wrong_build_parameter_rejected(self):
        with self.assertRaises(RevError):
            validate_action('build.player', dict(automation='yes'))

    def test_nonempty_launch_directory_never_overwritten(self):
        with tempfile.TemporaryDirectory() as temp:
            p = Path(temp)
            exe = p / 'fake.exe'
            exe.write_bytes(b'fixture')
            with patch.object(player.subprocess, 'Popen') as spawn:
                with self.assertRaises(RevError):
                    player.launch(str(exe), temp)
                spawn.assert_not_called()
                self.assertEqual(exe.read_bytes(), b'fixture')

    def test_stale_endpoint_rejected_before_http(self):
        with tempfile.TemporaryDirectory() as temp:
            p = Path(temp)
            (p / 'player.json').write_text(json.dumps(dict(pid=1, launchId='new')), encoding='utf-8')
            (p / 'runtime.json').write_text(json.dumps(dict(pid=1, launchId='old')), encoding='utf-8')
            with patch.object(player, 'request') as request:
                with self.assertRaises(RevError):
                    player.session(p)
                request.assert_not_called()

    def test_headless_and_endpoint_override_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            p = Path(temp)
            exe = p / 'fake.exe'
            exe.touch()
            for argument in ['-nographics', '-batchmode', '-revAutomationPort', '-logFile']:
                with self.assertRaises(RevError):
                    player.launch(str(exe), str(p / 'session'), arguments=[argument])

    def test_remote_endpoint_rejected(self):
        with self.assertRaises(RevError):
            player.request(dict(url='http://example.com', token='private', sessionId='id'), 'GET', '/health')


if __name__ == '__main__':
    unittest.main()
