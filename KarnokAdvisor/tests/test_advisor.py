import copy
from contextlib import contextmanager
import ctypes
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import sys
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
from http.server import BaseHTTPRequestHandler, HTTPServer

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from catalog import Catalog, atomic_json, read_json
from engine import Engine, UnknownRule
from advisor import serve, snapshot_age
from model_api import decode_chat_stream, decode_gemini_stream, parse_ranking_json, request_ranking, validate_ranking


@contextmanager
def windows_reader_without_delete_share(path):
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_ulong, ctypes.c_ulong,
                                  ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    kernel.CloseHandle.restype = ctypes.c_int
    handle = kernel.CreateFileW(str(path), 0x80000000, 3, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        yield
    finally:
        kernel.CloseHandle(handle)


def template(id_, type_='Item', size='Small', attributes=None, action=None):
    d = {'Id': id_, 'Type': type_, 'Size': size, 'StartingTier': 'Bronze', 'Heroes': ['Karnok'],
         'Tags': [], 'HiddenTags': [], 'SpawningEligibility': 'Always', 'InternalName': id_,
         'Localization': {'Title': {'Text': id_}}, 'Tiers': {'Bronze': {'Attributes': attributes or {}}}, 'Abilities': {}}
    if action:
        d['Abilities'] = {'0': {'Trigger': {'$type': 'TTriggerOnCardSelected'}, 'Action': action}}
    return d


def gain(attribute, value, duration=None):
    return {'$type': 'TActionPlayerModifyAttribute', 'AttributeType': attribute, 'Operation': 'Add',
            'Value': {'$type': 'TFixedValue', 'Value': value}, 'Duration': duration,
            'Target': {'$type': 'TTargetPlayerAbsolute', 'TargetMode': 'Player'}}


def card(id_, socket=None, attrs=None):
    return {'template_id': id_, 'instance_id': 'inst_' + id_, 'tier': 'Bronze', 'socket': socket, 'attributes': attrs or {}}


def state(options=None):
    return {'snapshot_id': 'current', 'captured_at_utc': datetime.now(timezone.utc).isoformat(), 'status': 'live',
            'hero': 'Karnok', 'day': 3, 'hour': 2, 'player_attributes': {'Gold': 10, 'Prestige': 10, 'HealthMax': 600},
            'board': [], 'stash': [], 'skills': [], 'unlocked_slots': list(range(6)),
            'options': options or [], 'selection_rules': {'is_free': True, 'can_exit': False}}


class EngineTests(unittest.TestCase):
    def setUp(self):
        self.catalog = Catalog(cards=[
            template('weak', attributes={'DamageAmount': 1, 'CooldownMax': 6000, 'SellPrice': 1}),
            template('strong', size='Medium', attributes={'DamageAmount': 120, 'CooldownMax': 3000, 'BuyPrice': 4}),
            template('unknown_price', attributes={'DamageAmount': 500}),
            template('gold5', 'EncounterStep', action=gain('Gold', 5)),
            template('xp1', 'EncounterStep', action=gain('Experience', 1)),
            template('health50', 'EncounterStep', action=gain('HealthMax', 50)),
            template('temporary', 'EncounterStep', action=gain('HealthMax', 1000, {'DurationType': 'UntilEndOfDay'})),
            template('danger', 'EncounterStep', action={'$type': 'TActionAnd', 'Actions': [gain('Gold', 30), {'$type': 'TActionCardDestroy'}]}),
        ])
        self.engine = Engine(self.catalog)

    def shop(self, s):
        s['selection_rules'] = {'is_free': False, 'can_exit': True}
        prepared = self.engine.prepare(s)
        return self.engine.shop_plan(prepared['options'][0], prepared)

    def test_event_gold_and_experience_have_observed_values(self):
        result = self.engine.recommend(state([card('gold5'), card('xp1'), card('health50')]))
        self.assertEqual(result['recommendations'][0]['action_id'], 'inst_gold5')
        self.assertIn('金币变化 +5', result['recommendations'][0]['reasons'][0])
        self.assertEqual(result['snapshot_id'], 'current')

    def test_level_up_options_are_ranked(self):
        s = state([card('xp1'), card('health50')]); s['state_type'] = 'LevelUpState'
        self.assertEqual(len(self.engine.recommend(s)['recommendations']), 2)

    def test_native_price_overrides_template_price(self):
        s = state([card('strong', attrs={'BuyPrice': 9})])
        result = self.shop(s)
        self.assertTrue(result['eligible'])
        self.assertIn('实际购买价格 9', result['reasons'][0])

    def test_unknown_price_does_not_become_free(self):
        self.assertFalse(self.shop(state([card('unknown_price')]))['eligible'])

    def test_locked_slots_cannot_be_used(self):
        s = state([card('strong')]); s['unlocked_slots'] = [0, 2, 4]
        self.assertFalse(self.shop(s)['eligible'])

    def test_replacement_frees_contiguous_slots(self):
        s = state([card('strong')]); s['unlocked_slots'] = [0, 1]
        s['board'] = [card('weak', 0), {**card('weak', 1), 'instance_id': 'second'}]
        result = self.shop(s)
        self.assertTrue(result['eligible'])
        self.assertEqual(len([x for x in result['steps'] if '先卖出' in x]), 2)
        self.assertIn('预计剩余 8', result['reasons'][0])

    def test_sell_steps_are_required_to_fund_purchase(self):
        s = state([card('strong')]); s['unlocked_slots'] = [0, 1]
        s['player_attributes']['Gold'] = 3; s['board'] = [card('weak', 0)]
        result = self.shop(s)
        self.assertTrue(result['eligible'])
        self.assertTrue(result['steps'][0].startswith('先卖出'))
        self.assertIn('预计剩余 0', result['reasons'][0])

    def test_insufficient_gold_is_ineligible(self):
        s = state([card('strong')]); s['player_attributes']['Gold'] = 0
        self.assertFalse(self.shop(s)['eligible'])

    def test_incomplete_placement_is_ineligible(self):
        s = state([card('strong')]); s['board'] = [card('weak')]
        self.assertFalse(self.shop(s)['eligible'])

    def test_duplicate_item_is_not_treated_as_two_independent_items(self):
        s = state([card('strong')]); s['stash'] = [card('strong')]
        self.assertFalse(self.shop(s)['eligible'])

    def test_unknown_destruction_cannot_hide_behind_gold_reward(self):
        result = self.engine.recommend(state([card('danger'), card('xp1')]))
        self.assertEqual(result['recommendations'][0]['action_id'], 'inst_xp1')
        self.assertFalse(next(c for c in result['candidates'] if c['action_id'] == 'inst_danger')['eligible'])

    def test_temporary_health_is_not_reported_as_permanent(self):
        result = self.engine.recommend(state([card('temporary')]))
        self.assertFalse(result['candidates'][0]['eligible'])
        self.assertTrue(result['candidates'][0]['uncertainties'])

    def test_menu_combat_replay_and_other_hero_stop_recommendations(self):
        for changes in ({'status': 'menu'}, {'in_combat': True}, {'is_replay': True}, {'hero': 'Vanessa'}):
            with self.subTest(changes=changes):
                s = state([card('gold5')]); s.update(changes)
                self.assertEqual(self.engine.recommend(s)['recommendations'], [])

    def test_live_attribute_used_for_reward_value(self):
        source = self.catalog.enrich(card('xp1', attrs={'Custom_0': 7}))
        v = {'$type': 'TReferenceValueCardAttribute', 'AttributeType': 'Custom_0', 'Target': {'$type': 'TTargetCardSelf'}}
        self.assertEqual(self.engine.value(v, state(), source), 7)

    def test_unknown_value_is_explicit(self):
        with self.assertRaises(UnknownRule): self.engine.value({'$type': 'TFutureValue'}, state(), {})

    def test_conditions_do_not_invent_event_appearance_days(self):
        source = self.catalog.cards['gold5']
        source['Abilities']['0']['Prerequisites'] = [{'$type': 'TPrerequisiteRun', 'Conditions':
            {'$type': 'TRunConditionalCurrentDay', 'CurrentDay': 5, 'ComparisonOperator': 'GreaterThanOrEqual'}}]
        early = self.engine.recommend(state([card('gold5')]))
        late_state = state([card('gold5')]); late_state['day'] = 6
        late = self.engine.recommend(late_state)
        self.assertFalse(early['candidates'][0]['eligible'])
        self.assertTrue(late['candidates'][0]['eligible'])

    def test_input_state_is_not_modified(self):
        s = state([card('gold5')]); original = copy.deepcopy(s)
        self.engine.recommend(s)
        self.assertEqual(s, original)

    def test_identical_input_is_deterministic(self):
        s = state([card('xp1'), card('gold5')])
        self.assertEqual(self.engine.recommend(s), self.engine.recommend(s))

    def test_ordered_gold_cost_cannot_be_paid_with_later_reward(self):
        self.catalog.cards['gold5']['Abilities']['0']['Action'] = {'$type':'TActionAnd','Actions':[gain('Gold',-15),gain('Gold',20)]}
        r = self.engine.recommend(state([card('gold5')]))
        self.assertFalse(r['candidates'][0]['eligible'])

    def test_sequence_uses_modified_player_attribute(self):
        modified = gain('Gold',0)
        modified['Value'] = {'$type':'TReferenceValuePlayerAttribute','AttributeType':'Gold',
                             'Target':{'$type':'TTargetPlayerAbsolute','TargetMode':'Player'}}
        self.catalog.cards['gold5']['Abilities']['0']['Action'] = {'$type':'TActionAnd','Actions':[gain('Gold',5),modified]}
        benefit = self.engine.template_benefit(self.catalog.enrich(card('gold5')),state())
        self.assertEqual(benefit.gold_delta,20)


class ModelTests(unittest.TestCase):
    def setUp(self):
        self.local = {'snapshot_id': 'now', 'candidates': [
            {'action_id': 'a', 'eligible': True, 'title': 'A', 'reasons': ['实际价格2金币']},
            {'action_id': 'b', 'eligible': False, 'title': 'B', 'reasons': []}]}

    def test_model_cannot_invent_actions_or_override_legality(self):
        for id_ in ('invented', 'b'):
            with self.assertRaises(ValueError):
                validate_ranking({'snapshot_id': 'now', 'ranking': [{'action_id': id_, 'reason': 'r'}]}, self.local)

    def test_stale_model_result_rejected(self):
        with self.assertRaises(ValueError): validate_ranking({'snapshot_id': 'old', 'ranking': []}, self.local)

    def test_duplicate_ranking_rejected(self):
        row = {'action_id': 'a', 'reason': 'r'}
        with self.assertRaises(ValueError): validate_ranking({'snapshot_id': 'now', 'ranking': [row, row]}, self.local)

    def test_model_keeps_locally_verified_facts(self):
        result = validate_ranking({'snapshot_id': 'now', 'ranking': [{'action_id': 'a', 'reason': '保留经济空间'}]}, self.local)
        self.assertIn('实际价格2金币', result[0]['reasons'])
        self.assertEqual(len(self.local['candidates'][0]['reasons']), 1)

    def test_http_contract_with_mock_server(self):
        outer = self
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                outer.assertEqual(self.path, '/v1/chat/completions')
                body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                outer.assertEqual(body['model'], 'test-model')
                outer.assertEqual(body['response_format']['type'], 'json_object')
                content = json.dumps({'snapshot_id': 'now', 'ranking': [{'action_id': 'a', 'reason': '测试策略'}]})
                raw = json.dumps({'choices': [{'message': {'content': content}}]}).encode()
                self.send_response(200); self.send_header('Content-Length', str(len(raw))); self.end_headers(); self.wfile.write(raw)
            def log_message(self, *_): pass
        server = HTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            result = request_ranking({'base_url': f'http://127.0.0.1:{server.server_port}/v1', 'model': 'test-model'}, {}, self.local, {})
            self.assertEqual(result[0]['action_id'], 'a')
        finally: server.shutdown(); server.server_close(); thread.join()

    def test_gemini_stream_contract_and_verified_facts(self):
        outer = self
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                outer.assertEqual(self.path, '/v1beta/models/gemini-3.8-flash:streamGenerateContent?alt=sse')
                outer.assertEqual(self.headers['Authorization'], 'Bearer test-only-token')
                outer.assertEqual(self.headers['Accept'], 'text/event-stream')
                body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                outer.assertIn('systemInstruction', body)
                outer.assertEqual(body['generationConfig']['responseMimeType'], 'application/json')
                outer.assertEqual(body['generationConfig']['maxOutputTokens'], 4096)
                data = json.loads(body['contents'][0]['parts'][0]['text'])
                outer.assertEqual([c['action_id'] for c in data['candidates']], ['a'])
                answer = '```json\n'+json.dumps({'snapshot_id': 'now', 'ranking': [{'action_id': 'a', 'reason': '保留经济空间'}]}, ensure_ascii=False)+'\n```'
                chunks = [
                    {'candidates': [{'content': {'parts': [{'thought': True, 'text': 'test-only non-answer'}]}, 'index': 0}]},
                    {'candidates': [{'content': {'parts': [{'text': answer[:30]}]}, 'index': 0}]},
                    {'candidates': [{'content': {'parts': [{'text': answer[30:]}]}, 'index': 0, 'finishReason': 'STOP'}]},
                    {'usageMetadata': {'totalTokenCount': 100}}]
                raw = (': keepalive\r\n\r\n' + ''.join('data: '+json.dumps(c, ensure_ascii=False)+'\r\n\r\n' for c in chunks)).encode('utf-8')
                self.send_response(200); self.send_header('Content-Type', 'text/event-stream')
                self.send_header('Content-Length', str(len(raw))); self.end_headers(); self.wfile.write(raw)
            def log_message(self, *_): pass
        server = HTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            with patch.dict(os.environ, {'KARNOK_TEST_API_KEY': 'test-only-token'}):
                result = request_ranking({'protocol': 'gemini', 'base_url': f'http://127.0.0.1:{server.server_port}/v1beta',
                                          'model': 'gemini-3.8-flash', 'key_environment': 'KARNOK_TEST_API_KEY'}, {}, self.local, {})
            self.assertEqual(result[0]['action_id'], 'a')
            self.assertIn('实际价格2金币', result[0]['reasons'])
            self.assertIn('保留经济空间', result[0]['reasons'][0])
        finally: server.shutdown(); server.server_close(); thread.join()

    def test_chat_stream_contract_and_verified_facts(self):
        outer = self
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                outer.assertEqual(self.path, '/v1/chat/completions')
                outer.assertEqual(self.headers['Accept'], 'text/event-stream')
                body = json.loads(self.rfile.read(int(self.headers['Content-Length'])))
                outer.assertEqual(body['model'], 'gemini-3.8-flash:stable')
                outer.assertIs(body['stream'], True)
                data = json.loads(body['messages'][1]['content'])
                outer.assertEqual([c['action_id'] for c in data['candidates']], ['a'])
                answer = '```json\n'+json.dumps({'snapshot_id': 'now', 'ranking': [{'action_id': 'a', 'reason': '保留经济空间'}]}, ensure_ascii=False)+'\n```'
                chunks = [
                    {'choices': [{'index': 0, 'delta': {'role': 'assistant'}}]},
                    {'choices': [{'index': 0, 'delta': {'reasoning_content': 'test-only non-answer'}}]},
                    {'choices': [{'index': 0, 'delta': {'content': answer[:30]}}]},
                    {'choices': [{'index': 0, 'delta': {'content': answer[30:]}, 'finish_reason': 'stop'}]},
                    {'choices': [], 'usage': {'total_tokens': 100}}]
                frames = [': keepalive\n\n']
                for chunk in chunks:
                    frames.append(''.join('data: '+line+'\n' for line in json.dumps(chunk, ensure_ascii=False, indent=2).splitlines())+'\n')
                raw = (''.join(frames)+'data: [DONE]\n\n').encode('utf-8')
                self.send_response(200); self.send_header('Content-Type', 'text/event-stream')
                self.send_header('Content-Length', str(len(raw))); self.end_headers(); self.wfile.write(raw)
            def log_message(self, *_): pass
        server = HTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            result = request_ranking({'protocol': 'openai', 'stream': True, 'base_url': f'http://127.0.0.1:{server.server_port}/v1',
                                      'model': 'gemini-3.8-flash:stable'}, {}, self.local, {})
            self.assertEqual(result[0]['action_id'], 'a')
            self.assertIn('实际价格2金币', result[0]['reasons'])
            self.assertIn('保留经济空间', result[0]['reasons'][0])
        finally: server.shutdown(); server.server_close(); thread.join()

    def test_chat_stream_rejects_incomplete_or_refused_answers(self):
        answer = json.dumps({'snapshot_id': 'now', 'ranking': [{'action_id': 'a', 'reason': 'r'}]})
        for finish, done, refusal in ((None, True, False), ('length', True, False), ('content_filter', True, False),
                                      ('stop', False, False), ('stop', True, True)):
            choice = {'index': 0, 'delta': {'content': answer}}
            if finish: choice['finish_reason'] = finish
            if refusal: choice['delta']['refusal'] = 'test-only refusal'
            raw = ('data: '+json.dumps({'choices': [choice]})+'\n\n'+('data: [DONE]\n\n' if done else '')).encode()
            with self.subTest(finish=finish, done=done, refusal=refusal), self.assertRaises(ValueError):
                decode_chat_stream(raw)

    def test_gemini_stream_rejects_partial_or_blocked_answers(self):
        answer = json.dumps({'snapshot_id': 'now', 'ranking': [{'action_id': 'a', 'reason': 'r'}]})
        for finish in (None, 'MAX_TOKENS', 'SAFETY'):
            candidate = {'content': {'parts': [{'text': answer}]}}
            if finish: candidate['finishReason'] = finish
            raw = ('data: '+json.dumps({'candidates': [candidate]})+'\n\n').encode()
            with self.subTest(finish=finish), self.assertRaises(ValueError):
                decode_gemini_stream(raw)

    def test_ranking_json_accepts_one_complete_fence_and_rejects_extra_text(self):
        payload = {'snapshot_id': 'now', 'ranking': [{'action_id': 'a', 'reason': 'r'}]}
        content = json.dumps(payload)
        fenced = '```json\n'+content+'\n```'
        for text in (content, fenced):
            self.assertEqual(parse_ranking_json(text), payload)
        for text in ('explanation\n'+fenced, fenced+'\nextra text', fenced+'\n'+fenced):
            with self.subTest(text=text), self.assertRaises(ValueError):
                parse_ranking_json(text)

    def test_streams_do_not_expose_provider_error_body(self):
        raw = b'data: {"error":{"message":"test-only-sensitive-provider-text"}}\n\n'
        for decoder in (decode_chat_stream, decode_gemini_stream):
            with self.subTest(decoder=decoder.__name__), self.assertRaises(RuntimeError) as raised:
                decoder(raw)
            self.assertNotIn('test-only-sensitive-provider-text', str(raised.exception))

    def test_unknown_model_protocol_is_rejected(self):
        with self.assertRaises(ValueError):
            request_ranking({'protocol': 'unknown', 'base_url': 'http://127.0.0.1/v1', 'model': 'test'}, {}, self.local, {})

    def test_model_credentials_are_not_forwarded_on_redirect(self):
        followed = []
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                self.rfile.read(int(self.headers['Content-Length']))
                self.send_response(302); self.send_header('Location', '/another-target'); self.end_headers()
            def do_GET(self):
                followed.append(True)
                self.send_response(200); self.end_headers()
            def log_message(self, *_): pass
        server = HTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            with self.assertRaisesRegex(RuntimeError, 'HTTP 302'):
                request_ranking({'base_url': f'http://127.0.0.1:{server.server_port}/v1', 'model': 'test'}, {}, self.local, {})
            self.assertEqual(followed, [])
        finally: server.shutdown(); server.server_close(); thread.join()


class WorkerTests(unittest.TestCase):
    def test_snapshot_reader_handles_brief_publication_gap(self):
        root = Path(__file__).resolve().parent
        with tempfile.TemporaryDirectory(dir=root) as directory:
            path = Path(directory)/'snapshot.json'
            publisher = threading.Timer(.06, atomic_json, args=(path, {'snapshot_id': 'new'}))
            publisher.start()
            try:
                self.assertEqual(read_json(path)['snapshot_id'], 'new')
            finally:
                publisher.join(1)

    @unittest.skipUnless(sys.platform == 'win32', 'Windows file sharing regression')
    def test_atomic_reply_waits_for_brief_reader_lock(self):
        root = Path(__file__).resolve().parent
        with tempfile.TemporaryDirectory(dir=root) as directory:
            path = Path(directory)/'recommendation.json'
            atomic_json(path, {'snapshot_id': 'old'})
            errors = []
            def publish():
                try: atomic_json(path, {'snapshot_id': 'new'})
                except OSError as error: errors.append(error)
            with windows_reader_without_delete_share(path):
                writer = threading.Thread(target=publish)
                writer.start()
                time.sleep(.1)
                self.assertEqual(json.loads(path.read_text())['snapshot_id'], 'old')
            writer.join(2)
            self.assertFalse(writer.is_alive())
            self.assertEqual(errors, [])
            self.assertEqual(json.loads(path.read_text())['snapshot_id'], 'new')

    @unittest.skipUnless(sys.platform == 'win32', 'Windows file sharing regression')
    def test_atomic_reply_retains_previous_json_if_lock_persists(self):
        root = Path(__file__).resolve().parent
        with tempfile.TemporaryDirectory(dir=root) as directory:
            path = Path(directory)/'recommendation.json'
            atomic_json(path, {'snapshot_id': 'old'})
            start = time.monotonic()
            with windows_reader_without_delete_share(path):
                with self.assertRaises(PermissionError):
                    atomic_json(path, {'snapshot_id': 'new'})
            self.assertLess(time.monotonic()-start, 2)
            self.assertEqual(json.loads(path.read_text())['snapshot_id'], 'old')

    def test_stale_snapshot_age(self):
        self.assertGreater(snapshot_age({'captured_at_utc': (datetime.now(timezone.utc)-timedelta(seconds=20)).isoformat()}), 19)
        self.assertEqual(snapshot_age({}), float('inf'))

    @unittest.skipUnless((Path(__file__).resolve().parents[1]/'settings.json').exists(), 'needs local settings')
    def test_real_catalog_file_ipc_and_stale_invalidation(self):
        root = Path(__file__).resolve().parents[1]
        catalog = Catalog()
        gold = next(c for c in catalog.cards.values() if c.get('InternalName') == 'Meditation (Level Up)')
        with tempfile.TemporaryDirectory(dir=root/'tests') as directory:
            runtime = Path(directory)
            assert runtime.resolve().is_relative_to((root/'tests').resolve())
            s = state([card(gold['Id'])]); s['state_type'] = 'LevelUpState'
            atomic_json(runtime/'snapshot.json', s)
            worker = threading.Thread(target=serve, args=(runtime, 0, root/'settings.json'), kwargs={'max_seconds': 3}, daemon=True)
            worker.start()
            deadline=time.monotonic()+2
            while not (runtime/'recommendation.json').exists() and time.monotonic()<deadline: time.sleep(.05)
            result=json.loads((runtime/'recommendation.json').read_text(encoding='utf-8'))
            self.assertEqual(result['snapshot_id'], 'current')
            self.assertTrue(result['recommendations'])
            s['captured_at_utc']=(datetime.now(timezone.utc)-timedelta(seconds=20)).isoformat()
            atomic_json(runtime/'snapshot.json',s)
            time.sleep(.7)
            stale=json.loads((runtime/'recommendation.json').read_text(encoding='utf-8'))
            self.assertEqual(stale['recommendations'],[])
            self.assertIn('暂停推荐',stale['summary'])
            worker.join(4)
            self.assertFalse(worker.is_alive())


if __name__ == '__main__': unittest.main(verbosity=2)
