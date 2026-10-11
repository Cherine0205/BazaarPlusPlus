import copy
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from advisor import model_context
from catalog import Catalog, TIERS, default_cache
from engine import Engine
from strategy import AMMO_WEAPONS, HEALTH_WEAPONS, SELF_SLOW, StrategyBook, stage_for


def fixture_engine():
    data = json.loads(Path(__file__).resolve().parents[1].joinpath("strategies.json").read_text(encoding="utf-8"))
    names = set(AMMO_WEAPONS + HEALTH_WEAPONS + SELF_SLOW)
    for route in data["routes"]:
        names.update(route["anchors"])
        for part in route["roles"] + route["stages"]: names.update(part["items"])
    medium = {"Tent", "Shotgun", "Boa Constrictor", "Lost Blade", "Forest Cloak"}
    passive = {"Tent", "Forest Cloak", "Hunter's Journal", "Toxic Sigil", "Flame Sigil", "Wing Sigil"}
    weapons = set(AMMO_WEAPONS + HEALTH_WEAPONS) | {"Spear", "Boa Constrictor", "Outlands Terror"}
    cards = [{"Id": n, "InternalName": n, "Type": "Item", "StartingTier": "Bronze",
              "Size": "Medium" if n in medium else "Small", "Tags": ["Weapon"] if n in weapons else [],
              "Localization": {"Title": {"Text": n}}, "HiddenTags": [],
              "Tiers": {tier: {"Attributes": {"DamageAmount": 10 * (i + 1) if n in weapons else 0,
                         "CooldownMax": 0 if n in passive else 6000, "Multicast": 1, "BuyPrice": 2, "SellPrice": 1}}
                        for i, tier in enumerate(TIERS)}} for n in sorted(names)]
    catalog = Catalog(cards=cards)
    data["verified_catalog_digest"] = StrategyBook(catalog, data).digest
    return Engine(catalog, strategy_data=data)


def card(name, socket=0, **attributes):
    return {"template_id": name, "instance_id": name, "socket": socket, "attributes": attributes}


def state(board=(), options=(), **changes):
    return {"status": "live", "state_type": "EncounterState", "hero": "Karnok", "day": 6,
            "snapshot_id": "current", "board": list(board), "stash": [], "skills": [], "options": list(options),
            "unlocked_slots": list(range(10)), "player_attributes": {"Gold": 20, "HealthMax": 1000, "Prestige": 20},
            "selection_rules": {"is_free": False, "can_exit": True}, **changes}


class StrategyTests(unittest.TestCase):
    def setUp(self):
        self.engine = fixture_engine()
        self.book = self.engine.strategies

    def prepared(self, s): return self.engine.prepare(s)

    def link_ids(self, s): return {l["id"] for l in self.book.links(self.prepared(s))}

    def test_menu_and_unknown_run_metadata_never_report_current_components(self):
        for changes in ({"status": "menu"}, {"state_type": "Unknown"}, {"unlocked_slots": None}, {"day": None}, {"is_replay": True}):
            guide = self.engine.recommend(state([card("Shotgun")], **changes))["strategy"]
            self.assertFalse(guide["live"])
            self.assertIsNone(guide["stage"])
            self.assertTrue(all(not r["owned_board"] and not r["missing_roles"] for r in guide["routes"]))
            self.assertEqual(self.book.value(self.prepared(state([card("Shotgun")], **changes))), 0)

    def test_reference_remains_available_without_options_and_during_combat(self):
        for changes in ({}, {"in_combat": True, "state_type": "UnfamiliarCombatState"}):
            result = self.engine.recommend(state([card("Shotgun")], **changes))
            self.assertEqual(result["recommendations"], [])
            self.assertTrue(result["strategy"]["live"])
            self.assertEqual(result["strategy"]["routes"][0]["id"], "ammo")

    def test_stash_is_a_reserve_and_does_not_activate_a_route_or_bonus(self):
        s = state(stash=[card("Shotgun"), card("Tent"), card("Chains")])
        guide = self.book.reference(self.prepared(s))
        ammo = next(r for r in guide["routes"] if r["id"] == "ammo")
        self.assertFalse(ammo["signal"])
        self.assertEqual(ammo["owned_board"], [])
        self.assertEqual(len(ammo["owned_stash"]), 3)
        self.assertEqual(self.book.value(self.prepared(s)), 0)

    def test_generic_transition_card_does_not_lock_a_route(self):
        guide = self.book.reference(self.prepared(state([card("Spear")])) )
        self.assertTrue(all(r["fit"] == 0 and not r["signal"] for r in guide["routes"]))

    def test_planning_boundaries_and_unknown_day(self):
        self.assertEqual([stage_for(d) for d in (None, 0, 1, 3, 4, 7, 8, 20)],
                         [None, None, "early", "early", "mid", "mid", "late", "late"])
        self.assertIn("不是卡牌出现日", self.book.reference({})["stage_note"])

    def test_direction_and_adjacency_use_sockets_and_item_spans(self):
        self.assertIn("squirrel", self.link_ids(state([card("Shotgun", 1), card("Flying Squirrel", 0)])))
        self.assertNotIn("squirrel", self.link_ids(state([card("Shotgun", 0), card("Flying Squirrel", 2)])))
        self.assertNotIn("squirrel", self.link_ids(state([card("Flying Squirrel", 0), card("Shotgun", 2)])))

    def test_tent_requires_adjacent_ammo_and_self_slow_supply(self):
        s = state([card("Shotgun", 0), card("Tent", 2)])
        self.assertNotIn("tent_reload", self.link_ids(s))
        s["board"].append(card("Chains", 4))
        self.assertIn("tent_reload", self.link_ids(s))
        s["board"][1]["socket"] = 3
        self.assertNotIn("tent_reload", self.link_ids(s))

    def test_radiant_cuts_the_self_slow_link(self):
        shotgun = {**card("Shotgun", 0), "enchantment": "Radiant"}
        s = state([shotgun, card("Tent", 2), card("Chains", 4)])
        self.assertNotIn("tent_reload", self.link_ids(s))
        boa = {**card("Boa Constrictor", 0), "enchantment": "Radiant"}
        self.assertNotIn("honey_boa", self.link_ids(state([boa, card("Honey Jar", 2)])))

    def test_squirrel_without_a_right_target_is_not_a_rage_engine(self):
        s = state([card("Outlands Terror", 0), card("Wing Sigil", 1), card("Flying Squirrel", 2)])
        self.assertNotIn("terror_rage", self.link_ids(s))
        s["board"] = [card("Flying Squirrel", 0), card("Outlands Terror", 1), card("Wing Sigil", 2)]
        self.assertIn("terror_rage", self.link_ids(s))

    def test_waterskin_needs_an_active_left_neighbor_to_provide_healing(self):
        s = state([card("Waterskin", 0), card("Geyser", 1)])
        self.assertNotIn("geyser_heal", self.link_ids(s))
        s["board"] = [card("Forest Cloak", 0), card("Waterskin", 2), card("Geyser", 3)]
        self.assertNotIn("geyser_heal", self.link_ids(s))
        s["board"] = [card("Shotgun", 0), card("Waterskin", 2), card("Geyser", 3)]
        self.assertIn("geyser_heal", self.link_ids(s))

    def test_selling_an_enabler_subtracts_its_synergy(self):
        before = self.prepared(state([card("Boa Constrictor", 0), card("Chains", 2)]))
        after = {**before, "board": before["board"][:1]}
        delta, reasons = self.book.change(before, after)
        self.assertLess(delta, 0)
        self.assertTrue(any("会拆掉联动" in reason for reason in reasons))

    def test_early_synergy_is_smaller_and_total_is_bounded(self):
        board = [card("Boa Constrictor", 0), card("Chains", 2), card("Honey Jar", 3)]
        self.assertLess(self.book.value(self.prepared(state(board, day=2))), self.book.value(self.prepared(state(board))))
        self.assertLessEqual(self.book.value(self.prepared(state(board))), 12)

    def test_shop_chooses_a_verified_adjacent_position_without_mutating_options(self):
        s = state([card("Chains", 5), card("Tent", 0)], [card("Shotgun", None)])
        original = copy.deepcopy(s)
        prepared = self.prepared(s)
        plan = self.engine.shop_plan(prepared["options"][0], prepared)
        self.assertTrue(plan["eligible"])
        self.assertIn("放到第 3–4 格", plan["steps"])
        self.assertTrue(any("帐篷" in r for r in plan["reasons"]))
        self.assertEqual(s, original)
        self.assertIsNone(prepared["options"][0]["socket"])

    def test_guide_never_overrides_insufficient_gold(self):
        s = state([card("Tent", 0), card("Chains", 4)], [card("Shotgun", None, BuyPrice=99)])
        result = self.engine.recommend(s)
        candidate = next(c for c in result["candidates"] if c["action_id"] == "Shotgun")
        self.assertFalse(candidate["eligible"])
        self.assertEqual(result["recommendations"][0]["action_id"], "__skip__")

    def test_lost_blade_multi_active_counterfactual_is_not_claimed_supported(self):
        s = state([card("Lost Blade", 0)], [card("Shotgun", None)], unlocked_slots=list(range(4)))
        prepared = self.prepared(s)
        plan = self.engine.shop_plan(prepared["options"][0], prepared)
        self.assertTrue(plan["eligible"])
        self.assertTrue(any("先卖出 Lost Blade" in step for step in plan["steps"]))

    def test_lost_blade_guard_allows_passive_purchases_without_a_condition_change(self):
        s = state([card("Lost Blade", 0), card("Shotgun", 2)], [card("Forest Cloak", None)])
        prepared = self.prepared(s)
        plan = self.engine.shop_plan(prepared["options"][0], prepared)
        self.assertTrue(plan["eligible"])
        self.assertFalse(any("先卖出" in step for step in plan["steps"]))

    def test_health_bonus_requires_an_active_health_weapon_and_is_bounded(self):
        self.assertEqual(self.book.health_adjustment(self.prepared(state(stash=[card("Runic Claymore")])), 1000), 0)
        ready = self.prepared(state([card("Runic Claymore")]))
        self.assertEqual(self.book.health_adjustment(ready, 1000), 6)
        self.assertEqual(self.book.health_adjustment(ready, -1000), -6)
        action = {"$type": "TActionPlayerModifyAttribute", "Target": {"TargetMode": "Self"},
                  "AttributeType": "HealthMax", "Operation": "Add", "Value": {"$type": "TFixedValue", "Value": 100}}
        benefit = self.engine.action(action, ready, {}, 0)
        self.assertAlmostEqual(benefit.score, 2.5)
        self.assertTrue(any("生命路线" in r for r in benefit.reasons))

    def test_template_changes_disable_all_guide_score_adjustments(self):
        self.engine.catalog.cards["Tent"]["Tiers"]["Bronze"]["Attributes"]["DamageAmount"] = 123
        changed = StrategyBook(self.engine.catalog, self.book.data)
        ready = self.prepared(state([card("Shotgun", 0), card("Tent", 2), card("Chains", 4)]))
        self.assertFalse(changed.verified)
        self.assertEqual(changed.value(ready), 0)
        self.assertEqual(changed.health_adjustment(ready, 100), 0)
        self.assertIn("已停用", changed.reference(ready)["version_note"])

    def test_model_gets_same_current_components_stage_and_version_guard(self):
        s = state([card("Shotgun", 0)], stash=[card("Tent", 2)])
        before = copy.deepcopy(s)
        reference = model_context(self.engine, s)["strategy_reference"]
        ammo = next(r for r in reference["routes"] if r["name"].startswith("弹药"))
        self.assertTrue(reference["verified"] and reference["live"])
        self.assertEqual(reference["stage"], "mid")
        self.assertEqual(ammo["owned_board"], ["Shotgun"])
        self.assertEqual(ammo["owned_stash"], ["Tent"])
        self.assertEqual(ammo["current_stage"]["key"], "mid")
        self.assertEqual(s, before)

    def test_direct_gold_upgrade_preserves_observed_permanent_damage(self):
        s = self.prepared(state([card("Shotgun", 0, DamageAmount=70)]))
        seen = []
        original_power = self.engine.power
        def observe(c, current):
            seen.append(copy.deepcopy(c))
            return original_power(c, current)
        action = {"$type": "TActionCardUpgrade", "UpgradeToTier": "Gold", "InheritEnchantment": True,
                  "Target": {"$type": "TTargetCardSection", "TargetSection": "SelfHand"}}
        with patch.object(self.engine, "power", side_effect=observe):
            benefit = self.engine.action(action, s, {}, 0)
        upgraded = next(c for c in seen if c["tier"] == "Gold")
        self.assertEqual(upgraded["attributes"]["DamageAmount"], 90)
        self.assertEqual(s["board"][0]["attributes"]["DamageAmount"], 70)
        self.assertGreater(benefit.score, 0)
        self.assertIn("黄金", benefit.reasons[0])

    def test_upgrade_does_not_count_all_targets_when_only_one_is_affected(self):
        s = self.prepared(state([card("Shotgun", 0), card("Boa Constrictor", 2)]))
        action = {"$type": "TActionCardUpgrade", "TargetCount": {"$type": "TFixedValue", "Value": 1},
                  "Target": {"$type": "TTargetCardSection", "TargetSection": "SelfHand"}}
        benefit = self.engine.action(action, s, {}, 0)
        self.assertEqual(benefit.known, 0)
        self.assertEqual(benefit.score, 0)
        self.assertTrue(any("实际目标未确定" in u for u in benefit.uncertainties))

    def test_upgrade_to_same_or_lower_tier_has_no_invented_gain(self):
        s = self.prepared(state([{**card("Shotgun", 0), "tier": "Gold"}]))
        action = {"$type": "TActionCardUpgrade", "UpgradeToTier": "Gold",
                  "Target": {"$type": "TTargetCardSection", "TargetSection": "SelfHand"}}
        self.assertEqual(self.engine.action(action, s, {}, 0).score, 0)

    def test_unknown_enchantment_loss_cannot_hide_behind_a_gold_reward(self):
        s = self.prepared(state([{**card("Shotgun", 0), "enchantment": "Radiant"}]))
        action = {"$type": "TActionAnd", "Actions": [
            {"$type": "TActionPlayerModifyAttribute", "Target": {"TargetMode": "Self"}, "AttributeType": "Gold",
             "Operation": "Add", "Value": {"$type": "TFixedValue", "Value": 5}},
            {"$type": "TActionCardUpgrade", "InheritEnchantment": False,
             "Target": {"$type": "TTargetCardSection", "TargetSection": "SelfHand"}}]}
        benefit = self.engine.action(action, s, {}, 0)
        self.assertTrue(benefit.critical_unknown)
        self.assertTrue(any("附魔损失" in u for u in benefit.uncertainties))

    def test_multicast_one_is_one_activation_rather_than_two(self):
        s = self.prepared(state())
        single = self.engine.catalog.enrich(card("Shotgun", 0, DamageAmount=120, Multicast=1))
        double = self.engine.catalog.enrich(card("Shotgun", 0, DamageAmount=120, Multicast=2))
        self.assertAlmostEqual(self.engine.power(single, s), 21.5)
        self.assertAlmostEqual(self.engine.power(double, s), 41.5)


class InstalledCatalogStrategyTests(unittest.TestCase):
    def test_reference_resolves_real_items_and_all_example_boards_fit(self):
        if not (default_cache() / "GameData.db").exists(): self.skipTest("Installed game cache unavailable")
        book = StrategyBook(Catalog())
        self.assertTrue(all(book.templates.values()))
        guide = book.reference({})
        self.assertEqual(len(guide["routes"]), 7)
        for route in guide["routes"]:
            self.assertTrue(route["sources"])
            self.assertTrue(all(s["url"].startswith("https://") and s["date"] for s in route["sources"]))
            for stage in route["stages"]:
                self.assertLessEqual(stage["slots"], 10, route["name"])
                self.assertTrue(all(item["known"] and item["starting_tier"] for item in stage["items"]))
        if book.verified:
            self.assertIn("黄金 D6+", guide["tier_note"])
            trapvine = book.catalog.template_card(book.templates["Trapvine"]["Id"])
            self.assertEqual(trapvine["tier"], "Gold")
            self.assertTrue(any("充能" in text for text in book.catalog.texts(trapvine)))
        else:
            self.assertIn("已停用", guide["version_note"])


if __name__ == "__main__":
    unittest.main()
