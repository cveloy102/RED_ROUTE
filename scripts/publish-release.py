"""Owner-only release publishing. Uses the configured Git credential helper in memory."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import urllib.error
import urllib.parse
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--make-public', action='store_true')
parser.add_argument('--version')
parser.add_argument('--commit', default='main')
parser.add_argument('--assets', nargs='*', default=[])
args = parser.parse_args()
env = dict(os.environ, GIT_TERMINAL_PROMPT='0')
credential = subprocess.check_output(['git', 'credential', 'fill'], input=b'protocol=https\nhost=github.com\n\n', env=env)
fields = dict(line.split('=', 1) for line in credential.decode().splitlines() if '=' in line)
token = fields.get('password')
if not token:
    raise SystemExit('GitHub credential helper did not provide credentials')
headers = {'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json', 'User-Agent': 'RED_ROUTE-release', 'X-GitHub-Api-Version': '2022-11-28'}
repo = 'https://api.github.com/repos/cveloy102/RED_ROUTE'

def request(method, url, body=None, binary=None):
    outgoing = dict(headers)
    data = binary if binary is not None else json.dumps(body).encode() if body is not None else None
    if data is not None:
        outgoing['Content-Type'] = 'application/octet-stream' if binary is not None else 'application/json'
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=data, headers=outgoing, method=method), timeout=300) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        raise SystemExit(f'GitHub API returned HTTP {error.code}') from None

if args.make_public:
    changed = request('PATCH', repo, {'private': False})
    print('Repository visibility:', changed.get('visibility'))
if args.version:
    assets = [Path(value).resolve(strict=True) for value in args.assets]
    if not assets:
        raise SystemExit('Release assets are required')
    existing = request('GET', repo + '/releases')
    release = next((item for item in existing if item['tag_name'] == 'v' + args.version and item['draft']), None)
    if release is None:
        release = request('POST', repo + '/releases', {'tag_name': 'v' + args.version, 'target_commitish': args.commit, 'name': 'RED_ROUTE ' + args.version, 'draft': True, 'prerelease': False, 'body': 'Установщик RED_ROUTE для Windows 10/11 x64. Профили и правила сохраняются при обновлении.\n\nИзменения: обновление из приложения через GitHub Releases, каталог сервисов и доменов с поиском.'})
    upload = release['upload_url'].split('{')[0]
    if not upload.startswith('https://uploads.github.com/repos/cveloy102/RED_ROUTE/releases/'):
        raise SystemExit('Unexpected release upload URL')
    for asset in assets:
        if any(item['name'] == asset.name and item['state'] == 'uploaded' and item['size'] == asset.stat().st_size for item in release['assets']):
            continue
        config = 'header = "Authorization: Bearer ' + token + '"\n'
        response = subprocess.run(['curl.exe', '--http1.1', '--silent', '--show-error', '--fail', '--connect-timeout', '20', '--max-time', '300', '--config', '-', '-H', 'Content-Type: application/octet-stream', '-H', 'User-Agent: RED_ROUTE-release', '--data-binary', '@' + str(asset), upload + '?name=' + urllib.parse.quote(asset.name)], input=config.encode(), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if response.returncode:
            raise SystemExit('Asset upload failed: ' + response.stderr.decode(errors='replace'))
        result = json.loads(response.stdout)
        print('Uploaded:', result['name'], result['size'], flush=True)
    published = request('PATCH', repo + '/releases/' + str(release['id']), {'draft': False, 'make_latest': 'true'})
    print('Published:', published['html_url'])
