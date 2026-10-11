"""Curated build references, with small bonuses for verified, present interactions."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from catalog import Catalog, TIERS, TIER_ZH

STAGES = (("early", "前期 · D1–3"), ("mid", "中期 · D4–7"), ("late", "后期 · D8+"))
HEALTH_WEAPONS = ("Runic Claymore", "Wild Bear", "Lost Blade")
AMMO_WEAPONS = ("Shotgun", "Heavy Crossbow", "Bear Trap")
SELF_SLOW = ("Honey Jar", "Hunter's Boots", "Night Vision", "Fogshroom", "Worry Wart")
READY_STATES = {"ChoiceState", "EncounterState", "LevelUpState", "PedestalState", "CombatState"}


def stage_for(day):
    if not isinstance(day, (int, float)) or day < 1:
        return None
    return STAGES[0 if day <= 3 else 1 if day <= 7 else 2][0]


def state_ready(state):
    return (state.get("status") == "live" and state.get("hero") == "Karnok"
            and not state.get("is_replay") and stage_for(state.get("day")) is not None
            and (state.get("state_type") in READY_STATES or state.get("in_combat") is True)
            and bool(state.get("unlocked_slots")))


def adjacent(left, right):
    return (isinstance(left.get("socket"), int) and isinstance(right.get("socket"), int)
            and isinstance(left.get("span"), int) and isinstance(right.get("span"), int)
            and (left["socket"] + left["span"] == right["socket"]
                 or right["socket"] + right["span"] == left["socket"]))


def slowable(card):
    return card.get("enchantment", "").lower() != "radiant"


class StrategyBook:
    def __init__(self, catalog: Catalog, data=None):
        self.catalog = catalog
        self.data = data if data is not None else json.loads(Path(__file__).with_name("strategies.json").read_text(encoding="utf-8"))
        names = set(HEALTH_WEAPONS + AMMO_WEAPONS + SELF_SLOW)
        for route in self.data["routes"]:
            names.update(route["anchors"])
            for role in route["roles"]: names.update(role["items"])
            for stage in route["stages"]: names.update(stage["items"])
        self.templates = {}
        for name in sorted(names):
            found = [t for t in catalog.cards.values() if t.get("Type") == "Item"
                     and t.get("Localization", {}).get("Title", {}).get("Text") == name]
            self.templates[name] = found[0] if len(found) == 1 else None
        # Hash the actual templates, independently of a manifest that may update separately.
        self.digest = hashlib.sha256(json.dumps(self.templates, sort_keys=True, ensure_ascii=False,
                                               separators=(",", ":")).encode("utf-8")).hexdigest()
        self.verified = self.digest == self.data["verified_catalog_digest"] and all(self.templates.values())
        self.sources = {s["id"]: s for s in self.data["sources"]}

    def matches(self, card, name):
        template = self.templates.get(name)
        return bool(template and card.get("template_id") == template["Id"])

    def find(self, board, names):
        return [c for c in board if any(self.matches(c, name) for name in names)]

    def item(self, name, board=(), stash=()):
        template = self.templates.get(name)
        if not template:
            return {"name": name + "（模板未找到）", "known": False, "owned": "", "span": None, "art_key": ""}
        tier = template.get("StartingTier", "")
        card = self.catalog.template_card(template["Id"])
        return {"name": card["name"], "template_id": template["Id"], "known": True, "span": card["span"],
                "starting_tier": TIER_ZH.get(tier, tier), "art_key": template.get("ArtKey", ""),
                "owned": "场上" if self.find(board, [name]) else "仓库" if self.find(stash, [name]) else "未持有"}

    def reference(self, state):
        ready = state_ready(state)
        board, stash = (state.get("board", []), state.get("stash", [])) if ready else ([], [])
        stage_key = stage_for(state.get("day")) if ready else None
        routes = []
        for route in self.data["routes"]:
            anchors = self.find(board, route["anchors"])
            roles = [{"name": role["name"], "items": [self.item(n, board, stash) for n in role["items"]]} for role in route["roles"]]
            hits = sum(any(i["owned"] == "场上" for i in role["items"]) for role in roles)
            stages = []
            for stage in route["stages"]:
                items = [{**self.item(n, board, stash), "core": n in route["anchors"],
                          "role": " / ".join(role["name"] for role in route["roles"] if n in role["items"]) or "过渡 / 辅助"}
                         for n in stage["items"]]
                slots = sum(i["span"] for i in items) if all(i["span"] for i in items) else None
                stages.append({**stage, "label": dict(STAGES)[stage["key"]], "items": items, "slots": slots})
            names = list(dict.fromkeys(n for role in route["roles"] for n in role["items"]))
            missing = [role["name"] for role in roles if not any(i["owned"] == "场上" for i in role["items"])]
            routes.append({"id": route["id"], "name": route["name"], "summary": route["summary"],
                           "signal": bool(anchors), "fit": len(anchors) * 3 + hits if anchors else 0,
                           "status": "已有核心，检查联动" if anchors else "尚无辨识核心" if ready else "仅浏览参考",
                           "owned_board": [self.item(n, board, stash)["name"] for n in names if self.find(board, [n])],
                           "owned_stash": [self.item(n, board, stash)["name"] for n in names if self.find(stash, [n])],
                           "missing_roles": missing if ready else [], "roles": roles, "stages": stages,
                           "pivot": route["pivot"], "caution": route["caution"], "skills": route["skills"],
                           "sources": [self.sources[s] for s in route["sources"]]})
        routes.sort(key=lambda r: -r["fit"])
        tier_days = []
        table = self.catalog.mode.get("ItemSkillSpawnTierPercantagesByDay", {})
        for tier in ("Silver", "Gold", "Diamond"):
            days = [int(day) for day, weights in table.items() if str(day).isdigit()
                    and isinstance(weights, dict) and weights.get(tier, 0) > 0]
            if days: tier_days.append(f"{TIER_ZH[tier]} D{min(days)}+")
        return {"reviewed_at": self.data["reviewed_at"], "reviewed_patch": self.data["reviewed_patch"],
                "verified": self.verified, "live": ready, "stage": stage_key,
                "stage_label": dict(STAGES).get(stage_key, "等待实时阵容"),
                "stage_note": self.data["stage_note"], "unlocked_slots": len(state.get("unlocked_slots") or []) if ready else None,
                "tier_note": ("本机基础品质表首次正权重：" + "、".join(tier_days) + "。这是品质表，非单卡掉率；指定奖励和商人规则可能例外。") if tier_days else "未读取基础品质表，不推断卡牌出现日。",
                "version_note": "相关卡牌模板与复核版本一致；攻略仅作路线参考" if self.verified else "本机模板与复核版不同；已停用攻略评分调整，旧路线仅供浏览",
                "routes": routes}

    def links(self, state):
        if not self.verified or not state_ready(state): return []
        board = state.get("board", [])
        links = []

        def add(key, text, score, condition):
            if condition: links.append({"id": key, "text": text, "score": score})

        def pair(a, b, relation=None, predicate=None):
            return any((relation is None or relation(left, right)) and (predicate is None or predicate(left, right))
                       for left in self.find(board, a) for right in self.find(board, b) if left is not right)

        def has(*names): return bool(self.find(board, names))

        def active(card): return card.get("attributes", {}).get("CooldownMax", 0) > 0

        def usable_haste():
            if has("Hunting Hawk", "Messenger Sparrow"): return True
            if has("Beast Call", "Spear") and any("Friend" in c.get("tags", []) for c in board): return True
            return any(adjacent(a, b) and a["socket"] < b["socket"] and active(b)
                       for a in self.find(board, ["Flying Squirrel"]) for b in board if a is not b)

        def usable_heal(include_aurora=True):
            names = ["Dryad", "Stretch Pants"] + (["Aurora Vista"] if include_aurora else [])
            if any(active(c) for c in self.find(board, names)): return True
            return any(adjacent(a, b) and b["socket"] < a["socket"] and active(b)
                       for a in self.find(board, ["Waterskin"]) for b in board if a is not b)

        def self_slow_on(target):
            if not slowable(target): return False
            if "Weapon" in target.get("tags", []) and has("Chains"): return True
            return any(c is not target and adjacent(c, target) for c in self.find(board, SELF_SLOW))

        add("spear_friends", "伙伴可提高长矛暴击率，帮助触发其他物品加速", 2,
            has("Spear") and any("Friend" in c.get("tags", []) for c in board))
        add("squirrel", "飞鼠位于主力左侧，可加速该主力并使其飞行", 2.5,
            pair(["Flying Squirrel"], list(HEALTH_WEAPONS + AMMO_WEAPONS) + ["Spear", "Boa Constrictor", "Outlands Terror"],
                 lambda a, b: adjacent(a, b) and a["socket"] < b["socket"]))
        add("flying", "猎鹰提供飞行，信使麻雀可为其他飞行物品充能", 2,
            has("Hunting Hawk") and has("Messenger Sparrow"))
        add("terror_rage", "加速可触发羽翼符印怒火，帮助恐兽进入暴怒后的输出与吸血", 3,
            has("Outlands Terror") and has("Wing Sigil") and usable_haste())
        add("chains_boa", "锁链能减速己方武器，蚺蛇可从减速获得充能", 3,
            has("Chains") and has("Boa Constrictor") and any("Weapon" in c.get("tags", []) and slowable(c) for c in board))
        add("honey_boa", "蜂蜜罐相邻蚺蛇，可提供减速充能触发", 2.5,
            pair(["Honey Jar"], ["Boa Constrictor"], adjacent, lambda a, b: slowable(b)))
        add("tent_reload", "帐篷相邻弹药武器，且有可作用到该武器的自减速来源，可衔接装填", 4,
            pair(["Tent"], AMMO_WEAPONS, adjacent, lambda a, b: self_slow_on(b)))
        add("belt_reload", "工具腰带提供装填，帮助弹药输出继续作战（具体目标由游戏选择）", 2,
            has("Utility Belt") and has(*AMMO_WEAPONS))
        add("trapvine_slow", "蝙蝠可主动减速，为新版陷阱藤提供充能", 3,
            has("Trapvine") and has("Bat"))
        add("poison_growth", "黑曼巴在暴怒时可为忧虑疣蛙增加剧毒", 2,
            has("Black Mamba") and has("Worry Wart"))
        add("toxic_sigil", "陷阱藤提供剧毒，剧毒符印连接怒火与暴怒时的剧毒成长", 3,
            has("Trapvine") and has("Toxic Sigil"))
        add("health_support", "森林斗篷的生命支持可增强生命关联武器", 2.5,
            has(*HEALTH_WEAPONS) and has("Forest Cloak"))
        add("lake_weapon", "隐秘之湖邻接生命武器；半血条件满足时可进一步缩短主力冷却", 3,
            pair(["Hidden Lake"], HEALTH_WEAPONS, adjacent))
        add("claymore_crit", "猎人日志提供暴击支持，帮助符文阔剑触发生命成长", 2,
            has("Hunter's Journal") and has("Runic Claymore"))
        add("aurora_heal", "极光之景可把治疗转成护盾，已有治疗物品可衔接", 3,
            has("Aurora Vista") and usable_heal(include_aurora=False))
        add("aurora_crit", "狼面具邻接极光之景，可支持其暴击相关治疗效果", 2,
            pair(["Wolf Mask"], ["Aurora Vista"], adjacent))
        add("tinderbox", "树精或水囊的非武器触发可为火绒盒充能", 2.5,
            has("Tinderbox") and has("Dryad", "Waterskin"))
        add("burn_growth", "灼烧疤痕可承接灼烧物品的使用，并在暴怒时提供成长", 2.5,
            has("Burn Scar") and has("Tinderbox", "Signal Fire", "Firefly Lantern"))
        add("geyser_heal", "已有治疗可为新版间歇泉增加灼烧，实际释放还需暴怒", 2,
            has("Geyser") and usable_heal())
        factor = .5 if stage_for(state.get("day")) == "early" else 1.0
        return [{**link, "score": link["score"] * factor} for link in links]

    def value(self, state):
        return min(12.0, sum(link["score"] for link in self.links(state)))

    def change(self, before, after):
        old, new = self.links(before), self.links(after)
        old_ids, new_ids = {l["id"] for l in old}, {l["id"] for l in new}
        delta = min(12.0, sum(l["score"] for l in new)) - min(12.0, sum(l["score"] for l in old))
        reasons = ["新增联动：" + l["text"] for l in new if l["id"] not in old_ids]
        reasons += ["会拆掉联动：" + l["text"] for l in old if l["id"] not in new_ids]
        return delta, reasons

    def health_adjustment(self, state, delta):
        if not self.verified or not state_ready(state) or not self.find(state.get("board", []), HEALTH_WEAPONS):
            return 0.0
        return max(-6.0, min(6.0, delta * (.005 if stage_for(state.get("day")) == "early" else .01)))

    def option_hint(self, card):
        names = [r["name"] for r in self.data["routes"] if any(self.matches(card, n) for n in r["anchors"])]
        return "路线参考：可关注" + " / ".join(names) + "；需核对配套，持有单卡不代表成型" if names else ""
