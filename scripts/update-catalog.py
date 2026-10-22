"""Bundle the complete pinned Geo-Aggregator catalogue for offline search."""
from concurrent.futures import ThreadPoolExecutor
import ipaddress
import json
from pathlib import Path
import re
import urllib.request

def download(url):
    request = urllib.request.Request(url, headers={'User-Agent': 'RED_ROUTE-catalog'})
    with urllib.request.urlopen(request, timeout=40) as response:
        return response.read()

sha = json.loads(download('https://api.github.com/repos/Ground-Zerro/Geo-Aggregator/git/ref/heads/main'))['object']['sha']
if not re.fullmatch('[0-9a-f]{40}', sha):
    raise SystemExit('Invalid catalogue commit')
prefix = 'https://raw.githubusercontent.com/Ground-Zerro/Geo-Aggregator/' + sha + '/'
catalog = json.loads(download(prefix + 'db/catalog.json'))

def service(item):
    source = item['src']
    if '..' in source or source.startswith('/') or ':' in source or '\\' in source:
        raise ValueError('Unsafe catalogue source path')
    values = set()
    for raw in download(prefix + source).decode('utf-8-sig').splitlines():
        line = raw.split('#')[0].strip()
        if not line or line.startswith('regexp:') or '@' in line:
            continue
        for marker in ('domain:', 'full:'):
            if line.startswith(marker):
                line = line[len(marker):]
        try:
            values.add(str(ipaddress.ip_network(line, strict=False)))
            continue
        except ValueError:
            pass
        try:
            domain = line.rstrip('.').encode('idna').decode().lower()
        except UnicodeError:
            continue
        if '.' in domain and len(domain) <= 253 and all(re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', label) for label in domain.split('.')):
            values.add(domain)
    return {'id': item['id'], 'name': item['name'], 'sourcePath': source, 'domains': sorted(values)}

with ThreadPoolExecutor(max_workers=12) as workers:
    services = list(workers.map(service, catalog['services']))
destination = Path(__file__).resolve().parent.parent / 'src/RedRoute.Platform/Resources/seed.json'
destination.write_text(json.dumps({'commit': sha, 'services': services}, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
print('Bundled services:', len(services), 'unique entries:', len({d for s in services for d in s['domains']}), 'largest service:', max(len(s['domains']) for s in services), 'commit:', sha)
