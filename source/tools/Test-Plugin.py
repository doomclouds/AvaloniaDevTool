"""Install the plugin in an isolated Codex home and validate the desktop host's MCP catalog."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', required=True)
    parser.add_argument('--codex', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    repo, output = Path(args.repo).resolve(), Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    home = output / 'home'
    home.mkdir(exist_ok=True)
    env = os.environ.copy()
    env['CODEX_HOME'] = str(home)
    creation = subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0
    for label, command in (
        ('marketplace', ['plugin', 'marketplace', 'add', str(repo), '--json']),
        ('install', ['plugin', 'add', 'avalonia-devtool@avalonia-devtool', '--json']),
    ):
        result = subprocess.run([args.codex, *command], env=env, cwd=repo, capture_output=True,
                                text=True, encoding='utf-8', timeout=90, creationflags=creation)
        (output / (label + '.json')).write_text(result.stdout, encoding='utf-8')
        (output / (label + '.stderr.log')).write_text(result.stderr, encoding='utf-8')
        assert result.returncode == 0, result.stderr
    installed = Path(json.loads((output / 'install.json').read_text(encoding='utf-8'))['installedPath'])
    expected = {
        'plugin.json', 'mcp.json', 'mcp/AvaloniaDevTool.exe',
        'skills/avalonia-devtool/SKILL.md',
        'assets/avalonia.png', 'assets/README.md',
        'scripts/Install-AvaloniaDevTool.ps1', 'scripts/Manage-CodexMcp.ps1',
        'scripts/Codex-Mcp.ps1',
    }
    files = {file.relative_to(installed).as_posix() for file in installed.rglob('*') if file.is_file()}
    assert files == expected, {'unexpected': sorted(files - expected), 'missing': sorted(expected - files)}
    directories = {path.relative_to(installed).as_posix() for path in installed.rglob('*') if path.is_dir()}
    assert directories == {'assets', 'mcp', 'scripts', 'skills', 'skills/avalonia-devtool'}, directories
    plugin = repo / 'plugins/avalonia-devtool'
    for name in files:
        assert hashlib.sha256((installed / name).read_bytes()).digest() == hashlib.sha256((plugin / name).read_bytes()).digest(), name
    report = {'installedPath': str(installed), 'installedFiles': sorted(files), 'pluginVersion':
              json.loads((installed / 'plugin.json').read_text(encoding='utf-8'))['version']}
    lines = queue.Queue()
    log = open(output / 'app-server.stderr.log', 'w', encoding='utf-8')
    server = subprocess.Popen([args.codex, 'app-server', '--stdio'], env=env, cwd=repo,
                              stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log,
                              text=True, encoding='utf-8', creationflags=creation)
    def read_lines():
        for line in server.stdout:
            lines.put(line)
        lines.put(None)
    threading.Thread(target=read_lines, daemon=True).start()
    sequence = 0
    def send(message):
        server.stdin.write(json.dumps(message) + '\n')
        server.stdin.flush()
    def request(method, params, timeout=60):
        nonlocal sequence
        sequence += 1
        current = sequence
        send({'id': current, 'method': method, 'params': params})
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            line = lines.get(timeout=max(0.1, deadline - time.monotonic()))
            assert line is not None, 'Codex app-server exited before replying'
            message = json.loads(line)
            if message.get('id') == current:
                assert 'error' not in message, message
                return message['result']
            assert not ('id' in message and 'method' in message), message
        raise TimeoutError(method)
    try:
        request('initialize', {'clientInfo': {'name': 'avalonia-plugin-install-test', 'version': '1.0'},
                               'capabilities': {'experimentalApi': True}})
        send({'method': 'initialized'})
        status = request('mcpServerStatus/list', {'serverName': 'avalonia'})
        (output / 'mcp-status.json').write_text(json.dumps(status, ensure_ascii=False, indent=2), encoding='utf-8')
        servers = status['data']
        target = next(item for item in servers if item['name'] == 'avalonia')
        tools = target['tools']
        assert 'attach-to-app' in tools and 'tree' in tools and 'recording-start' in tools, target
        thread = request('thread/start', {'cwd': str(repo), 'ephemeral': True})
        thread_id = thread['thread']['id']
        selected = request('mcpServer/tool/call', {'threadId': thread_id, 'server': 'avalonia',
                                                 'tool': 'select-machine', 'arguments': {}})
        assert not selected.get('isError', False), selected
        selection = json.loads(next(block['text'] for block in selected['content'] if block['type'] == 'text'))
        assert selection['machine'] == 'local' and selection['state'] == 'ready', selection
        discovered = request('mcpServer/tool/call', {'threadId': thread_id, 'server': 'avalonia',
                                                   'tool': 'attach-to-app', 'arguments': {}})
        assert not discovered.get('isError', False), discovered
        report.update({'toolCount': len(tools), 'selectMachine': selected,
                       'discoverySucceeded': True, 'success': True})
    finally:
        (output / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        if server.poll() is None:
            server.stdin.close()
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                if os.name == 'nt':
                    subprocess.run(['taskkill', '/PID', str(server.pid), '/T', '/F'], capture_output=True)
                else:
                    server.kill()
                    server.wait()
        log.close()
    print(json.dumps({'success': report['success'], 'pluginVersion': report['pluginVersion'],
                      'installedFileCount': len(files), 'toolCount': report['toolCount']}))


if __name__ == '__main__':
    main()
