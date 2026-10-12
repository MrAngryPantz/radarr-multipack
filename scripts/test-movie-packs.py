#!/usr/bin/env python3
"""Exercise a disposable Radarr instance using generated media and a fake client."""
import argparse
import hashlib
from email.utils import formatdate
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.request import Request, urlopen
from urllib.error import HTTPError, URLError
from urllib.parse import parse_qs, urlparse


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


class Audit:
    def __init__(self, args):
        self.args = args
        self.root = Path(tempfile.mkdtemp(prefix='radarr pack audit '))
        self.data = self.root / 'appdata'
        self.library = self.root / 'library'
        self.movie_names = ('Example Arrival', 'Example Journey', 'Example Return')
        self.downloads = self.root / 'downloads'
        self.title = 'Example.Collection.Trilogy.2000-2002.1080p.BluRay.x264-Mixed'
        self.pack = self.downloads / self.title
        self.hash = 'a' * 40
        self.key = secrets.token_hex(16)
        self.port = free_port()
        self.client_port = free_port()
        self.process = None
        self.grabs = []
        self.deletions = []
        self.rss_round = 0
        self.rss_sources = set()
        self.search_requests = []
        self.present = False
        self.state = 'downloading'
        self.log_handle = None
        for folder in (self.data, self.library, self.pack):
            folder.mkdir(parents=True)
        if args.short_paths:
            import ctypes
            for name in ('data', 'library', 'downloads', 'pack'):
                original = str(getattr(self, name))
                buffer = ctypes.create_unicode_buffer(32768)
                if not ctypes.windll.kernel32.GetShortPathNameW(original, buffer, len(buffer)):
                    raise ctypes.WinError()
                if buffer.value == original:
                    raise RuntimeError('This Windows volume does not provide 8.3 aliases for the trial')
                setattr(self, name, Path(buffer.value))
        (self.data / 'config.xml').write_text(
            '<Config><BindAddress>127.0.0.1</BindAddress>'
            f'<Port>{self.port}</Port><ApiKey>{self.key}</ApiKey>'
            '<AuthenticationMethod>Forms</AuthenticationMethod>'
            '<AuthenticationRequired>DisabledForLocalAddresses</AuthenticationRequired>'
            '<LaunchBrowser>False</LaunchBrowser><AnalyticsEnabled>False</AnalyticsEnabled>'
            '<UpdateAutomatically>False</UpdateAutomatically><LogLevel>Debug</LogLevel></Config>')

    def api(self, path, data=None, method=None, version=3):
        request = Request(f'http://127.0.0.1:{self.port}/api/v{version}/{path}',
                          data=None if data is None else json.dumps(data).encode(),
                          headers={'X-Api-Key': self.key, 'Content-Type': 'application/json'}, method=method)
        try:
            with urlopen(request, timeout=60) as response:
                body = response.read()
                return json.loads(body) if body else None
        except HTTPError as error:
            raise RuntimeError(f'{request.get_method()} {path}: {error.code} {error.read().decode()}') from None

    def start(self):
        self.log_handle = (self.root / 'host.log').open('a')
        executable = [str(self.args.app)] if self.args.app.suffix.lower() == '.exe' else [self.args.dotnet, str(self.args.app)]
        self.process = subprocess.Popen([*executable, '-nobrowser', f'-data={self.data}'],
                                        stdout=self.log_handle, stderr=subprocess.STDOUT)
        for _ in range(180):
            if self.process.poll() is not None:
                raise RuntimeError(f'Host exited: see {self.root / "host.log"}')
            try:
                self.api('system/status')
                return
            except (URLError, RuntimeError):
                time.sleep(0.5)
        raise TimeoutError('Host did not become ready')

    def stop(self):
        if self.process is not None and self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
        if self.log_handle:
            self.log_handle.close()

    def command(self, name, **fields):
        command = self.api('command', {'name': name, **fields})
        for _ in range(180):
            result = self.api(f'command/{command["id"]}')
            if result['status'] == 'completed':
                return result
            if result['status'] == 'failed':
                raise RuntimeError(f'{name} failed: {result}')
            time.sleep(0.25)
        raise TimeoutError(name)

    def server(self):
        audit = self
        class Handler(BaseHTTPRequestHandler):
            protocol_version = 'HTTP/1.1'

            def log_message(self, *_):
                pass

            def handle(self):
                try:
                    super().handle()
                except (ConnectionResetError, BrokenPipeError):
                    # The application closes persistent connections when the trial stops it.
                    pass

            def do_GET(self):
                self.handle_request()

            def do_POST(self):
                self.handle_request()

            def handle_request(self):
                path = urlparse(self.path).path
                body = self.rfile.read(int(self.headers.get('Content-Length', '0')))
                response = {}
                if path.endswith('/api'):
                    query = parse_qs(urlparse(self.path).query)
                    if query.get('t') == ['caps']:
                        response = (
                            '<caps>'
                            '<server version="1.0"/>'
                            '<limits max="100" default="100"/>'
                            '<searching>'
                            + ('<search available="yes" supportedParams="q"/>' if '/2/' not in path else '')
                            + ('<movie-search available="yes" supportedParams="q,imdbid,tmdbid"/>' if '/1/' in path
                               else '<movie-search available="yes" supportedParams="q"/>' if '/2/' in path else '')
                            + '</searching>'
                            '<categories>'
                            '<category id="2000" name="Movies"/>'
                            '</categories>'
                            '</caps>'
                        )
                    else:
                        is_search = any(k in query for k in ('q', 'imdbid', 'tmdbid'))
                        if is_search:
                            audit.search_requests.append((path, query))
                        else:
                            audit.rss_sources.add(path)
                        # ID searches return individual movies but omit collection packs.
                        restricted = any(k in query for k in ('imdbid', 'tmdbid'))
                        release_title = 'Example.Journey.2001.CAM-Test' if restricted else audit.title
                        magnet = f'magnet:?xt=urn:btih:{audit.hash}'
                        guid = f'http://127.0.0.1:{audit.client_port}{path}/release/{audit.rss_round}'
                        response = (
                            f'<rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">'
                            f'<channel>'
                            f'<title>Simulated indexer</title>'
                            f'<description>Test data</description>'
                            f'<link>http://127.0.0.1</link>'
                            f'<item>'
                            f'<title>{release_title}</title>'
                            f'<guid>{guid}</guid>'
                            f'<pubDate>{formatdate(usegmt=True)}</pubDate>'
                            f'<link>{magnet}</link>'
                            f'<size>1200000000</size>'
                            f'<enclosure url="{magnet}" length="1200000000" type="application/x-bittorrent"/>'
                            f'<torznab:attr name="category" value="2000"/>'
                            f'<torznab:attr name="seeders" value="3"/>'
                            f'<torznab:attr name="peers" value="5"/>'
                            f'<torznab:attr name="infohash" value="{audit.hash}"/>'
                            f'<torznab:attr name="tmdbid" value="900000002"/>'
                            f'</item>'
                            f'</channel>'
                            f'</rss>'
                        )
                        if restricted:
                            response = response.replace(f'<guid>{guid}</guid>', f'<guid>{guid}/movie</guid>')
                elif path.endswith('/app/webapiVersion'):
                    response = '2.11.2'
                elif path.endswith('/app/version'):
                    response = 'v5.0.4'
                elif path.endswith('/auth/login'):
                    response = 'Ok.'
                elif path.endswith('/app/preferences'):
                    response = {'save_path': str(audit.downloads), 'dht': True, 'max_ratio_enabled': True,
                                'max_ratio': 1, 'max_ratio_act': 0, 'queueing_enabled': False}
                elif path.endswith('/torrents/categories'):
                    response = {'test-radarr': {'name': 'test-radarr', 'savePath': str(audit.downloads)}}
                elif path.endswith('/torrents/info'):
                    response = [] if not audit.present else [{
                        'hash': audit.hash, 'name': audit.title, 'size': 1200000000,
                        'progress': 0.5 if audit.state == 'downloading' else 1,
                        'state': audit.state, 'category': 'test-radarr', 'save_path': str(audit.downloads),
                        'content_path': str(audit.pack), 'ratio': 2, 'ratio_limit': 1,
                        'seeding_time': 7200, 'eta': 0}]
                elif path.endswith('/torrents/add'):
                    audit.grabs.append(body.decode(errors='replace'))
                    audit.present = True
                    response = 'Ok.'
                elif path.endswith('/torrents/delete'):
                    audit.deletions.append(parse_qs(body.decode()))
                    audit.present = False
                encoded = response.encode() if isinstance(response, str) else json.dumps(response).encode()
                self.send_response(200)
                self.send_header('Content-Type', 'text/plain' if isinstance(response, str) else 'application/json')
                self.send_header('Content-Length', str(len(encoded)))
                self.end_headers()
                self.wfile.write(encoded)
        self.http = ThreadingHTTPServer(('127.0.0.1', self.client_port), Handler)
        threading.Thread(target=self.http.serve_forever, daemon=True).start()

    def insert(self, db, table, values):
        columns = ','.join('"' + key + '"' for key in values)
        placeholders = ','.join('?' for _ in values)
        db.execute(f'INSERT INTO "{table}" ({columns}) VALUES ({placeholders})', tuple(values.values()))

    def seed(self):
        with sqlite3.connect(self.data / 'radarr.db') as db:
            self.insert(db, 'RootFolders', {'Path': str(self.library)})
            metadata = []
            for number, title in enumerate(self.movie_names, 1):
                values = {
                    'Id': number, 'TmdbId': 900000000 + number, 'ImdbId': f'tt900000{number}',
                    'Title': title, 'OriginalTitle': title, 'SortTitle': title.lower(),
                    'CleanTitle': title.lower().replace(' ', ''),
                    'CleanOriginalTitle': title.lower().replace(' ', ''),
                    'OriginalLanguage': 1, 'Status': 4, 'Images': '[]', 'Genres': '[]',
                    'Ratings': '{}', 'Runtime': 10, 'Year': 1999 + number,
                    'InCinemas': '2000-01-01 00:00:00', 'PhysicalRelease': '2000-01-01 00:00:00',
                    'DigitalRelease': '2000-01-01 00:00:00', 'Recommendations': '[]',
                    'CollectionTmdbId': 900000100
                }
                self.insert(db, 'MovieMetadata', values)
                self.insert(db, 'Movies', {
                    'Id': number, 'MovieMetadataId': number, 'Path': str(self.library / title),
                    'Monitored': 1, 'QualityProfileId': 1, 'MovieFileId': 0,
                    'MinimumAvailability': 0, 'Added': '2000-01-01 00:00:00', 'Tags': '[]'})
                metadata.append({'tmdbId': 900000000 + number, 'title': title, 'year': 1999 + number})
            self.insert(db, 'Collections', {
                'Id': 1, 'TmdbId': 900000100, 'Title': 'Example Collection', 'CleanTitle': 'examplecollection',
                'SortTitle': 'example collection', 'Monitored': 0, 'QualityProfileId': 1,
                'RootFolderPath': str(self.library), 'SearchOnAdd': 0, 'MinimumAvailability': 0,
                'Images': '[]', 'Tags': '[]', 'Added': '2000-01-01 00:00:00', 'LastInfoSync': '2026-10-01 00:00:00'})
            self.insert(db, 'DownloadClients', {
                'Id': 1, 'Enable': 1, 'Name': 'Simulated client', 'Implementation': 'QBittorrent',
                'ConfigContract': 'QBittorrentSettings', 'Tags': '[]', 'RemoveCompletedDownloads': 1,
                'Settings': json.dumps({'host': '127.0.0.1', 'port': self.client_port,
                                        'movieCategory': 'test-radarr', 'recentMoviePriority': 0, 'olderMoviePriority': 0})})
            for indexer_id, name in enumerate(('First indexer', 'Second indexer', 'Third indexer'), 1):
                self.insert(db, 'Indexers', {
                    'Id': indexer_id, 'Name': name, 'Implementation': 'Torznab', 'ConfigContract': 'TorznabSettings',
                    'EnableRss': 1, 'EnableAutomaticSearch': 1, 'EnableInteractiveSearch': 1, 'Tags': '[]',
                    'Settings': json.dumps({'baseUrl': f'http://127.0.0.1:{self.client_port}',
                                            'apiPath': f'/indexer/{indexer_id}/api', 'categories': [2000], 'minimumSeeders': 3})})
            db.execute("UPDATE ScheduledTasks SET LastExecution = '2099-01-01 00:00:00'")
            # Generated clips are deliberately tiny; size decisions have separate unit tests.
            db.execute('UPDATE QualityDefinitions SET MinSize=0, MaxSize=0')

    def media(self):
        clip = self.root / 'clip.mkv'
        subprocess.run(['ffmpeg', '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i',
                        'color=c=black:s=1280x720:r=1', '-f', 'lavfi', '-i',
                        'anullsrc=channel_layout=stereo:sample_rate=44100', '-t', '600',
                        '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '35', '-c:a', 'aac',
                        '-b:a', '32k', str(clip)], check=True)
        self.original = self.library / self.movie_names[0] / 'Example.Arrival.2000.720p.HDTV.mkv'
        self.original.parent.mkdir()
        shutil.copyfile(clip, self.original)
        self.original_hash = hashlib.sha256(self.original.read_bytes()).hexdigest()
        for name in ('Example.Arrival.2000.1080p.BluRay.mkv', 'Example.Journey.2001.720p.WEB-DL.mkv', 'unidentified.mkv'):
            shutil.copyfile(clip, self.pack / name)

    def push(self, indexer, suffix='', seeders=10):
        return self.api('release/push', {
            'title': self.title, 'size': 1200000000, 'tmdbId': 900000002,
            'indexer': indexer, 'publishDate': '2026-01-01T00:00:00Z',
            'downloadUrl': f'http://127.0.0.1:{self.client_port}/release/{indexer.replace(" ", "-")}{suffix}',
            'magnetUrl': f'magnet:?xt=urn:btih:{self.hash}&dn=Example.Collection',
            'infoHash': self.hash, 'seeders': seeders, 'leechers': 0, 'protocol': 'torrent', 'downloadClientId': 1})

    def movies(self):
        return sorted(self.api('movie'), key=lambda m: m['id'])

    def process_downloads(self):
        self.command('RefreshMonitoredDownloads')
        self.command('ProcessMonitoredDownloads')

    def run(self):
        self.server()
        self.start()
        self.stop()
        self.seed()
        self.media()
        self.start()
        self.command('RescanMovie', movieId=1)
        assert sum(m['hasFile'] for m in self.movies()) == 1, 'Existing movie was not indexed'
        original_id = self.movies()[0]['movieFile']['id']
        profile = self.api('qualityprofile/1')
        assert not profile['allowMoviePacks'], 'Feature must start disabled'
        profile.update(allowMoviePacks=True, upgradeAllowed=True)
        self.api('qualityprofile/1', profile, method='PUT')
        profile = self.api('qualityprofile/1')
        assert profile['allowMoviePacks'] and not profile['upgradeAllowed']
        legacy_profile = dict(profile)
        legacy_profile.pop('allowMoviePacks')
        legacy_profile['upgradeAllowed'] = True
        self.api('qualityprofile/1', legacy_profile, method='PUT')
        profile = self.api('qualityprofile/1')
        assert profile['allowMoviePacks'] and not profile['upgradeAllowed'], 'Older client profile update disabled pack safeguards'
        print('PASS: profile API enforces upgrades off, including updates from older clients', flush=True)
        low_seeders = self.push('First indexer', seeders=2)[0]
        assert not low_seeders['approved'] and any('seeders' in r.lower() for r in low_seeders['rejections']), low_seeders['rejections']
        assert not self.grabs, 'Release below the minimum seeders was grabbed'
        for movie_id in (2, 3):
            results = self.api(f'release?movieId={movie_id}')
            packs = [r for r in results if r['title'] == self.title]
            assert {r['indexerId'] for r in packs} == {1, 2, 3}, 'A capability variant could not discover the pack'
            assert all(r['approved'] for r in packs), [r['rejections'] for r in packs]
            assert all(r['moviePackMovieIds'] == [1, 2, 3] for r in packs)
        assert any('imdbid' in q or 'tmdbid' in q for _, q in self.search_requests), 'ID search was not exercised'
        assert any(q.get('t') == ['movie'] and 'q' in q for _, q in self.search_requests), 'Movie title search was not exercised'
        assert any(q.get('t') == ['search'] and 'q' in q for _, q in self.search_requests), 'Generic search was not exercised'
        print('PASS: collection search works with ID, movie-title and generic capability variants', flush=True)
        self.command('RssSync' if self.args.grab_method == 'rss' else 'MoviesSearch',
                     **({} if self.args.grab_method == 'rss' else {'movieIds': [2]}))
        assert len(self.grabs) == 1, f'Expected one pack grab, got {len(self.grabs)}'
        self.command('RssSync')
        assert len(self.rss_sources) == 3, 'RSS did not query all simulated indexers'
        self.process_downloads()
        decision = self.push('Second indexer')[0]
        assert not decision['approved'] and len(self.grabs) == 1, 'Queued pack was grabbed twice'
        queue = self.api('queue')['records']
        assert {q['movieId'] for q in queue} == {1, 2, 3}, 'Queue must show each member'
        print(f'PASS: {self.args.grab_method} selects one pack; seeders and queue deduplication work', flush=True)
        self.state = 'stoppedUP'
        self.process_downloads()
        movies = self.movies()
        assert sum(m['hasFile'] for m in movies) == 2, [(m['title'], m['hasFile']) for m in movies]
        assert movies[1]['movieFile']['quality']['quality']['name'] == 'WEBDL-720p', 'Pack title overrode individual file quality'
        assert not self.deletions, 'Incomplete pack was removed'
        assert all(p.exists() for p in self.pack.glob('*.mkv'))
        assert self.original.exists() and hashlib.sha256(self.original.read_bytes()).hexdigest() == self.original_hash
        assert movies[0]['movieFile']['id'] == original_id
        print('PASS: individual file quality is used, existing movie is unchanged, unknown file retains pack', flush=True)
        self.stop()
        with sqlite3.connect(self.data / 'radarr.db') as db:
            self.insert(db, 'DownloadHistory', {
                'EventType': 2, 'MovieId': 1, 'DownloadId': self.hash.upper(), 'SourceTitle': self.title,
                'Date': '2090-01-01 00:00:00', 'Protocol': 2, 'IndexerId': 0, 'DownloadClientId': 1, 'Data': '{}'})
            db.execute("UPDATE MovieMetadata SET CollectionTmdbId=0")
        self.start()
        self.process_downloads()
        assert not self.deletions, 'Legacy completion history allowed removal after restart'
        assert len(self.api('queue')['records']) == 3, 'Incomplete restarted pack was hidden from queue'
        print('PASS: saved membership survives metadata changes and legacy completion history', flush=True)
        items = self.api(f'manualimport?downloadId={self.hash.upper()}&filterExistingFiles=false')
        unknown = next(item for item in items if Path(item['path']).name == 'unidentified.mkv')
        assert not unknown.get('movie'), 'Unknown pack file inherited another movie identity'
        self.command('ManualImport', files=[{
            'path': unknown['path'], 'movieId': 3, 'downloadId': self.hash.upper(),
            'quality': unknown['quality'], 'languages': unknown['languages'], 'indexerFlags': 0
        }], importMode='move')
        assert all(m['hasFile'] for m in self.movies()), 'Manually matched final movie did not import'
        self.process_downloads()
        assert len(self.deletions) == 1, 'Complete reviewed pack was not removed'
        assert self.original.exists() and hashlib.sha256(self.original.read_bytes()).hexdigest() == self.original_hash
        assert (self.pack / 'unidentified.mkv').exists(), 'Pack manual import moved a source file'
        self.stop()
        with sqlite3.connect(self.data / 'radarr.db') as db:
            db.execute('UPDATE MovieMetadata SET CollectionTmdbId=900000100')
        self.start()
        decision = self.push('Third indexer', '-another-guid')[0]
        assert not decision['approved'] and len(self.grabs) == 1, 'Completed pack was grabbed again'
        assert any('already have files' in r for r in decision['rejections']), decision['rejections']
        print('PASS: manual matching completes pack; source is copied; cleanup occurs only after all movies exist', flush=True)
        print('All isolated movie pack checks passed.', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--grab-method', choices=('search', 'rss'), default='search')
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--short-paths', action='store_true', help='Exercise Windows 8.3 aliases in library and download paths')
    args = parser.parse_args()
    if args.short_paths and os.name != 'nt':
        parser.error('--short-paths requires Windows')
    args.app = args.app.resolve()
    if not args.app.is_file():
        parser.error('Build the application first, then pass the path to Radarr.dll or Radarr.Console.exe')
    if shutil.which('ffmpeg') is None:
        parser.error('FFmpeg must be installed and on PATH')
    probe = args.app.parent / ('ffprobe.exe' if os.name == 'nt' else 'ffprobe')
    if not probe.is_file():
        parser.error('Place ffprobe beside the application before running the trial')
    audit = Audit(args)
    try:
        audit.run()
    finally:
        audit.stop()
        if hasattr(audit, 'http'):
            audit.http.shutdown()
            audit.http.server_close()
        print(f'Audit files: {audit.root}', flush=True)


if __name__ == '__main__':
    main()
