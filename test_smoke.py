"""Run the Windows app against a local fixture and require real WebView2 results."""
import argparse
import ctypes
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shutil
from statistics import median
import subprocess
from tempfile import mkdtemp
from threading import Thread
from time import sleep
from urllib.parse import quote


def search(text):
    return 'https://duckduckgo.com/?q=' + quote(text, safe='')


# Must match the inputs listed in BrowserWindow.SmokeTest.
RESOLVE = {
    'example.com': 'https://example.com/',
    'example.com/a?b=c': 'https://example.com/a?b=c',
    'Example.COM:8443/x': 'https://example.com:8443/x',
    'localhost': 'http://localhost/',
    'localhost:3000': 'http://localhost:3000/',
    'localhost/app': 'http://localhost/app',
    '127.0.0.1:8080/x': 'http://127.0.0.1:8080/x',
    '[::1]:5173': 'http://[::1]:5173/',
    'https://example.com/path': 'https://example.com/path',
    'HTTP://Example.com': 'http://example.com/',
    '3.14': search('3.14'),
    'hello world': search('hello world'),
    'how to type http:// links': search('how to type http:// links'),
    'user@example.com': search('user@example.com'),
    'javascript:alert(1)': None,
    ' JavaScript:alert(1) ': None,
    'data:text/html,hi': None,
    'file:///C:/Windows/win.ini': None,
    'C:\\Windows\\win.ini': None,
    'mailto:a@example.com': None,
    'ftp://example.com': None,
    'http://': None,
    'https://user:pw@example.com': None,
    'user:pw@example.com': None,
    '': None,
    '   ': None,
}


# Must match the inputs in SmokeTest.
CLEAN_LINK = {
    'https://example.com/p?utm_source=x&utm_medium=y&id=1': 'https://example.com/p?id=1',
    'https://example.com/p?fbclid=abc': 'https://example.com/p',
    'https://example.com/p?b=two%20words&gclid=1&a=1+2': 'https://example.com/p?b=two%20words&a=1+2',
    'https://example.com/p?id=42&utm_source=smoke&fbclid=abc#keep': 'https://example.com/p?id=42#keep',
    'https://example.com/p?utm_campaign=z#sec': 'https://example.com/p#sec',
    'https://s3.example.com/obj?utm_source=x&X-Amz-Signature=deadbeef&id=1': 'https://s3.example.com/obj?utm_source=x&X-Amz-Signature=deadbeef&id=1',
    'https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=secret123': 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
    'https://example.com/page?si=keepme&id=2': 'https://example.com/page?si=keepme&id=2',
    'https://example.com/plain': 'https://example.com/plain',
    'https://example.com/search?q=hello&page=2#top': 'https://example.com/search?q=hello&page=2#top',
}


class Fixture(BaseHTTPRequestHandler):
    requested = []
    loop_seq = 0

    def do_GET(self):
        type(self).requested.append(self.path)
        if self.path == '/redirect-tracked':
            self.send_response(302)
            self.send_header('Location', '/second?fbclid=track123')
            self.send_header('Content-Length', '0')
            self.end_headers()
            return
        if self.path.startswith('/loop') and 'fbclid=' not in self.path:
            type(self).loop_seq += 1
            self.send_response(302)
            self.send_header('Location', f'/loop?fbclid=tok{type(self).loop_seq}')
            self.send_header('Content-Length', '0')
            self.end_headers()
            return
        if self.path == '/ad-worker.js':
            body = (
                "onconnect=e=>{const p=e.ports[0];"
                "fetch('http://sub.doubleclick.net/worker-pixel.js')"
                ".then(r=>p.postMessage(r.status))"
                ".catch(()=>p.postMessage('error'));};"
            ).encode()
            self.send_response(200)
            self.send_header('Content-Type', 'application/javascript; charset=utf-8')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if self.path.startswith('/ads'):
            body = (
                '<!doctype html><title>Ads</title>'
                '<div id="ad" class="adsbygoogle">ad</div><div id="ok">ok</div>'
                "<script>fetch('http://doubleclick.net/pixel.js')"
                ".then(r=>window.__adStatus=r.status)"
                ".catch(()=>window.__adStatus='error');"
                "const w=new SharedWorker('/ad-worker.js');"
                "w.port.onmessage=e=>window.__workerAdStatus=e.data;"
                "w.port.start();</script>"
            ).encode()
        else:
            if self.path.startswith('/first'):
                title, text = 'First page marker', 'First page marker'
            elif self.path.startswith('/third'):
                title, text = 'Third page marker', 'Third page body text'
            elif self.path.startswith('/loop'):
                title, text = 'Loop settled', 'Loop settled'
            elif self.path.startswith('/s') and self.path[2:].isdigit():
                title = text = f'Sleep page {self.path[2:]}'
            else:
                title, text = 'Second page marker', 'Second page body text'
            body = f'<!doctype html><title>{title}</title><main>{text}</main><input value="never export this"><p hidden>hidden marker</p>'.encode()
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (ConnectionAbortedError, BrokenPipeError):
            pass

    def log_message(self, *_):
        pass


def file_state(path):
    if path is None or not path.exists():
        return None
    stat = path.stat()
    return (stat.st_mtime_ns, stat.st_size)


def run_phase(exe, output, base, *extra):
    result = subprocess.run([str(exe), '--smoke-test', str(output), base, *extra], timeout=45)
    assert output.is_file(), f'Browser exited without a smoke report (exit {result.returncode})'
    report = json.loads(output.read_text(encoding='utf-8-sig'))
    print(json.dumps(report, indent=2))
    assert result.returncode == 0, report
    return report


def run():
    parser = argparse.ArgumentParser()
    parser.add_argument('exe', type=Path)
    args = parser.parse_args()
    assert args.exe.is_file(), f'Windows browser unavailable: {args.exe}'
    exe = args.exe.resolve()
    local_app_data = os.environ.get('LOCALAPPDATA')
    real_session = Path(local_app_data) / 'SearchWindows' / 'session.json' if local_app_data else None
    real_before = file_state(real_session)
    path = Path(mkdtemp(prefix='search-smoke-'))
    try:
        output = path / 'report.json'
        session_path = path / 'session.json'
        with ThreadingHTTPServer(('127.0.0.1', 0), Fixture) as server:
            thread = Thread(target=server.serve_forever)
            thread.start()
            base = f'http://127.0.0.1:{server.server_port}'
            try:
                bad_root = subprocess.run([str(exe), '--smoke-test', 'C:\\', base], timeout=15)
                assert bad_root.returncode != 0, f'Expected non-zero exit for drive-root report path, got {bad_root.returncode}'

                report = run_phase(exe, output, base)
                assert report['first'] == 'First page marker'
                assert report['second'] == 'Second page marker'
                assert report['restored'] == 'First page marker'
                assert report['pinnedSurvivesClose'] is True
                assert report['verticalAndHorizontal'] is True
                assert report.get('captionHidden') is True, report.get('captionHidden')
                assert report.get('hitTests') == {
                    'sideEmpty': 2,
                    'maxButton': 9,
                    'closeButton': 20,
                    'backButton': 1,
                    'tabRow': 1,
                    'topEdge': 12,
                    'stripEmpty': 2,
                }, report.get('hitTests')
                assert report.get('maximizeToggles') is True, report.get('maximizeToggles')
                assert report.get('chromeHiddenWorks') is True, report.get('chromeHiddenWorks')
                assert report.get('stripOverflowSafe') is True, report.get('stripOverflowSafe')
                assert report.get('stripWheelScrolled') is True, report.get('stripWheelScrolled')
                assert report.get('sideWheelStep') == 40, report.get('sideWheelStep')
                assert report.get('menuDeferredDispose') is True, report.get('menuDeferredDispose')
                assert report.get('ncTrackedOnDown') is True, report.get('ncTrackedOnDown')
                assert report.get('captionNoTooltips') is True, report.get('captionNoTooltips')
                assert report.get('updaterSelfCheck') is True, report.get('updaterSelfCheck')
                assert report.get('settingsRoundTrip') is True, report.get('settingsRoundTrip')
                assert report.get('phosphorLoaded') is True, report.get('phosphorLoaded')
                assert report.get('iconFontFamily') == 'Phosphor', report.get('iconFontFamily')
                assert 'Segoe' in (report.get('captionFontFamily') or ''), report.get('captionFontFamily')
                assert report.get('settingsDialogOk') is True, report.get('settingsDialogOk')
                assert report.get('shareToggleWorks') is True, report.get('shareToggleWorks')
                assert report.get('adStatusWhenBlockingOff') == 200, report.get('adStatusWhenBlockingOff')
                assert report.get('adVisibleWhenBlockingOff') is True, report.get('adVisibleWhenBlockingOff')
                assert report.get('adHiddenAfterBlockingOn') is True, report.get('adHiddenAfterBlockingOn')
                assert report.get('adStatusAfterBlockingOn') == 403, report.get('adStatusAfterBlockingOn')
                assert report.get('urlWhenCleanOff') == base + '/first?id=7&utm_source=keepme&fbclid=keep123#frag', report.get('urlWhenCleanOff')
                assert '/first?id=7&utm_source=keepme&fbclid=keep123' in Fixture.requested, Fixture.requested
                assert report.get('cliFlags') is True, report.get('cliFlags')
                assert (exe.parent / 'Phosphor-LICENSE.txt').is_file(), f'Missing Phosphor-LICENSE.txt next to {exe}'
                assert not (path / 'update-check.txt').exists(), 'Smoke test must skip update checks'
                assert report['snapshot']['text'] == 'First page marker', report
                assert report['snapshot']['url'] == base + '/first', report
                assert report['pipeOneUse'] is True
                assert report['rejectsLocalFiles'] is True
                assert report['resolve'] == RESOLVE, {k: v for k, v in report['resolve'].items() if RESOLVE.get(k, ...) != v}
                assert report['noTabNavigation'] is True
                assert report['runtimeVersion']
                assert report['adsFinalUrl'] == base + '/ads?id=42#keep', report
                assert '/ads?id=42' in Fixture.requested, Fixture.requested
                assert not any('utm_source' in p or 'fbclid' in p for p in Fixture.requested if p.startswith('/ads')), Fixture.requested
                assert report['adStatus'] == 403, report
                assert report['workerAdStatus'] == 403, report
                assert report['adHidden'] is True, report
                assert report['okVisible'] is True, report
                assert report['blockedCount'] == 2, report
                assert report['shield'] == {
                    'thirdPartyBlocked': True,
                    'firstPartyAllowed': True,
                    'firstPartySubdomainAllowed': True,
                    'topLevelNavExempt': True,
                    'firstPartyDuringNavAllowed': True,
                    'subframeAdBlocked': True,
                }, report['shield']
                assert report['redirectTracked'] == {
                    'openThrew': False,
                    'finalUrl': base + '/second',
                    'title': 'Second page marker',
                }, report['redirectTracked']
                assert report['loopGuard']['settled'] is True, report['loopGuard']
                assert report['loopGuard']['title'] == 'Loop settled', report['loopGuard']
                loop_hits = [p for p in Fixture.requested if p.startswith('/loop')]
                assert 2 <= len(loop_hits) < 10, loop_hits
                assert report['cleanLink'] == CLEAN_LINK, {k: v for k, v in report['cleanLink'].items() if CLEAN_LINK.get(k, ...) != v}

                assert session_path.is_file(), f'Missing session.json at {session_path}'
                raw_session = session_path.read_text(encoding='utf-8')
                for forbidden in ('Second page body text', 'Third page body text', 'hidden marker', 'never export this', 'SearchWindows-'):
                    assert forbidden not in raw_session, f'Unexpected content in session.json: {forbidden}'
                expected_tabs = [
                    {'url': base + '/first?omit=token#omit', 'title': 'First page marker', 'pinned': True},
                    {'url': base + '/second?a', 'title': 'Second page marker', 'pinned': False},
                    {'url': base + '/third', 'title': 'Third page marker', 'pinned': True},
                ]
                expected_session = {'tabs': expected_tabs, 'selected': 1, 'vertical': False}
                session = json.loads(raw_session)
                assert set(session.keys()) == {'tabs', 'selected', 'vertical'}, session
                assert [set(t.keys()) for t in session['tabs']] == [{'url', 'title', 'pinned'}] * 3, session
                assert session == expected_session, session

                settings_path = path / 'settings.json'
                assert settings_path.is_file(), f'Missing settings.json at {settings_path}'
                saved_settings = json.loads(settings_path.read_text(encoding='utf-8'))
                assert saved_settings == {
                    'theme': 'system',
                    'verticalTabs': False,
                    'blockingEnabled': True,
                    'cleanLinksEnabled': True,
                    'sleepingTabsEnabled': True,
                    'autoUpdateCheck': True,
                    'shareEnabled': True,
                }, saved_settings

                Fixture.requested.clear()
                restore = run_phase(exe, path / 'restore.json', base, 'restore')
                assert restore['selectedIndex'] == 1, restore
                assert restore['vertical'] is False, restore
                assert restore['horizontalLayout'] is True, restore
                assert restore['pinnedSurvivesClose'] is True, restore
                assert restore['pinnedFirst'] is True, restore
                assert [{k: t[k] for k in ('url', 'title', 'pinned')} for t in restore['tabs']] == expected_tabs, restore
                assert [t['loaded'] for t in restore['tabs']] == [False, True, False], restore
                # Only the selected tab may load; the browser's own favicon fetch is not a page load.
                assert [p for p in Fixture.requested if p != '/favicon.ico'] == ['/second?a'], Fixture.requested
                assert json.loads(session_path.read_text(encoding='utf-8')) == expected_session
                assert restore['canSaveSession'] is True, restore
                assert not list(path.glob('*.tmp')), list(path.glob('*.tmp'))

                # Second instance while another process holds the profile mutex must restore normally without writing session.json.
                kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
                kernel32.CreateMutexW.argtypes = [ctypes.c_void_p, ctypes.c_bool, ctypes.c_wchar_p]
                kernel32.CreateMutexW.restype = ctypes.c_void_p
                kernel32.ReleaseMutex.argtypes = [ctypes.c_void_p]
                kernel32.ReleaseMutex.restype = ctypes.c_bool
                kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
                kernel32.CloseHandle.restype = ctypes.c_bool
                lock_handle = kernel32.CreateMutexW(None, True, report['sessionLockName'])
                assert lock_handle and ctypes.get_last_error() == 0, f'Failed to acquire test mutex ({ctypes.get_last_error()})'
                try:
                    pretty_session = json.dumps(expected_session, indent=2)
                    session_path.write_text(pretty_session, encoding='utf-8')
                    locked_before = file_state(session_path)
                    Fixture.requested.clear()
                    locked = run_phase(exe, path / 'locked.json', base, 'restore')
                    assert locked['canSaveSession'] is False, locked
                    assert locked['selectedIndex'] == 1, locked
                    assert [{k: t[k] for k in ('url', 'title', 'pinned')} for t in locked['tabs']] == expected_tabs, locked
                    assert session_path.read_text(encoding='utf-8') == pretty_session
                    assert file_state(session_path) == locked_before
                    assert not list(path.glob('*.tmp')), list(path.glob('*.tmp'))
                finally:
                    kernel32.ReleaseMutex(lock_handle)
                    kernel32.CloseHandle(lock_handle)

                for name, payload in (('corrupt', '{not json'), ('empty', '')):
                    session_path.write_text(payload, encoding='utf-8')
                    fallback = run_phase(exe, path / f'{name}.json', base, 'restore')
                    assert len(fallback['tabs']) == 1, fallback
                    assert fallback['tabs'][0]['url'] is None, fallback
                    assert fallback['selectedIndex'] == 0, fallback

                perf_session = {
                    'tabs': [
                        {'url': f'{base}/p{i}', 'title': f'Page {i}', 'pinned': i <= 2}
                        for i in range(1, 21)
                    ],
                    'selected': 0,
                    'vertical': True,
                }
                perf_runs = []
                for idx in range(1, 4):
                    session_path.write_text(json.dumps(perf_session), encoding='utf-8')
                    Fixture.requested.clear()
                    perf = run_phase(exe, path / f'perf-{idx}.json', base, 'perf')
                    assert perf['tabCount'] == 20, perf
                    assert perf['webViewsCreated'] == 1, perf
                    assert perf['loadedTabs'] == 1, perf
                    assert perf['startupMs'] > 0, perf
                    assert perf['memoryMB'] > 0, perf
                    assert perf['processCount'] >= 2, perf
                    assert [p for p in Fixture.requested if p != '/favicon.ico'] == ['/p1'], Fixture.requested
                    perf_runs.append(perf)
                    label = 'run 1, warms caches' if idx == 1 else f'run {idx}'
                    print(
                        f"PERF startupMs={perf['startupMs']} envMs={perf['envMs']} restoreMs={perf['restoreMs']} "
                        f"drawTabsMs={perf['drawTabsMs']} titleUpdatesMs={perf['titleUpdatesMs']} "
                        f"webViewsCreated={perf['webViewsCreated']} memoryMB={perf['memoryMB']} "
                        f"processCount={perf['processCount']} ({label})"
                    )
                print(
                    f"PERF median startupMs={median(p['startupMs'] for p in perf_runs)} "
                    f"drawTabsMs={median(p['drawTabsMs'] for p in perf_runs)} "
                    f"titleUpdatesMs={median(p['titleUpdatesMs'] for p in perf_runs)} "
                    f"memoryMB={median(p['memoryMB'] for p in perf_runs)}"
                )

                sleep_dir = path / 'sleep'
                sleep_dir.mkdir()
                sleep_report = run_phase(exe, sleep_dir / 'sleep.json', base, 'sleep')
                assert sleep_report['suspended'] == [True, False, True, True, False], sleep_report
                assert sleep_report['pinnedSuspended'] is False, sleep_report
                assert sleep_report['resumedTitle'] == 'Sleep page 1', sleep_report
                print(
                    f"SLEEP memoryBeforeMB={sleep_report['memoryBeforeMB']} "
                    f"memoryAfterMB={sleep_report['memoryAfterMB']}"
                )
            finally:
                server.shutdown()
                thread.join()
        assert file_state(real_session) == real_before, 'Smoke test modified real session.json'
    finally:
        for attempt in range(40):
            try:
                shutil.rmtree(path)
                break
            except PermissionError:
                if attempt == 39:
                    raise
                sleep(0.25)


if __name__ == '__main__':
    run()
