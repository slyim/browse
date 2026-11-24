"""Run the Windows app against a local fixture and require real WebView2 results."""
import argparse
import ctypes
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shutil
from statistics import median
import struct
import subprocess
from tempfile import mkdtemp
from threading import Event, Thread
from time import sleep
import time
from urllib.parse import quote
import zlib

# The main phase runs ~200 checks; slower CI runners need well over a minute.
PHASE_TIMEOUT = int(os.environ.get('SMOKE_PHASE_TIMEOUT', '180'))


def make_png(size=16):
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xFFFFFFFF)
    raw = b''.join(b'\x00' + b'\x2a\x7f\xd4\xff' * size for _ in range(size))
    return (
        b'\x89PNG\r\n\x1a\n'
        + chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0))
        + chunk(b'IDAT', zlib.compress(raw))
        + chunk(b'IEND', b'')
    )


FAVICON_PNG = make_png()


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
    'site:github.com': search('site:github.com'),
    'std::vector': search('std::vector'),
    'filetype:pdf': search('filetype:pdf'),
    'define:x': search('define:x'),
    'javascript:alert(1)': None,
    ' JavaScript:alert(1) ': None,
    'data:text/html,hi': None,
    'file:///C:/Windows/win.ini': None,
    'C:\\Windows\\win.ini': None,
    'mailto:a@example.com': None,
    'ftp://example.com': None,
    'http://': None,
    'about:blank': None,
    'edge://settings': None,
    'blob:https://example.com/123': None,
    'https://user:pw@example.com': None,
    'user:pw@example.com': None,
    '': None,
    '   ': None,
}


# Must match the inputs in SmokeTest.
CLEAN_LINK = {
    'https://example.com/p?utm_source=x&utm_medium=y&id=1': 'https://example.com/p?id=1',
    'https://example.com/p?fbclid=abc': 'https://example.com/p',
    'https://shop.example/p?hmac=ab12&utm_source=x': 'https://shop.example/p?hmac=ab12&utm_source=x',
    'https://api.example/p?oauth_signature=s%3D&fbclid=y': 'https://api.example/p?oauth_signature=s%3D&fbclid=y',
    'https://example.com/p?b=two%20words&gclid=1&a=1+2': 'https://example.com/p?b=two%20words&a=1+2',
    'https://example.com/p?id=42&utm_source=smoke&fbclid=abc#keep': 'https://example.com/p?id=42#keep',
    'https://example.com/p?utm_campaign=z#sec': 'https://example.com/p#sec',
    'https://s3.example.com/obj?utm_source=x&X-Amz-Signature=deadbeef&id=1': 'https://s3.example.com/obj?utm_source=x&X-Amz-Signature=deadbeef&id=1',
    'https://www.youtube.com/watch?v=dQw4w9WgXcQ&si=secret123': 'https://www.youtube.com/watch?v=dQw4w9WgXcQ',
    'https://example.com/page?si=keepme&id=2': 'https://example.com/page?si=keepme&id=2',
    'https://example.com/plain': 'https://example.com/plain',
    'https://example.com/search?q=hello&page=2#top': 'https://example.com/search?q=hello&page=2#top',
}


SAMPLE_LISTS = {
    '/lists/easylist.txt': (
        '[Adblock Plus 2.0]\n'
        '! Sample EasyList for smoke test\n'
        '||doubleclick.net^$third-party\n'
        '||googlesyndication.com^$third-party\n'
        '||blocked-ads.test^$third-party,script\n'
        '||allowed-ads.test^\n'
        '@@||allowed-ads.test/ok.js$script\n'
        '||force-blocked.test^\n'
        '@@||force-blocked.test^\n'
        '||force-blocked.test/pixel.js$script,important\n'
        '/ads/banner-*.js^\n'
        '||fp-site.example/fp-track.js$~third-party\n'
        '! Unsupported options must be safely skipped\n'
        '||unsupported-opt.test^$redirect=noopjs\n'
        '||unsupported-opt.test^$csp=script-src none\n'
        '||unsupported-opt.test^$removeparam=foo\n'
        'example.com##+js(nobab)\n'
        'example.com##div:has-text(Sponsored)\n'
        '! Cosmetic rules & exceptions\n'
        '##.adsbygoogle\n'
        '##.smoke-ad-banner\n'
        '##.smoke-exempt-ad\n'
        '127.0.0.1##.smoke-local-ad\n'
        '127.0.0.1#@#.smoke-exempt-ad\n'
        'example.org,~sub.example.org##.promo-box\n'
    ),
    '/lists/easyprivacy.txt': (
        '[Adblock Plus 2.0]\n'
        '! Sample EasyPrivacy for smoke test\n'
        '||segment.com^$third-party\n'
        '||google-analytics.com^$third-party\n'
        '||scoped-tracker.test^$script,domain=news.example.com|~safe.news.example.com\n'
        '||beacon.tracker.test^$ping,third-party\n'
        '||pixel.quantserve.com^$image,third-party\n'
    ),
    '/lists/ublock-filters.txt': (
        '! Sample uBlock filters for smoke test\n'
        '||ads.example.net^$3p,xhr\n'
        '||analytics.tiktok.com^$3p\n'
        '##[id^="div-gpt-ad"]\n'
    ),
    '/lists/peter-lowe.txt': (
        '! Sample hosts + ABP entries for smoke test\n'
        '# comment line\n'
        '0.0.0.0 hosts-tracker.test # inline comment\n'
        '127.0.0.1 localhost\n'
        '||adservice.google.com^\n'
    ),
    '/lists/public_suffix_list.dat': (
        '// Sample PSL for smoke test\n'
        'com\n'
        'net\n'
        'org\n'
        'uk\n'
        'co.uk\n'
        'org.uk\n'
        'au\n'
        'com.au\n'
        'github.io\n'
        '*.ck\n'
        '!www.ck\n'
    ),
}


class Fixture(BaseHTTPRequestHandler):
    requested = []
    post_requests = []
    list_not_modified = 0
    loop_seq = 0
    stop_event = Event()

    def do_POST(self):
        length = int(self.headers.get('Content-Length') or '0')
        raw = self.rfile.read(length).decode('utf-8', errors='replace')
        type(self).post_requests.append((self.path, raw))
        safe = raw.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
        body = f'<!doctype html><title>Post received</title><main id="echo">{safe}</main>'.encode()
        self.send_response(200)
        self.send_header('Content-Type', 'text/html; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (ConnectionAbortedError, BrokenPipeError):
            pass

    def do_GET(self):
        type(self).requested.append(self.path)
        if self.path in SAMPLE_LISTS:
            etag = f'"smoke-{self.path.rsplit("/", 1)[-1]}"'
            if self.headers.get('If-None-Match') == etag:
                type(self).list_not_modified += 1
                self.send_response(304)
                self.send_header('ETag', etag)
                self.end_headers()
                return
            data = SAMPLE_LISTS[self.path].encode('utf-8')
            self.send_response(200)
            self.send_header('Content-Type', 'text/plain; charset=utf-8')
            self.send_header('ETag', etag)
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            try:
                self.wfile.write(data)
            except (ConnectionAbortedError, BrokenPipeError):
                pass
            return
        if self.path.startswith('/download/'):
            filename = self.path.rsplit('/', 1)[-1]
            if filename.endswith('.exe'):
                data = b'MZ-fake-smoke-test'
            elif filename == 'sample.txt':
                data = b'browse-download-smoke-payload-0123456789'
            else:
                data = b'hello-download'
            self.send_response(200)
            self.send_header('Content-Type', 'application/octet-stream')
            self.send_header('Content-Disposition', f'attachment; filename="{filename}"')
            self.send_header('Content-Length', str(len(data)))
            self.end_headers()
            try:
                self.wfile.write(data)
            except (ConnectionAbortedError, BrokenPipeError):
                pass
            return
        if self.path == '/favicon.ico':
            self.send_response(200)
            self.send_header('Content-Type', 'image/png')
            self.send_header('Content-Length', str(len(FAVICON_PNG)))
            self.end_headers()
            try:
                self.wfile.write(FAVICON_PNG)
            except (ConnectionAbortedError, BrokenPipeError):
                pass
            return
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
        if self.path == '/ad-worker.js' or self.path.startswith('/ad-worker-dedup.js'):
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
        if self.path.startswith('/popup-opener'):
            body = (
                '<!doctype html><title>Popup opener</title>'
                '<button id="open-direct" style="position:fixed;left:0;top:0;width:120px;height:40px"'
                ' onclick="window.__popup=window.open(\'/popup-child?utm_source=smoke&msg=ok\',\'oauth\')">Direct</button>'
                '<button id="open-blank" style="position:fixed;left:140px;top:0;width:120px;height:40px"'
                ' onclick="const w=window.open(\'about:blank\',\'oauth_blank\');'
                'if(w)w.location.href=\'/popup-child?utm_source=smoke2&msg=blank-ok\';">Blank</button>'
                '<button id="open-blank-stay" style="position:fixed;left:280px;top:0;width:120px;height:40px"'
                ' onclick="window.__blankPopup=window.open(\'about:blank\',\'oauth_stay\')">Stay</button>'
                '<script>window.__msgs=[];window.addEventListener(\'message\',e=>window.__msgs.push(e.data));'
                'window.open(\'/popup-child?msg=untrusted\',\'untrusted\');</script>'
            ).encode()
        elif self.path.startswith('/popup-child'):
            body = (
                '<!doctype html><title>Popup child</title>'
                '<script>const m=new URLSearchParams(location.search).get(\'msg\')||\'ok\';'
                'if(window.opener)window.opener.postMessage(m,\'*\');window.close();</script>'
            ).encode()
        elif self.path.startswith('/post-form'):
            body = (
                '<!doctype html><title>Post form</title>'
                '<form id="f" method="post" action="/post-target?utm_source=email&id=1">'
                '<input name="msg" value="hello-post"></form>'
            ).encode()
        elif self.path.startswith('/blob-page'):
            body = (
                '<!doctype html><title>Blob page</title>'
                '<button id="open-blob" style="position:fixed;left:0;top:0;width:120px;height:40px"'
                ' onclick="openBlob()">Blob</button>'
                '<script>function openBlob(){'
                'window.__blobClicked=true;'
                'const b=new Blob(["<!doctype html><title>Blob doc</title><main id=b>blob-ok</main>"],{type:"text/html"});'
                'window.__blobUrl=URL.createObjectURL(b);'
                'window.open(window.__blobUrl,"blob_win");'
                '}</script>'
            ).encode()
        elif self.path.startswith('/download-page'):
            body = (
                '<!doctype html><title>Download page</title>'
                '<a id="dl-exe" href="/download/tool.exe" download="tool.exe"'
                ' style="position:fixed;left:0;top:0;width:120px;height:40px;display:block">Download</a>'
                '<a id="dl-safe" href="/download/sample.txt" download="sample.txt"'
                ' style="position:fixed;left:140px;top:0;width:120px;height:40px;display:block">Safe</a>'
            ).encode()
        elif self.path.startswith('/external-scheme'):
            body = (
                '<!doctype html><title>External scheme</title>'
                '<a id="open-ext" href="browse-smoke:test"'
                ' style="position:fixed;left:0;top:0;width:120px;height:40px;display:block">Open app</a>'
            ).encode()
        elif self.path.startswith('/geo'):
            body = (
                '<!doctype html><title>Geo</title>'
                '<script>navigator.geolocation.getCurrentPosition(()=>{},()=>{});</script>'
            ).encode()
        elif self.path.startswith('/hang'):
            body = (
                '<!doctype html><title>Hung page</title><main>Hung page</main>'
                '<script>window.addEventListener("load", () => setTimeout(() => { while (true) {} }, 50));</script>'
            ).encode()
        elif self.path.startswith('/find-page'):
            body = (
                '<!doctype html><title>Find page</title>'
                '<main>alpha Alpha alpha beta alphabet</main>'
            ).encode()
        elif self.path.startswith('/slow'):
            type(self).stop_event.wait(3.0)
            body = b'<!doctype html><title>Slow page</title><main>Slow page done</main>'
        elif self.path.startswith('/filter-test'):
            body = (
                '<!doctype html><title>Filter test</title>'
                '<div id="cos-generic" class="smoke-ad-banner">generic</div>'
                '<div id="cos-domain" class="smoke-local-ad">domain</div>'
                '<div id="cos-exempt" class="smoke-exempt-ad">exempt</div>'
                '<div id="cos-ok">ok</div>'
                '<script>'
                "const s1=document.createElement('script');s1.src='http://blocked-ads.test/ad.js';"
                "s1.onload=()=>window.__blockedScriptStatus=200;s1.onerror=()=>window.__blockedScriptStatus=403;"
                'document.head.appendChild(s1);'
                "const s2=document.createElement('script');s2.src='http://allowed-ads.test/ok.js';"
                "s2.onload=()=>window.__allowedScriptStatus=200;s2.onerror=()=>window.__allowedScriptStatus=403;"
                'document.head.appendChild(s2);'
                '</script>'
            ).encode()
        elif self.path.startswith('/worker-dedup'):
            body = (
                '<!doctype html><title>Worker dedup</title>'
                '<div id="ad" class="adsbygoogle">ad</div><div id="ok">ok</div>'
                "<script>fetch('http://doubleclick.net/pixel.js')"
                ".then(r=>window.__adStatus=r.status)"
                ".catch(()=>window.__adStatus='error');"
                "const w=new SharedWorker('/ad-worker-dedup.js?t='+Date.now());"
                "w.port.onmessage=e=>window.__workerAdStatus=e.data;"
                "w.port.start();</script>"
            ).encode()
        elif self.path.startswith('/ads'):
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
    started = time.monotonic()
    result = subprocess.run([str(exe), '--smoke-test', str(output), base, *extra], timeout=PHASE_TIMEOUT)
    print(f'PHASE {extra[0] if extra else "main"} took {time.monotonic() - started:.1f}s', flush=True)
    assert output.is_file(), f'Browser exited without a smoke report (exit {result.returncode})'
    report = json.loads(output.read_text(encoding='utf-8-sig'))
    print(json.dumps(report, indent=2))
    assert result.returncode == 0, report
    return report


# WebView2 only raises LaunchingExternalUriScheme for schemes registered with Windows, and CI
# runners have no mail app, so the live test uses its own per-user scheme (launch is suppressed).
SMOKE_SCHEME_KEY = r'Software\Classes\browse-smoke'


def register_smoke_scheme():
    import winreg
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, SMOKE_SCHEME_KEY) as key:
        winreg.SetValueEx(key, None, 0, winreg.REG_SZ, 'URL:browse smoke test')
        winreg.SetValueEx(key, 'URL Protocol', 0, winreg.REG_SZ, '')
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, SMOKE_SCHEME_KEY + r'\shell\open\command') as key:
        winreg.SetValueEx(key, None, 0, winreg.REG_SZ, r'"%SystemRoot%\System32\cmd.exe" /c exit')


def unregister_smoke_scheme():
    import winreg
    for sub in (r'\shell\open\command', r'\shell\open', r'\shell', ''):
        try:
            winreg.DeleteKey(winreg.HKEY_CURRENT_USER, SMOKE_SCHEME_KEY + sub)
        except FileNotFoundError:
            pass


def run():
    register_smoke_scheme()
    try:
        run_checks()
    finally:
        unregister_smoke_scheme()


def run_checks():
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

                existing_file = path / 'not-a-dir.txt'
                existing_file.write_text('x', encoding='utf-8')
                for bad_args in (
                    ['--profile'],
                    ['--profile', ''],
                    ['--profile', '   '],
                    ['--profile', '--no-update-check'],
                    ['--profile', str(path / 'bad|profile')],
                    ['--profile', str(path / 'bad?profile')],
                    ['--profile', str(path / 'bad<profile>')],
                    ['--profile', 'bad:profile'],
                    ['--profile', str(existing_file)],
                    ['javascript:alert(1)'],
                    ['file:///C:/Windows/win.ini'],
                    ['mailto:a@example.com'],
                    ['ms-msdt:/id'],
                    ['--unknown-flag'],
                ):
                    bad_profile = subprocess.run([str(exe), *bad_args], timeout=15)
                    assert bad_profile.returncode == 2, f'Expected exit 2 for {bad_args}, got {bad_profile.returncode}'

                report = run_phase(exe, output, base)
                assert report['first'] == 'First page marker'
                assert report['second'] == 'Second page marker'
                assert report['restored'] == 'First page marker'
                assert report['pinnedSurvivesClose'] is True
                assert report['verticalAndHorizontal'] is True, report.get('orientationDetail')
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
                assert report.get('cliUrl') == {
                    'validHttp': True,
                    'validHttpsWithFlags': True,
                    'validFileUri': True,
                    'validLocalPath': True,
                    'rejectsBadInputs': True,
                    'localHtmlOpensSafely': True,
                }, report.get('cliUrl')
                assert report.get('browserRegistration') == {
                    'notInitially': True,
                    'writtenOk': True,
                    'removedOk': True,
                }, report.get('browserRegistration')
                assert report.get('externalSchemes') == {
                    'dangerousDenied': True,
                    'bgDecisionDenied': True,
                    'promptDecision': True,
                    'rememberedDecision': True,
                    'opaqueOriginNoSite': True,
                    'liveBgDenied': True,
                    'livePromptDenyOk': True,
                    'liveRememberOk': True,
                    'liveRememberedSkipsPrompt': True,
                    'liveClearRePrompts': True,
                }, report.get('externalSchemes')
                assert (exe.parent / 'Phosphor-LICENSE.txt').is_file(), f'Missing Phosphor-LICENSE.txt next to {exe}'
                assert not (path / 'update-check.txt').exists(), 'Smoke test must skip update checks'
                assert report.get('faviconCached') is True, report.get('faviconCached')
                assert report.get('addressDisplayOk') is True, report.get('addressDisplayOk')
                assert report.get('tabDrag') == {
                    'sidebarReorder': True,
                    'plainClickSelects': True,
                    'escapeCancels': True,
                    'outsideCancels': True,
                    'sidebarDragToPinEmpty': True,
                    'sidebarDragToPinSlot': True,
                    'sidebarPinGridReorder': True,
                    'sidebarDragToUnpin': True,
                    'stripPin': True,
                    'stripUnpin': True,
                    'stripReorder': True,
                    'ctrlDigitFollowsOrder': True,
                    'keyboardReorder': True,
                    'sessionOrderMatches': True,
                }, report.get('tabDrag')
                assert report.get('postClean') == {
                    'url': base + '/post-target?utm_source=email&id=1',
                    'echo': 'msg=hello-post',
                }, report.get('postClean')
                assert ('/post-target?utm_source=email&id=1', 'msg=hello-post') in Fixture.post_requests, Fixture.post_requests
                assert report.get('blobUrl') == {
                    'openedOk': True,
                    'notShareableOrSaved': True,
                    'helperOk': True,
                }, report.get('blobUrl')
                assert report.get('permissions') == {
                    'geoObserved': True,
                    'geoSavesInProfileFalse': True,
                    'policyOk': True,
                }, report.get('permissions')
                assert report.get('downloads') == {
                    'bgCancelled': True,
                    'exeDenied': True,
                    'exeAllowed': True,
                    'defaultDialogSuppressed': True,
                    'safeCompleted': True,
                    'ctrlJOpened': True,
                    'historyRoundTrip': True,
                    'removeDeletesEntry': True,
                    'corruptLoadsEmpty': True,
                    'extCheckOk': True,
                }, report.get('downloads')
                assert report.get('processCrash') == {
                    'sawRenderExit': True,
                    'navReset': True,
                    'recoveredAlive': True,
                    'tryCoreGuards': True,
                    'recoveryPreservesUrl': True,
                }, report.get('processCrash')
                debounce = report.get('sessionDebounce')
                assert debounce is not None, report
                assert debounce['titleEvents'] >= 5, debounce
                assert debounce['writesDuringBurst'] <= 1, debounce
                assert 1 <= debounce['writesAfterDebounce'] <= 2, debounce
                assert debounce['savedAnimTitle'] is True, debounce
                assert debounce['shareDeleteOk'] is True, debounce
                assert debounce['retryOk'] is True, debounce
                assert report.get('restoreShortcuts') == {
                    'restoringDuringEnv': True,
                    'shortcutsIgnored': True,
                }, report.get('restoreShortcuts')
                assert report.get('tabFixes') == {
                    'switchFocusesWebView': True,
                    'ctrl2FocusesWebView': True,
                    'escReturnsToWebView': True,
                    'redirectUpdatesAddress': True,
                    'unloadedReloadUsesNewUrl': True,
                    'uncommittedReloadUsesNewUrl': True,
                    'closeBSelectsD': True,
                    'closeDSelectsA': True,
                    'sidebarScrollsSelected': True,
                    'stripScrollsSelected': True,
                    'middleReleaseOutsideKeepsTab': True,
                    'leftTitleToCloseDotKeepsTab': True,
                    'middleClickInsideClosesTab': True,
                }, report.get('tabFixes')
                assert report.get('highDpi') == {
                    'perMonitorV2': True,
                    'minSizeScaled': True,
                    'liveMinLayoutOk': True,
                    'simulatedDpiOk': True,
                    'shareDialogOk': True,
                    'settingsDialogDpiOk': True,
                    'externalAppDialogOk': True,
                }, report.get('highDpi')
                assert report.get('shareTimeout') == {
                    'disabledDuringCapture': True,
                    'timedOut': True,
                    'elapsedOk': True,
                    'reEnabledAfter': True,
                    'shareWorksAfterTimeout': True,
                }, report.get('shareTimeout')
                assert report.get('tabCycle') == {
                    'ctrlTabOrderAndWrap': True,
                    'ctrlShiftTabOrderAndWrap': True,
                    'ctrlPageDownOrderAndWrap': True,
                    'ctrlPageUpOrderAndWrap': True,
                    'pageCtrlTabCycles': True,
                }, report.get('tabCycle')
                assert report.get('loadingStop') == {
                    'buttonSwappedToStop': True,
                    'tabShowsLoading': True,
                    'timerActiveWhileLoading': True,
                    'stopReturnedToReload': True,
                    'escStoppedLoading': True,
                    'escIdleNotConsumed': True,
                }, report.get('loadingStop')
                assert report.get('tabMenuAndNewWindow') == {
                    'copyLinkOk': True,
                    'duplicateOk': True,
                    'reloadOk': True,
                    'muteUnmuteOk': True,
                    'audioIndicatorOk': True,
                    'closeBelowOk': True,
                    'closeOthersOk': True,
                    'ctrlNLaunched': True,
                    'newWindowArgsOk': True,
                }, report.get('tabMenuAndNewWindow')
                assert report.get('findInPage') == {
                    'openedOk': True,
                    'pageShortcutOk': True,
                    'countOk': True,
                    'nextWrapOk': True,
                    'prevWrapOk': True,
                    'caseOk': True,
                    'wholeWordOk': True,
                    'escOk': True,
                    'tabSwitchStops': True,
                    'navStops': True,
                }, report.get('findInPage')
                assert report.get('zoomAndPrint') == {
                    'sitePrefsSelfCheck': True,
                    'zoomStepsOk': True,
                    'zoomSavedOnDisk': True,
                    'otherOriginResetOk': True,
                    'backOriginRestoredOk': True,
                    'zoomSurvivesReload': True,
                    'zoomResetKeyOk': True,
                    'zoomPillResetOk': True,
                    'wheelZoomCapturedOk': True,
                    'printSaveWiredOk': True,
                }, report.get('zoomAndPrint')
                fe = report.get('filterEngine')
                assert fe is not None, report
                for key in (
                    'listsDownloadedOk', 'etagStatusesOk', 'pslOk', 'rulesOk',
                    'liveFilterRulesOk', 'workerDedupOk', 'shieldBtnOk', 'shieldFlyoutOk',
                    'perSiteToggleOffOk', 'perSiteToggleBackOnOk', 'corruptFallbackOk',
                ):
                    assert fe.get(key) is True, (key, fe)
                assert Fixture.list_not_modified >= 5, Fixture.list_not_modified
                bench = fe.get('benchmark')
                assert bench is not None and bench['Samples'] >= 3000, bench
                assert bench['P95Ms'] < 0.2, bench
                assert bench['EngineMemoryMB'] > 0, bench
                assert (path / 'FilterLists' / 'easylist.txt').is_file()
                assert (path / 'FilterLists' / 'psl.txt').is_file()
                assert (path / 'FilterLists' / 'metadata.json').is_file()
                assert not list((path / 'FilterLists').glob('*.tmp'))
                favicon_files = list((path / 'Favicons').glob('*.png'))
                assert len(favicon_files) == 1, favicon_files
                assert favicon_files[0].read_bytes().startswith(b'\x89PNG\r\n\x1a\n')
                assert not list((path / 'Favicons').glob('*.tmp'))
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
                assert report['popup'] == {
                    'directOk': True,
                    'blankNavOk': True,
                    'untrustedBlocked': True,
                    'blankSessionClean': True,
                    'blankClosedBackToOpener': True,
                    'allowlistOk': True,
                }, report['popup']
                assert '/popup-child?utm_source=smoke&msg=ok' in Fixture.requested, Fixture.requested
                assert '/popup-child?utm_source=smoke2&msg=blank-ok' in Fixture.requested, Fixture.requested
                assert not any('untrusted' in p for p in Fixture.requested), Fixture.requested
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
                    'allowedExternalApps': [],
                    'disabledFilterLists': [],
                    'customFilterLists': [],
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
                assert [t['hasFavicon'] for t in restore['tabs']] == [True, True, True], restore
                # Only the selected tab may load; the browser's own favicon fetch is not a page load.
                assert [p for p in Fixture.requested if p != '/favicon.ico'] == ['/second?a'], Fixture.requested
                assert json.loads(session_path.read_text(encoding='utf-8')) == expected_session
                assert restore['canSaveSession'] is True, restore
                assert '(tabs not saved)' not in restore['windowTitle'], restore
                assert restore['sessionNoticeVisible'] is False, restore
                assert restore['restoringDuringEnv'] is True, restore
                assert restore['restoringCleared'] is True, restore
                assert restore['shieldDisabledRestored'] is True, restore
                assert restore['shieldUiOffRestored'] is True, restore
                assert restore['noCosmeticAfterRestore'] is True, restore
                assert restore['cachedListsLoaded'] is True, restore
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
                    assert '(tabs not saved)' in locked['windowTitle'], locked
                    assert locked['sessionNoticeVisible'] is True, locked
                    assert locked['selectedIndex'] == 1, locked
                    assert [{k: t[k] for k in ('url', 'title', 'pinned')} for t in locked['tabs']] == expected_tabs, locked
                    assert session_path.read_text(encoding='utf-8') == pretty_session
                    assert file_state(session_path) == locked_before
                    assert not list(path.glob('*.tmp')), list(path.glob('*.tmp'))
                finally:
                    kernel32.ReleaseMutex(lock_handle)
                    kernel32.CloseHandle(lock_handle)

                # #34: second instance takes over session saving once the first window releases the lock.
                takeover_handle = kernel32.CreateMutexW(None, True, report['sessionLockName'])
                assert takeover_handle and ctypes.get_last_error() == 0, f'Failed to acquire takeover mutex ({ctypes.get_last_error()})'
                takeover_released = False
                takeover_report_path = path / 'takeover.json'
                takeover_ready = path / 'takeover.ready'
                takeover_signal = path / 'takeover.released'
                session_path.write_text(pretty_session, encoding='utf-8')
                takeover_proc = subprocess.Popen([str(exe), '--smoke-test', str(takeover_report_path), base, 'takeover'])
                try:
                    for _ in range(400):
                        if takeover_ready.is_file() or takeover_proc.poll() is not None:
                            break
                        sleep(0.05)
                    assert takeover_ready.is_file(), f'Takeover process did not signal ready (exit {takeover_proc.poll()})'
                    assert session_path.read_text(encoding='utf-8') == pretty_session
                    kernel32.ReleaseMutex(takeover_handle)
                    kernel32.CloseHandle(takeover_handle)
                    takeover_released = True
                    takeover_signal.write_text('ok', encoding='utf-8')
                    takeover_proc.wait(timeout=20)
                    assert takeover_report_path.is_file(), f'Missing takeover report (exit {takeover_proc.returncode})'
                    takeover = json.loads(takeover_report_path.read_text(encoding='utf-8-sig'))
                    print(json.dumps(takeover, indent=2))
                    assert takeover_proc.returncode == 0, takeover
                    assert takeover['lockedInitially'] is True, takeover
                    assert '(tabs not saved)' in takeover['titleWhenLocked'], takeover
                    assert takeover['noticeWhenLocked'] is True, takeover
                    assert takeover['stillLockedBeforeRelease'] is True, takeover
                    assert takeover['tookOver'] is True, takeover
                    assert '(tabs not saved)' not in takeover['titleAfterTakeover'], takeover
                    assert takeover['noticeAfterTakeover'] is False, takeover
                    taken_session = json.loads(session_path.read_text(encoding='utf-8'))
                    assert any(t['url'] == base + '/third?from=b' for t in taken_session['tabs']), taken_session
                    assert not list(path.glob('*.tmp')), list(path.glob('*.tmp'))
                finally:
                    if takeover_proc.poll() is None:
                        takeover_proc.kill()
                        takeover_proc.wait(timeout=5)
                    if not takeover_released:
                        kernel32.ReleaseMutex(takeover_handle)
                        kernel32.CloseHandle(takeover_handle)

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

                # #49: Launching a second instance with a URL forwards it to the running window for that profile and exits.
                fwd_dir = path / 'forward'
                fwd_dir.mkdir()
                fwd_report_path = fwd_dir / 'forward.json'
                fwd_ready = fwd_dir / 'forward.ready'
                fwd_proc = subprocess.Popen([str(exe), '--smoke-test', str(fwd_report_path), base, 'forward'])
                try:
                    for _ in range(400):
                        if fwd_ready.is_file() or fwd_proc.poll() is not None:
                            break
                        sleep(0.05)
                    assert fwd_ready.is_file(), f'Forward host did not signal ready (exit {fwd_proc.poll()})'
                    sender = subprocess.run(
                        [str(exe), '--profile', str(fwd_dir), '--no-update-check', base + '/third'],
                        timeout=15,
                    )
                    assert sender.returncode == 0, f'Expected sender exit 0, got {sender.returncode}'
                    fwd_proc.wait(timeout=20)
                    assert fwd_report_path.is_file(), f'Missing forward report (exit {fwd_proc.returncode})'
                    fwd_report = json.loads(fwd_report_path.read_text(encoding='utf-8-sig'))
                    print(json.dumps(fwd_report, indent=2))
                    assert fwd_proc.returncode == 0, fwd_report
                    assert fwd_report == {
                        'tabCount': 2,
                        'openedUrl': base + '/third',
                        'openedTitle': 'Third page marker',
                        'selectedIsNewTab': True,
                        'pipeRejectsInvalid': True,
                    }, fwd_report
                finally:
                    if fwd_proc.poll() is None:
                        fwd_proc.kill()
                        fwd_proc.wait(timeout=5)
            finally:
                Fixture.stop_event.set()
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
