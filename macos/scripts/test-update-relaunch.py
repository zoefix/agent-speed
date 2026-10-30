#!/usr/bin/env python3
"""Exercise production updater replacement and deferred AppKit termination in isolated bundles."""
import argparse
import hashlib
import http.server
import json
import os
from pathlib import Path
import plistlib
import shutil
import signal
import subprocess
import sys
import threading
import time
import uuid

CORE_SOURCES = [
    'SemanticVersion.swift', 'Release.swift', 'UpdateSchedule.swift',
    'UpdateURLPolicy.swift', 'ZipDirectory.swift', 'Checksums.swift',
]
APP_SOURCES = [
    'UpdateController.swift', 'UpdateStore.swift', 'UpdateFailure.swift',
    'Install/UpdateInstaller.swift', 'Install/BundleReplacer.swift',
    'Install/ProcessRunner.swift', 'Install/UpdateWorkspace.swift',
    'Install/PackageValidator.swift', 'Network/UpdateHTTP.swift', 'Network/ReleaseFeedClient.swift',
]
PACKAGE = """// swift-tools-version: 6.0
import PackageDescription
let package = Package(
    name: "UpdateFinishIsolation", platforms: [.macOS(.v14)],
    products: [.executable(name: "UpdateFinishPipeline", targets: ["UpdateFinishPipeline"])],
    targets: [.target(name: "AgentSpeedCore"),
              .executableTarget(name: "UpdateFinishPipeline", dependencies: ["AgentSpeedCore"])])
"""


def repository_from_script():
    for parent in Path(__file__).resolve().parents:
        if (parent / 'macos/Package.swift').is_file():
            return parent
    raise RuntimeError('Pass --repository with the repository root')


def build(repository, fixtures, output):
    package = output / 'package'
    core = package / 'Sources/AgentSpeedCore'
    app = package / 'Sources/UpdateFinishPipeline'
    core.mkdir(parents=True)
    app.mkdir(parents=True)
    hashes = {}
    for names, relative, target in [
        (CORE_SOURCES, 'macos/Sources/AgentSpeedCore/Updates', core),
        (APP_SOURCES, 'macos/Sources/AgentSpeed/Updates', app),
    ]:
        for name in names:
            source = repository / relative / name
            data = source.read_bytes()
            (target / source.name).write_bytes(data)
            hashes[str(source.relative_to(repository))] = hashlib.sha256(data).hexdigest()
    for name in ['Harness.swift', 'FixtureConfiguration.swift']:
        shutil.copyfile(fixtures / name, app / name)
    (package / 'Package.swift').write_text(PACKAGE)
    (output / 'production-sources.json').write_text(json.dumps(hashes, indent=2) + '\n')
    with (output / 'build.log').open('wb') as log:
        process = subprocess.Popen(
            ['swift', 'build', '--package-path', str(package), '--product', 'UpdateFinishPipeline'],
            stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            code = process.wait(timeout=180)
        except subprocess.TimeoutExpired:
            # Only the process group created above belongs to this fixture build.
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()
            raise RuntimeError('Fixture build exceeded 180 seconds; see ' + str(output / 'build.log'))
    if code:
        raise RuntimeError('Fixture build failed; see ' + str(output / 'build.log'))
    location = subprocess.check_output(
        ['swift', 'build', '--package-path', str(package), '--show-bin-path'],
        text=True, timeout=30).strip().splitlines()[-1]
    return Path(location) / 'UpdateFinishPipeline'


def events(path):
    return [json.loads(line) for line in path.read_text().splitlines()] if path.exists() else []


def run(binary, label, output):
    case_id = uuid.uuid4().hex
    root = output / 'runs' / (label + '-' + case_id)
    root.mkdir(parents=True, mode=0o700)
    www = root / 'www'
    www.mkdir()
    requests = []

    class Handler(http.server.SimpleHTTPRequestHandler):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, directory=str(www), **kwargs)
        def log_message(self, fmt, *args):
            pass
        def do_GET(self):
            requests.append({'path': self.path, 'at': time.time()})
            if self.path not in ['/feed.json', '/AgentSpeed-macOS.zip', '/SHA256SUMS.txt']:
                self.send_error(404)
                return
            super().do_GET()

    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Handler)
    server.daemon_threads = True
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    port = server.server_address[1]
    base = 'http://127.0.0.1:' + str(port)
    bundle_id = 'io.github.zoefix.update-finish-isolation.r' + case_id
    suite = bundle_id + '.defaults'
    target = root / 'target' / 'Agent Speed.app'
    package = root / 'package' / 'Agent Speed.app'
    for app, version in [(target, '1.0.0'), (package, '1.1.0')]:
        contents = app / 'Contents'
        macos = contents / 'MacOS'
        macos.mkdir(parents=True)
        shutil.copy2(binary, macos / 'UpdateFinishPipeline')
        info = {'CFBundleIdentifier': bundle_id, 'CFBundleExecutable': 'UpdateFinishPipeline', 'CFBundlePackageType': 'APPL',
                'CFBundleName': 'Agent Speed Update Isolation', 'CFBundleShortVersionString': version, 'CFBundleVersion': version,
                'LSUIElement': True, 'ASOutputRoot': str(root), 'ASFeedURL': base + '/feed.json', 'ASDefaultsSuite': suite}
        (contents / 'Info.plist').write_bytes(plistlib.dumps(info))
        subprocess.run(['/usr/bin/codesign', '--force', '--sign', '-', str(app)], check=True, capture_output=True)
    archive = www / 'AgentSpeed-macOS.zip'
    subprocess.run(['/usr/bin/ditto', '-c', '-k', '--norsrc', '--keepParent', str(package), str(archive)], check=True)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    sums = www / 'SHA256SUMS.txt'
    sums.write_text(digest + '  AgentSpeed-macOS.zip\n')
    feed = {'tag_name': 'v1.1.0', 'name': 'Synthetic update 1.1.0', 'body': 'Isolated update completion test.',
            'draft': False, 'prerelease': False, 'assets': [
                {'name': archive.name, 'size': archive.stat().st_size, 'browser_download_url': base + '/' + archive.name},
                {'name': sums.name, 'size': sums.stat().st_size, 'browser_download_url': base + '/' + sums.name}]}
    (www / 'feed.json').write_text(json.dumps(feed))
    temporary = root / 'tmp'
    temporary.mkdir(mode=0o700)
    environment = dict(os.environ, TMPDIR=str(temporary) + '/')
    began = time.monotonic()
    try:
        with (root / 'old.stderr.log').open('wb') as err:
            proc = subprocess.Popen([str(target / 'Contents/MacOS/UpdateFinishPipeline')], stdout=subprocess.DEVNULL, stderr=err, env=environment)
            try:
                old_code = proc.wait(timeout=16)
            except subprocess.TimeoutExpired:
                proc.kill()
                old_code = proc.wait()
        marker = root / 'new-launch.json'
        until = time.monotonic() + 12
        while time.monotonic() < until and not marker.exists():
            time.sleep(0.1)
        if marker.exists():
            until = time.monotonic() + 12
            while time.monotonic() < until and not any(e['event'] == 'will_terminate' for e in events(root / 'new-events.jsonl')):
                time.sleep(0.1)
        marker_data = json.loads(marker.read_text()) if marker.exists() else None
        new_exited = False
        if marker_data:
            until = time.monotonic() + 12
            while time.monotonic() < until:
                try:
                    os.kill(marker_data['pid'], 0)
                except ProcessLookupError:
                    new_exited = True
                    break
                time.sleep(0.1)
        old_events = events(root / 'old-events.jsonl')
        new_events = events(root / 'new-events.jsonl')
        final_info = plistlib.loads((target / 'Contents/Info.plist').read_bytes())
        proof = {'label': label, 'root': str(root), 'oldPid': proc.pid, 'oldExitCode': old_code,
                 'newLaunch': marker_data, 'elapsedSeconds': time.monotonic() - began,
                 'installedVersion': final_info['CFBundleShortVersionString'], 'oldEvents': old_events, 'newEvents': new_events,
                 'requests': requests, 'bundleIdentifier': bundle_id, 'defaultsSuite': suite,
                 'productionAppTouched': False, 'newProcessExited': new_exited}
        (root / 'results.json').write_text(json.dumps(proof, indent=2) + '\n')
        assert old_code == 0, ('old exit', old_code, root)
        assert marker_data is not None, ('new version did not launch', root)
        assert marker_data['pid'] != proc.pid
        assert marker_data['version'] == '1.1.0'
        assert new_exited, ('new fixture process did not exit', root)
        assert marker_data['installedBefore'] == '1.1.0'
        assert marker_data['previousBefore'] == '1.0.0'
        assert marker_data['installedAfter'] == marker_data['previousAfter'] == 'nil'
        assert marker_data['justUpdatedTo'] == '1.1.0'
        old_names = [e['event'] for e in old_events]
        new_names = [e['event'] for e in new_events]
        assert 'ready_to_relaunch' in old_names and 'leaving_reply' in old_names and 'will_terminate' in old_names
        assert 'watchdog_exit' not in old_names
        assert 'will_terminate' in new_names and 'watchdog_exit' not in new_names
        assert final_info['CFBundleShortVersionString'] == '1.1.0'
        assert sorted(r['path'] for r in requests) == ['/AgentSpeed-macOS.zip', '/SHA256SUMS.txt', '/feed.json']
        proof['passed'] = True
        (root / 'results.json').write_text(json.dumps(proof, indent=2) + '\n')
        print(json.dumps({'passed': True, 'root': str(root), 'oldPid': proc.pid, 'oldExitCode': old_code,
                          'newPid': marker_data['pid'], 'version': marker_data['version'],
                          'flagsConsumed': True, 'seconds': round(proof['elapsedSeconds'], 3)}, indent=2))
        return proof
    finally:
        (root / 'http-requests.json').write_text(json.dumps(requests, indent=2) + '\n')
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repository', type=Path)
    parser.add_argument('--fixtures', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    if sys.platform != 'darwin':
        parser.error('Requires macOS with AppKit, LaunchServices and Xcode Swift 6')
    repository = (args.repository or repository_from_script()).resolve()
    fixtures = (args.fixtures or repository / 'macos/Tests/UpdateRelaunchIntegration').resolve()
    parent = (args.output or repository / 'artifacts/macos-update-relaunch').resolve()
    output = parent / ('run-' + uuid.uuid4().hex)
    output.mkdir(parents=True, mode=0o700)
    print('Retaining isolated update fixture evidence in ' + str(output), flush=True)
    binary = build(repository, fixtures, output)
    run(binary, 'fixed', output)
