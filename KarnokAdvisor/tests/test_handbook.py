import copy
from contextlib import contextmanager
import ctypes
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import socket
import sqlite3
import sys
import tempfile
import threading
import time
import unittest
import uuid
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from advisor import DOCUMENT_KIND, catalog_stamp, make_document, serve, snapshot_age
from catalog import Catalog, atomic_json, default_cache, read_json
from strategy import StrategyBook, stage_for


def fixture_book():
    data = json.loads((ROOT / "strategies.json").read_text(encoding="utf-8"))
    names = set(n for route in data["routes"] for part in route["roles"] for n in part["items"])
    names.update(n for route in data["routes"] for n in route["anchors"])
    names.update(n for route in data["routes"] for stage in route["stages"] for n in stage["base_items"])
    names.update(option["name"] for route in data["routes"] for stage in route["stages"] for option in stage["optional_items"])
    cards = [{"Id": name, "Type": "Item", "Size": "Medium" if name == "Shotgun" else "Small",
              "ArtKey": uuid.uuid5(uuid.NAMESPACE_DNS, name).hex, "StartingTier": "Bronze",
              "Localization": {"Title": {"Text": name}, "Description": {"Text": "造成{ability.0}伤害，获得{aura.1}%"}},
              "Tiers": {"Bronze": {"Attributes": {}}}} for name in sorted(names)]
    catalog = Catalog(cards=cards)
    data["verified_catalog_digest"] = StrategyBook(catalog, data).digest
    return StrategyBook(catalog, data)


def state(board=(), **changes):
    return {"status": "live", "hero": "Karnok", "day": 2, "state_type": "ChoiceState", "snapshot_id": "current",
            "captured_at_utc": datetime.now(timezone.utc).isoformat(), "board": list(board) if board is not None else None, "stash": [],
            "unlocked_slots": list(range(6)), "player_attributes": {"Gold": 15, "Level": 1}, **changes}


def card(name, tier="Bronze"):
    return {"template_id": name, "tier": tier}


def route(reference, name):
    return next(r for r in reference["routes"] if r["id"] == name)


def components(route):
    return [item for stage in route["stages"] for item in stage["base_items"] + stage["optional_items"]]


@contextmanager
def windows_reader_without_delete_share(path):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.argtypes = [ctypes.c_wchar_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong, ctypes.c_void_p]
    kernel.CreateFileW.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.CreateFileW(str(path), 0x80000000, 3, None, 3, 0x80, None)
    if handle == ctypes.c_void_p(-1).value: raise ctypes.WinError(ctypes.get_last_error())
    try: yield
    finally: kernel.CloseHandle(handle)


class ReferenceTests(unittest.TestCase):
    def setUp(self): self.book = fixture_book()

    def test_all_routes_stages_and_images_resolve_without_scoring(self):
        reference = self.book.reference({})
        self.assertEqual([r["id"] for r in reference["routes"]], ["friends", "boa", "ammo", "poison", "health", "aurora", "burn"])
        for result, definition in zip(reference["routes"], self.book.data["routes"]):
            self.assertNotIn("fit", result)
            self.assertEqual([s["key"] for s in result["stages"]], ["early", "mid", "late"])
            for stage, original in zip(result["stages"], definition["stages"]):
                self.assertNotIn("items", stage)
                self.assertNotIn("slots", stage)
                self.assertTrue(stage["optional_items"])
                for item, name in zip(stage["base_items"], original["base_items"]):
                    self.assertEqual(item["template_id"], name)
                    self.assertEqual(item["core"], name in original["core_items"])
                    self.assertEqual(item["group"], "base")
                for item, option in zip(stage["optional_items"], original["optional_items"]):
                    self.assertEqual(item["template_id"], option["name"])
                    self.assertEqual(item["note"], option["note"])
                    self.assertEqual(item["use"], option["use"])
                    self.assertFalse(item["core"])
                    self.assertEqual(item["group"], "optional")
                for item in stage["base_items"] + stage["optional_items"]:
                    self.assertEqual(item["art_key"], self.book.templates[item["template_id"]]["ArtKey"])
                    self.assertTrue(item["role"])
                    self.assertEqual(item["owned"], "未核对")

    def test_all_positioning_blocks_preserve_sizes_order_and_ten_slot_geometry(self):
        for result in self.book.reference({})["routes"]:
            for stage in result["stages"]:
                position = stage["positioning"]
                self.assertTrue(position["available"])
                self.assertLessEqual(position["slots"], 10)
                cursor = 0
                for block in position["blocks"]:
                    self.assertEqual(block["start"], cursor)
                    self.assertGreater(block["span"], 0)
                    cursor += block["span"]
                self.assertEqual(cursor, 10)
                blocks = [block for block in position["blocks"] if block["kind"] == "item"]
                self.assertEqual([block["template_id"] for block in blocks], [item["template_id"] for item in stage["base_items"]])
                self.assertEqual([block["span"] for block in blocks], [item["span"] for item in stage["base_items"]])
                self.assertTrue(position["notes"])
                self.assertTrue(all(option["position_note"] for option in stage["optional_items"]))

    def test_curated_positioning_rejects_broken_direction_gaps_and_relations(self):
        for change in (lambda stage: stage["base_items"].reverse(),
                       lambda stage: stage["positioning"]["gaps_after"].update({"Spear": 0}),
                       lambda stage: stage["positioning"]["gaps_after"].update({"Unknown": 1}),
                       lambda stage: stage["positioning"]["links"][0].update(kind="nearby"),
                       lambda stage: stage["positioning"]["links"][0].update(target="Not in base")):
            data = copy.deepcopy(self.book.data)
            change(data["routes"][0]["stages"][0])
            with self.assertRaises(ValueError): StrategyBook(self.book.catalog, data)
        data = copy.deepcopy(self.book.data)
        stage = data["routes"][0]["stages"][0]
        stage["positioning"]["gaps_after"] = {"Flying Squirrel": 8}
        with self.assertRaises(ValueError): StrategyBook(self.book.catalog, data)

    def test_optional_position_space_keeps_fills_or_removes_the_explicit_gap(self):
        early = route(self.book.reference({}), "poison")["stages"][0]
        self.assertEqual(early["positioning"]["slots"], early["base_slots"] + 1)
        water = next(item for item in early["optional_items"] if item["template_id"] == "Waterskin")
        drop = next(item for item in early["optional_items"] if item["template_id"] == "Drowsdrop")
        self.assertEqual(water["result_position_slots"], water["result_slots"] + 1)
        self.assertEqual(drop["result_position_slots"], drop["result_slots"])
        data = copy.deepcopy(self.book.data)
        definition = next(r for r in data["routes"] if r["id"] == "poison")["stages"][0]
        definition["optional_items"].append({"name": "Torch", "use": "synthetic replacement", "note": "test only",
            "replaces": ["Worry Wart"], "position_note": "The slow source was removed", "fills_gap_after": []})
        modified = route(StrategyBook(self.book.catalog, data).reference({}), "poison")["stages"][0]
        replacement = next(item for item in modified["optional_items"] if item["template_id"] == "Torch")
        self.assertEqual(replacement["result_position_slots"], replacement["result_slots"])
        definition["optional_items"][-1]["fills_gap_after"] = ["Worry Wart"]
        with self.assertRaises(ValueError): StrategyBook(self.book.catalog, data)
        safe_fillers = {"Lichen", "Drowsdrop", "Security Blanket", "Hunter's Blind", "Ghillie Suit"}
        for route_definition in self.book.data["routes"]:
            for stage_definition in route_definition["stages"]:
                for option in stage_definition["optional_items"]:
                    if option["fills_gap_after"]: self.assertIn(option["name"], safe_fillers)

    def test_unreviewed_or_unavailable_templates_never_publish_usable_positioning(self):
        self.book.templates["Spear"] = None
        position = route(self.book.reference({}), "friends")["stages"][0]["positioning"]
        self.assertFalse(position["available"])
        self.assertEqual(position["blocks"], [])
        catalog = copy.deepcopy(self.book.catalog)
        catalog.cards["Tent"]["Size"] = "Large"
        updated = StrategyBook(catalog, self.book.data)
        for result in updated.reference({})["routes"]:
            for stage in result["stages"]:
                self.assertFalse(stage["positioning"]["available"])
                self.assertEqual(stage["positioning"]["blocks"], [])
                self.assertTrue(all(item["result_position_slots"] is None for item in stage["optional_items"]))

    def test_catalog_failure_disables_cached_positioning_until_catalog_recovers(self):
        snapshot = state([card("Shotgun")])
        failed = make_document(self.book, snapshot, catalog_ok=False)
        reference = failed["reference"]
        self.assertEqual(failed["status"], "catalog_error")
        self.assertFalse(reference["verified"])
        self.assertFalse(reference["live"])
        self.assertIn("暂不可读", reference["version_note"])
        self.assertTrue(self.book.verified)
        self.assertEqual(len(reference["routes"]), 7)
        for result in reference["routes"]:
            for stage in result["stages"]:
                self.assertFalse(stage["positioning"]["available"])
                self.assertIsNone(stage["positioning"]["slots"])
                self.assertEqual(stage["positioning"]["blocks"], [])
                self.assertTrue(all(item["owned"] == "未核对" and item["art_key"] for item in stage["base_items"]))
                self.assertTrue(all(item["result_position_slots"] is None for item in stage["optional_items"]))
        recovered = make_document(self.book, snapshot)["reference"]
        self.assertTrue(recovered["verified"])
        self.assertTrue(recovered["live"])
        self.assertTrue(all(stage["positioning"]["available"] for result in recovered["routes"] for stage in result["stages"]))
        offline = make_document(self.book, {})["reference"]
        self.assertFalse(offline["live"])
        self.assertTrue(all(stage["positioning"]["available"] for result in offline["routes"] for stage in result["stages"]))

    def test_item_tags_use_native_type_translation_and_exclude_effect_keywords(self):
        template = self.book.catalog.cards["Spear"]
        template["Tags"] = ["Weapon", "Friend", "Weapon"]
        template["HiddenTags"] = ["Damage", "Burn"]
        self.book.catalog.translations.update({"18c83669920215a818638ad0e5421e4b": "武器", "930a91848917f92cf7e2f8d744fa4177": "伙伴"})
        self.assertEqual(self.book.item("Spear")["tags"], ["武器", "伙伴"])

    def test_item_tags_distinguish_no_type_from_unavailable_template_and_keep_unknown_types(self):
        self.assertEqual(self.book.item("Toxic Sigil")["tags"], [])
        self.book.catalog.cards["Spear"]["Tags"] = ["FutureType"]
        self.assertEqual(self.book.item("Spear")["tags"], ["FutureType"])
        self.book.templates["Shotgun"] = None
        self.assertIsNone(self.book.item("Shotgun")["tags"])

    def test_optional_components_do_not_inflate_base_slots_or_become_phase_cores(self):
        guide = self.book.reference(state([card("Honey Badger")], stash=[card("Spear")]))
        friends = route(guide, "friends")["stages"][0]
        self.assertEqual(friends["base_slots"], sum(item["span"] for item in friends["base_items"]))
        self.assertTrue(any(item["owned"] == "场上" for item in friends["optional_items"]))
        self.assertTrue(all(item["owned"] != "场上" for item in friends["base_items"]))
        self.assertFalse(any(item["core"] for item in friends["optional_items"]))
        self.assertEqual(next(item for item in friends["base_items"] if item["template_id"] == "Spear")["core_label"], "过渡核心")
        self.assertIsNone(guide["matched_route_id"])

    def test_replacements_account_for_removed_size_and_can_still_exceed_capacity(self):
        data = copy.deepcopy(self.book.data)
        ammo = next(route for route in data["routes"] if route["id"] == "ammo")
        ammo["stages"][0]["optional_items"][1]["replaces"] = ["Utility Belt"]
        catalog = copy.deepcopy(self.book.catalog)
        for name in ("Shotgun", "Utility Belt", "Heavy Crossbow"):
            catalog.cards[name]["Size"] = "Large"
        book = StrategyBook(catalog, data)
        stage = route(book.reference({}), "ammo")["stages"][0]
        crossbow = next(item for item in stage["optional_items"] if item["template_id"] == "Heavy Crossbow")
        self.assertEqual(stage["base_slots"], 6)
        self.assertEqual(crossbow["result_slots"], 6)
        self.assertEqual(crossbow["replaces"], [{"name": "Utility Belt", "template_id": "Utility Belt"}])
        self.assertEqual(crossbow["core_label"], "")
        # A synthetically larger foundation still needs an explicit total, not a promise that replacing fits.
        ammo["stages"][0]["base_items"] += ["Bear Trap", "Waterskin", "Honey Jar", "Fairies"]
        ammo["stages"][0]["optional_items"] = [ammo["stages"][0]["optional_items"][1]]
        ammo["stages"][0]["optional_items"][0]["replaces"] = ["Fairies"]
        stage = route(StrategyBook(catalog, data).reference({}), "ammo")["stages"][0]
        self.assertEqual(stage["base_slots"], 10)
        self.assertEqual(stage["optional_items"][0]["result_slots"], 12)
        self.assertFalse(stage["optional_items"][0]["core"])

    def test_phase_core_alternative_is_labelled_without_changing_route_identity(self):
        stage = route(self.book.reference(state([card("Heavy Crossbow")])), "ammo")["stages"][0]
        crossbow = next(item for item in stage["optional_items"] if item["template_id"] == "Heavy Crossbow")
        self.assertEqual(crossbow["core_label"], "核心替代")
        self.assertFalse(crossbow["core"])
        self.assertEqual(crossbow["result_slots"], stage["base_slots"] - 2 + 1)

    def test_invalid_component_definitions_fail_instead_of_double_counting(self):
        for change in (lambda stage: stage["base_items"].append(stage["base_items"][0]),
                       lambda stage: stage["core_items"].append("Not a base card"),
                       lambda stage: stage["optional_items"].append(copy.deepcopy(stage["optional_items"][0])),
                       lambda stage: stage["optional_items"][0].update(name=stage["base_items"][0]),
                       lambda stage: stage["optional_items"][0].update(replaces=["Not a base card"]),
                       lambda stage: stage["optional_items"][0].update(note="")):
            data = copy.deepcopy(self.book.data)
            change(data["routes"][0]["stages"][0])
            with self.assertRaises(ValueError): StrategyBook(self.book.catalog, data)

    def test_digest_changes_with_art_identity_but_not_inventory(self):
        original = self.book.reference({})["catalog_digest"]
        self.assertEqual(original, self.book.reference(state([card("Shotgun")]))["catalog_digest"])
        self.book.catalog.cards["Shotgun"]["ArtKey"] = uuid.uuid4().hex
        self.assertNotEqual(original, StrategyBook(self.book.catalog, self.book.data).digest)

    def test_owned_and_stash_are_distinct_and_current_tier_is_separate(self):
        reference = self.book.reference(state([card("Shotgun", "Gold")], stash=[card("Tent", "Silver")]))
        ammo = route(reference, "ammo")
        shotgun = ammo["stages"][0]["base_items"][0]
        self.assertEqual(shotgun["owned"], "场上")
        self.assertEqual(shotgun["owned_tier"], "黄金")
        self.assertEqual(shotgun["starting_tier"], "青铜")
        self.assertEqual(self.book.item("Tent", [], [card("Tent")])["owned"], "仓库")
        self.assertEqual(reference["matched_route_id"], "ammo")
        self.assertEqual(ammo["stages"][1]["base_items"][1]["owned"], "仓库")
        self.assertEqual(ammo["role_count"], 1)

    def test_empty_inventory_is_known_but_unavailable_inventory_is_not(self):
        self.assertTrue(self.book.reference(state())["live"])
        for changes in ({"board": None}, {"stash": None}, {"unlocked_slots": None}):
            guide = self.book.reference(state(**{"board": [card("Shotgun")], **changes}))
            self.assertFalse(guide["live"])
            self.assertIsNone(guide["matched_route_id"])
            self.assertTrue(all(item["owned"] == "未核对" for r in guide["routes"] for item in components(r)))

    def test_menu_replay_other_hero_and_unknown_state_never_claim_ownership(self):
        for changes in ({"status": "menu"}, {"hero": "Dooley"}, {"is_replay": True}, {"state_type": "Unknown"}, {"day": None}):
            guide = self.book.reference(state([card("Shotgun")], **changes))
            self.assertFalse(guide["live"])
            self.assertTrue(all(r["core_count"] == 0 for r in guide["routes"]))
            self.assertTrue(all(item["owned"] == "未核对" for r in guide["routes"] for item in components(r)))
            self.assertEqual(route(guide, "ammo")["stages"][0]["base_items"][0]["owned"], "未核对")

    def test_combat_can_browse_and_a_reserve_never_matches_a_route(self):
        self.assertTrue(self.book.reference(state(in_combat=True, state_type="UnfamiliarCombatState"))["live"])
        guide = self.book.reference(state(stash=[card("Shotgun"), card("Tent")]))
        self.assertIsNone(guide["matched_route_id"])
        self.assertEqual(route(guide, "ammo")["core_count"], 0)

    def test_generic_transition_card_never_locks_a_build_and_order_never_changes(self):
        self.assertIsNone(self.book.reference(state([card("Spear")]))["matched_route_id"])
        expected = [r["id"] for r in self.book.reference({})["routes"]]
        self.assertEqual([r["id"] for r in self.book.reference(state([card("Shotgun")]))["routes"]], expected)

    def test_single_active_lost_blade_does_not_match_multi_active_health_recipe(self):
        blade = copy.deepcopy(self.book.catalog.cards["Spear"])
        blade["Id"] = "lost-blade"
        blade["Localization"]["Title"]["Text"] = "Lost Blade"
        self.book.catalog.cards[blade["Id"]] = blade
        book = StrategyBook(self.book.catalog, self.book.data)
        guide = book.reference(state([card(blade["Id"])]))
        self.assertIsNone(guide["matched_route_id"])
        self.assertEqual(route(guide, "health")["core_count"], 0)
        self.assertNotIn("Lost Blade", next(r for r in book.data["routes"] if r["id"] == "health")["anchors"])
        self.assertTrue(all(item["template_id"] != blade["Id"] for item in components(route(guide, "health"))))

    def test_stage_boundaries_and_effect_placeholders_remain_explicit(self):
        self.assertEqual([stage_for(d) for d in (None, False, 0, 1, 3, 4, 7, 8)], [None, None, None, "early", "early", "mid", "mid", "late"])
        effect = self.book.item("Shotgun")["effects"][0]
        self.assertEqual(effect.count("［数值待确认］"), 2)
        self.assertNotIn("{", effect)
        self.assertIn("不是卡牌出现日", self.book.data["stage_note"])

    def test_missing_or_ambiguous_template_never_borrows_another_image(self):
        self.book.templates["Shotgun"] = None
        item = self.book.item("Shotgun")
        self.assertFalse(item["known"])
        self.assertIn("Shotgun", item["name"])
        self.assertEqual(item["art_key"], "")
        self.assertIsNone(route(self.book.reference({}), "ammo")["stages"][0]["base_slots"])
        duplicate = copy.deepcopy(self.book.catalog.cards["Tent"])
        duplicate["Id"] = "another-tent"
        self.book.catalog.cards[duplicate["Id"]] = duplicate
        self.assertFalse(StrategyBook(self.book.catalog, self.book.data).item("Tent")["known"])

    def test_template_update_warns_without_removing_the_reference(self):
        self.book.catalog.cards["Tent"]["Size"] = "Large"
        updated = StrategyBook(self.book.catalog, self.book.data)
        self.assertFalse(updated.verified)
        self.assertEqual(len(updated.reference({})["routes"]), 7)
        self.assertIn("请核对", updated.reference({})["version_note"])

    def test_reference_is_pure_and_observation_identity_is_authoritative(self):
        snapshot = state([{"template_id": "unrelated", "name": "Shotgun"}])
        before = copy.deepcopy(snapshot)
        guide = self.book.reference(snapshot)
        self.assertIsNone(guide["matched_route_id"])
        self.assertEqual(snapshot, before)

    def test_freshness_catalog_error_and_new_run_invalidate_all_dynamic_facts(self):
        ready = make_document(self.book, state([card("Shotgun")]))
        self.assertTrue(ready["reference"]["live"])
        for snapshot, healthy in ((state([card("Shotgun")], captured_at_utc=(datetime.now(timezone.utc)-timedelta(seconds=20)).isoformat()), True),
                                  (state([card("Shotgun")]), False), ({}, True)):
            doc = make_document(self.book, snapshot, catalog_ok=healthy)
            self.assertEqual(doc["kind"], DOCUMENT_KIND)
            self.assertFalse(doc["reference"]["live"])
            self.assertTrue(all(item["owned"] == "未核对" for item in components(route(doc["reference"], "ammo"))))
        new_run = make_document(self.book, state(snapshot_id="new-run"))
        self.assertIsNone(new_run["reference"]["matched_route_id"])
        self.assertNotIn("recommendations", new_run)


class WorkerTests(unittest.TestCase):
    def test_reader_handles_brief_atomic_publication_gap(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "tests") as directory:
            path = Path(directory) / "snapshot.json"
            publisher = threading.Timer(.06, atomic_json, args=(path, {"snapshot_id": "new"}))
            publisher.start()
            try: self.assertEqual(read_json(path)["snapshot_id"], "new")
            finally: publisher.join(1)

    @unittest.skipUnless(sys.platform == "win32", "Windows sharing regression")
    def test_atomic_reply_waits_for_reader_without_truncating_old_json(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "tests") as directory:
            path = Path(directory) / "guide.json"
            atomic_json(path, {"snapshot_id": "old"})
            errors = []
            def publish():
                try: atomic_json(path, {"snapshot_id": "new"})
                except OSError as error: errors.append(error)
            with windows_reader_without_delete_share(path):
                thread = threading.Thread(target=publish)
                thread.start()
                time.sleep(.1)
                self.assertEqual(read_json(path)["snapshot_id"], "old")
            thread.join(2)
            self.assertEqual(errors, [])
            self.assertEqual(read_json(path)["snapshot_id"], "new")

    @unittest.skipUnless(sys.platform == "win32", "Windows sharing regression")
    def test_persistent_lock_retains_previous_valid_reply(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "tests") as directory:
            path = Path(directory) / "guide.json"
            atomic_json(path, {"snapshot_id": "old"})
            with windows_reader_without_delete_share(path):
                with self.assertRaises(PermissionError): atomic_json(path, {"snapshot_id": "new"})
            self.assertEqual(read_json(path)["snapshot_id"], "old")

    def test_catalog_stamp_tracks_chinese_translation_changes(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "tests") as directory:
            cache = Path(directory)
            (cache / "translations").mkdir()
            (cache / "GameData.db").write_bytes(b"db")
            before = catalog_stamp(cache)
            (cache / "translations/zh-CN.bytes").write_bytes(b"new translation")
            self.assertNotEqual(catalog_stamp(cache), before)

    def test_invalid_or_naive_timestamp_is_not_live(self):
        self.assertEqual(snapshot_age({}), float("inf"))
        self.assertEqual(snapshot_age({"captured_at_utc": "invalid"}), float("inf"))
        self.assertEqual(snapshot_age({"captured_at_utc": "2026-10-11T12:00:00"}), float("inf"))

    @unittest.skipUnless((default_cache() / "GameData.db").exists(), "Installed game catalog unavailable")
    def test_real_worker_ignores_legacy_api_settings_and_invalidates_old_run(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "tests") as directory:
            runtime = Path(directory)
            settings = runtime / "settings.json"
            atomic_json(settings, {"cache_dir": "", "model_api": {"enabled": True, "key_file": "must-not-be-read"}})
            book = StrategyBook(Catalog())
            shotgun = book.templates["Shotgun"]["Id"]
            atomic_json(runtime / "snapshot.json", state([card(shotgun)]))
            with patch.object(socket, "socket", side_effect=AssertionError("Handbook must stay offline")):
                thread = threading.Thread(target=serve, args=(runtime, 0, settings), kwargs={"max_seconds": 3.2})
                thread.start()
                def wait_for(predicate):
                    deadline = time.monotonic() + 1.5
                    while time.monotonic() < deadline:
                        if (runtime / "guide.json").exists():
                            document = read_json(runtime / "guide.json")
                            if predicate(document): return document
                        time.sleep(.04)
                    self.fail("Worker did not publish expected reference")
                try:
                    live = wait_for(lambda d: d["status"] == "live")
                    self.assertEqual(route(live["reference"], "ammo")["core_count"], 1)
                    atomic_json(runtime / "snapshot.json", state(snapshot_id="new-run", hero="Dooley"))
                    other = wait_for(lambda d: d["snapshot_id"] == "new-run")
                    self.assertFalse(other["reference"]["live"])
                    self.assertTrue(all(item["owned"] == "未核对" for item in components(route(other["reference"], "ammo"))))
                    stale = state(snapshot_id="stale", captured_at_utc=(datetime.now(timezone.utc)-timedelta(seconds=20)).isoformat())
                    atomic_json(runtime / "snapshot.json", stale)
                    self.assertEqual(wait_for(lambda d: d["snapshot_id"] == "stale")["status"], "stale")
                finally: thread.join(5)
            self.assertFalse(thread.is_alive())
            self.assertFalse((runtime / "decisions.jsonl").exists())
            self.assertFalse((runtime / "recommendation.json").exists())

    def test_database_reload_failure_preserves_static_guide_and_does_not_end_worker(self):
        with tempfile.TemporaryDirectory(dir=ROOT / "tests") as directory:
            runtime = Path(directory)
            book = fixture_book()
            atomic_json(runtime / "snapshot.json", state([card("Shotgun")]))
            with patch("advisor.Catalog", side_effect=[book.catalog, sqlite3.DatabaseError("database updating")]), \
                 patch("advisor.StrategyBook", return_value=book), patch("advisor.catalog_stamp", side_effect=[1, 2]):
                thread = threading.Thread(target=serve, args=(runtime,), kwargs={"max_seconds": 5.8})
                thread.start()
                thread.join(7)
            self.assertFalse(thread.is_alive())
            document = read_json(runtime / "guide.json")
            self.assertEqual(document["status"], "catalog_error")
            self.assertEqual(len(document["reference"]["routes"]), 7)
            self.assertFalse(document["reference"]["live"])


class InstalledCatalogTests(unittest.TestCase):
    def test_all_reference_cards_match_installed_catalog_and_boards_fit(self):
        if not (default_cache() / "GameData.db").exists(): self.skipTest("Installed game catalog unavailable")
        book = StrategyBook(Catalog())
        self.assertTrue(all(book.templates.values()))
        self.assertTrue(book.verified)
        if book.catalog.translations:
            self.assertEqual(book.item("Wild Bear")["tags"], ["武器", "伙伴"])
            self.assertEqual(book.item("Waterskin")["tags"], ["食物"])
            self.assertEqual(book.item("Hunter's Pack")["tags"], ["服饰"])
        self.assertEqual(book.item("Toxic Sigil")["tags"], [])
        self.assertLessEqual(len({template["ArtKey"] for template in book.templates.values()}), 64)
        for result in book.reference({})["routes"]:
            self.assertTrue(result["sources"])
            for stage in result["stages"]:
                self.assertLessEqual(stage["base_slots"], 10)
                self.assertTrue(stage["positioning"]["available"])
                self.assertLessEqual(stage["positioning"]["slots"], 10)
                for item in stage["base_items"] + stage["optional_items"]:
                    self.assertEqual(item["art_key"], book.catalog.cards[item["template_id"]]["ArtKey"])
                    self.assertTrue(item["effects"])


if __name__ == "__main__": unittest.main()
