#!/usr/bin/env python3
"""Synthetic Ryujinx/Yuzu over real HTTP WebDAV; never opens user saves."""
import hashlib
import http.server
import shlex
import subprocess
import tempfile
import threading
import time
from pathlib import Path
from integration import Agent, Dav, synthetic

TITLE = '0100F2C0115B6000'
USER = '1234567890ABCDEF1234567890ABCDEF'


def yuzu(nand, text):
    root = nand / 'user/save/0000000000000000' / USER / TITLE
    root.mkdir(parents=True)
    (root / '.yuzu_save_size').write_bytes(bytes(range(16)))
    (root / 'save.dat').write_text(text)
    return root


def approve(agent, binding, state, outcome=None):
    outcome = outcome or agent.request('/save-sets/' + binding + '/compare', 'POST', {})
    return agent.request('/save-sets/' + binding + '/compatibility', 'POST', {
        'state': state, 'expectedLocalHash': outcome['localHash'], 'expectedHeads': outcome['heads']})


def wait_session(agent, session):
    for _ in range(250):
        current = agent.request('/launch-sessions/' + session['id'])
        if current['status'] in ('completed', 'failed', 'blocked', 'needs-attention'):
            return current
        time.sleep(.1)
    raise AssertionError('launch timeout')


def main():
    with tempfile.TemporaryDirectory(prefix='ryusync-yuzu-integration-') as folder:
        tmp = Path(folder)
        Dav.root = tmp / 'remote'
        Dav.root.mkdir()
        server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Dav)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        agents = []
        try:
            def agent(name, workspace='cross', protocol=2):
                current = Agent(tmp / (name + '-state'))
                agents.append(current)
                settings = current.request('/settings')
                settings.update(deviceName=name, protocolVersion=protocol, webDavUrl=f'http://127.0.0.1:{server.server_port}', webDavUser='test', workspace=workspace, allowInsecureHttp=True)
                current.request('/settings', 'PUT', settings)
                current.request('/storage/unlock', 'POST', {'password': 'password'})
                return current

            rfile = synthetic(tmp / 'ryujinx', 'server Ryujinx progress')
            r = agent('pc')
            settings = r.request('/settings')
            settings['dataRoot'] = str(tmp / 'ryujinx')
            r.request('/settings', 'PUT', settings)
            save = r.request('/ryujinx/scan', 'POST', {})['saves'][0]
            rb = r.request('/save-sets', 'POST', {'discoveryId': save['id'], 'slot': 'main'})
            assert r.sync()['status'] == 'completed'

            nand = tmp / 'custom-nand'
            nand.mkdir()
            y = agent('deck')
            marker = tmp / 'yuzu-launched'
            yfile = nand / 'user/save/0000000000000000' / USER / TITLE / 'save.dat'
            stub = tmp / 'fake-yuzu'
            stub.write_text('#!/bin/sh\nsleep 1\nprintf yuzu-played > ' + shlex.quote(str(yfile)) + '\ntouch ' + shlex.quote(str(marker)) + '\n')
            stub.chmod(0o700)
            installation = dict(id='deck-yuzu', name='Deck Yuzu', kind='yuzu', build='synthetic-1', dataRoot='', nandRoot=str(nand), executable=str(stub), workingDirectory='', arguments=[])
            settings = y.request('/settings')
            settings['installations'] = [installation]
            y.request('/settings', 'PUT', settings)
            head = y.request('/catalog')[0]
            reservation = y.request('/restore-reservations', 'POST', {'installationId': installation['id'], 'revisionId': head['id']})
            assert not y.request('/installations/deck-yuzu/scan', 'POST', {})['saves']
            root = yuzu(nand, 'fresh initialization save')
            name_cache = tmp / 'game-list-cache'
            name_cache.mkdir()
            (name_cache / (TITLE + '.appname.txt')).write_text('한글 게임 이름', encoding='utf-8')
            settings = y.request('/settings')
            settings['installations'][0]['gameListCacheRoot'] = str(name_cache)
            y.request('/settings', 'PUT', settings)
            save = y.request('/installations/deck-yuzu/scan', 'POST', {})['saves'][0]
            assert save['title'] == '한글 게임 이름' and save['titleSource'] == 'yuzu-cache'
            yb = y.request('/save-sets', 'POST', {'discoveryId': save['id'], 'slot': 'main', 'installationId': installation['id'], 'restoreReservationId': reservation['id']})
            y.request('/game-titles/' + TITLE, 'PUT', {'title': '내 게임 이름'})
            renamed = y.request('/save-sets')[0]
            assert renamed['title'] == '내 게임 이름' and renamed['id'] == yb['id'] and renamed['remoteKey'] == yb['remoteKey']
            assert yfile.read_text() == 'fresh initialization save'
            y.request('/game-titles/' + TITLE, 'DELETE')
            assert y.request('/save-sets')[0]['title'] == '한글 게임 이름'
            assert not y.request('/restore-reservations')
            result = y.sync()
            assert result['status'] == 'blocked', result
            assert result['results'][0]['status'] == 'compatibility-required'
            assert yfile.read_text() == 'fresh initialization save'
            approve(y, yb['id'], 'trial', result['results'][0])
            assert y.sync()['status'] == 'completed'
            assert yfile.read_text() == 'server Ryujinx progress'
            assert (root / '.yuzu_save_size').read_bytes() == bytes(range(16))
            backup = y.request('/backups')[0]
            assert (Path(backup['path']) / 'save.dat').read_text() == 'fresh initialization save'

            # A queued request outside the launch profile must keep its own scope.
            other_root = nand / 'user/save/0000000000000000' / USER / '0100F2C0115B6001'
            other_root.mkdir()
            (other_root / '.yuzu_save_size').write_bytes(bytes(16))
            (other_root / 'save.dat').write_text('outside-profile')
            other_save = next(s for s in y.request('/installations/deck-yuzu/scan', 'POST', {})['saves'] if s['titleId'] == '0100F2C0115B6001')
            other_binding = y.request('/save-sets', 'POST', {'discoveryId': other_save['id'], 'slot': 'main', 'installationId': installation['id']})
            assert y.sync(other_binding['id'])['status'] == 'completed'
            other_hash = next(h['contentHash'] for h in y.request('/catalog') if h['titleId'] == '0100F2C0115B6001')
            (other_root / 'save.dat').write_text('outside-profile web request')

            profile = dict(id='deck-launch', name='Deck Yuzu', installationId=installation['id'], groupKeys=[yb['remoteKey']], arguments=[])
            y.request('/launch-profiles/deck-launch', 'PUT', profile)
            blocked = wait_session(y, y.request('/launch-sessions', 'POST', {'launchProfileId': profile['id']}))
            assert blocked['status'] == 'blocked', blocked
            assert not marker.exists()
            approve(y, yb['id'], 'confirmed')
            session = y.request('/launch-sessions', 'POST', {'launchProfileId': profile['id']})
            queued = None
            for _ in range(250):
                session = y.request('/launch-sessions/' + session['id'])
                if session['status'] == 'playing' and queued is None:
                    queued = y.request('/sync/jobs', 'POST', {'bindingId': other_binding['id']})
                    assert queued['status'] == 'waiting'
                if session['status'] in ('completed', 'failed', 'blocked', 'needs-attention'):
                    break
                time.sleep(.1)
            assert session['status'] == 'completed', session
            assert marker.exists() and queued
            for _ in range(100):
                pending = y.request('/jobs/' + queued['id'])
                if pending['status'] not in ('waiting', 'running', 'queued'):
                    break
                time.sleep(.1)
            assert pending['status'] == 'completed', pending
            assert [v['bindingId'] for v in pending['results']] == [other_binding['id']]
            assert other_hash != next(h['contentHash'] for h in y.request('/catalog') if h['titleId'] == '0100F2C0115B6001')

            manual = tmp / 'manual-yuzu'
            manual.write_text('#!/bin/sh\nsleep 2\n')
            manual.chmod(0o700)
            running = subprocess.Popen([str(manual)])
            try:
                failed = y.sync(yb['id'])
                assert failed['status'] == 'failed' and '실행 중' in failed['message'], failed
                assert yfile.read_text() == 'yuzu-played'
            finally:
                running.wait(timeout=5)
            result = r.sync()
            assert result['status'] == 'blocked', result
            approve(r, rb['id'], 'trial', result['results'][0])
            assert r.sync()['status'] == 'completed'
            assert rfile.read_text() == 'yuzu-played'
            assert not (rfile.parent / '.yuzu_save_size').exists()
            approve(r, rb['id'], 'confirmed')

            # A second local installation joins the same remote group.
            local_nand = tmp / 'pc-yuzu-nand'
            local_save = yuzu(local_nand, 'yuzu-played')
            settings = r.request('/settings')
            settings['installations'] = [{**installation, 'id': 'pc-yuzu', 'nandRoot': str(local_nand)}]
            r.request('/settings', 'PUT', settings)
            found = r.request('/installations/pc-yuzu/scan', 'POST', {})['saves'][0]
            local = r.request('/save-sets', 'POST', {'discoveryId': found['id'], 'slot': 'main', 'installationId': 'pc-yuzu'})
            assert local['remoteKey'] == rb['remoteKey'] and local['id'] != rb['id']
            approve(r, local['id'], 'trial')
            assert r.sync()['status'] == 'completed'
            (local_save / 'save.dat').write_text('independent yuzu progress')
            rfile.write_text('independent ryujinx progress')
            conflict = r.sync()
            assert conflict['status'] == 'conflict', conflict
            assert (local_save / 'save.dat').read_text() == 'independent yuzu progress'
            assert rfile.read_text() == 'independent ryujinx progress'

            # v1 configuration + remote revision import leaves the source intact.
            old_file = synthetic(tmp / 'old-ryujinx', 'legacy progress')
            old = agent('old', 'legacy', 1)
            settings = old.request('/settings')
            settings['dataRoot'] = str(tmp / 'old-ryujinx')
            old.request('/settings', 'PUT', settings)
            found = old.request('/ryujinx/scan', 'POST', {})['saves'][0]
            old.request('/save-sets', 'POST', {'discoveryId': found['id'], 'slot': 'main'})
            assert old.sync()['status'] == 'completed'
            legacy_head = old.request('/catalog')[0]
            old.close()
            agents.remove(old)
            old_db = tmp / 'old-state/state.db'
            digest = hashlib.sha256(old_db.read_bytes()).hexdigest()
            old_remote = {str(p): p.read_bytes() for p in (Dav.root / 'ryusync/legacy').rglob('*') if p.is_file()}
            new = agent('imported', 'unused')
            imported = new.request('/import/legacy', 'POST', {'sourceRoot': str(old_db.parent)})
            assert Path(imported['backupPath']).is_file()
            assert hashlib.sha256(old_db.read_bytes()).hexdigest() == digest
            assert new.request('/settings')['protocolVersion'] == 2
            new.request('/storage/unlock', 'POST', {'password': 'password'})
            copied_binding = new.request('/save-sets')[0]
            imported_revision = new.request('/import/legacy/revision', 'POST', {'bindingId': copied_binding['id'], 'revisionId': legacy_head['id']})
            assert imported_revision['parents'] == []
            assert imported_revision['importedFromRevisionId'] == legacy_head['id']
            assert new.sync()['status'] == 'completed'
            assert old_file.read_text() == 'legacy progress'
            assert old_remote == {str(p): p.read_bytes() for p in (Dav.root / 'ryusync/legacy').rglob('*') if p.is_file()}
            print('PASS: custom NAND discovery, reserved initial restore, cross-emulator trial/confirmation, profile launch + queued sync, Ryujinx/Yuzu round-trip, same-PC conflict protection, v1 import without changing source data.')
            print('Synthetic fixtures only; actual game/emulator compatibility remains unverified.')
        finally:
            for current in agents:
                current.close()
            server.shutdown()
            server.server_close()


if __name__ == '__main__':
    main()
