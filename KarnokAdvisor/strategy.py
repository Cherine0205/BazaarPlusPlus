"""Curated build references and observed ownership. No action scoring or network access."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path

from catalog import Catalog, TIER_ZH

STAGES = (("early", "前期 · D1–3"), ("mid", "中期 · D4–7"), ("late", "后期 · D8+"))
READY_STATES = {"ChoiceState", "EncounterState", "LevelUpState", "PedestalState", "CombatState"}


def stage_for(day):
    if isinstance(day, bool) or not isinstance(day, (int, float)) or day < 1:
        return None
    return STAGES[0 if day <= 3 else 1 if day <= 7 else 2][0]


def state_ready(state):
    return (state.get("status") == "live" and state.get("hero") == "Karnok"
            and not state.get("is_replay") and stage_for(state.get("day")) is not None
            and (state.get("state_type") in READY_STATES or state.get("in_combat") is True)
            and isinstance(state.get("board"), list) and isinstance(state.get("stash"), list)
            and isinstance(state.get("unlocked_slots"), list) and bool(state["unlocked_slots"]))


class StrategyBook:
    def __init__(self, catalog: Catalog, data=None):
        self.catalog = catalog
        self.data = data if data is not None else json.loads(Path(__file__).with_name("strategies.json").read_text(encoding="utf-8"))
        names = set()
        for route in self.data["routes"]:
            names.update(route["anchors"])
            for part in route["roles"] + route["stages"]:
                names.update(part["items"])
        self.templates = {}
        for name in sorted(names):
            found = [t for t in catalog.cards.values() if t.get("Type") == "Item"
                     and t.get("Localization", {}).get("Title", {}).get("Text") == name]
            self.templates[name] = found[0] if len(found) == 1 else None
        self.digest = hashlib.sha256(json.dumps(self.templates, sort_keys=True, ensure_ascii=False,
                                               separators=(",", ":")).encode("utf-8")).hexdigest()
        self.verified = self.digest == self.data["verified_catalog_digest"] and all(self.templates.values())
        self.sources = {s["id"]: s for s in self.data["sources"]}

    def find(self, cards, names):
        ids = {self.templates[n]["Id"] for n in names if self.templates.get(n)}
        return [c for c in cards or [] if c.get("template_id") in ids]

    def item(self, name, board=None, stash=None):
        template = self.templates.get(name)
        if not template:
            return {"name": name + "（模板未找到）", "known": False, "owned": "未核对", "span": None,
                    "template_id": "", "art_key": "", "effects": []}
        card = self.catalog.template_card(template["Id"])
        on_board, in_stash = self.find(board, [name]), self.find(stash, [name])
        observed = next(iter(on_board or in_stash), {})
        return {"name": card["name"], "template_id": template["Id"], "known": True, "span": card["span"],
                "starting_tier": TIER_ZH.get(card["tier"], card["tier"]), "art_key": template.get("ArtKey", ""),
                "effects": self.catalog.texts(card),
                "owned": "未核对" if board is None or stash is None else "场上" if on_board else "仓库" if in_stash else "未持有",
                "owned_tier": TIER_ZH.get(observed.get("tier"), observed.get("tier", ""))}

    def reference(self, state):
        ready = state_ready(state)
        board, stash = (state["board"], state["stash"]) if ready else (None, None)
        stage_key = stage_for(state.get("day")) if ready else None
        routes = []
        for route in self.data["routes"]:
            roles = [{"name": role["name"], "items": [self.item(n, board, stash) for n in role["items"]]}
                     for role in route["roles"]]
            stages = []
            for stage in route["stages"]:
                items = [{**self.item(n, board, stash), "core": n in route["anchors"],
                          "role": " / ".join(role["name"] for role in route["roles"] if n in role["items"]) or "过渡 / 辅助"}
                         for n in stage["items"]]
                slots = sum(i["span"] for i in items) if all(i["span"] for i in items) else None
                stages.append({**stage, "label": dict(STAGES)[stage["key"]], "items": items, "slots": slots})
            names = list(dict.fromkeys(n for part in route["roles"] + route["stages"] for n in part["items"]))
            core_count = len(self.find(board, route["anchors"]))
            routes.append({"id": route["id"], "name": route["name"], "summary": route["summary"],
                           "cover": self.item(route["anchors"][0]), "core_count": core_count,
                           "role_count": sum(any(i["owned"] == "场上" for i in role["items"]) for role in roles),
                           "owned_board": [self.item(n, board, stash)["name"] for n in names if self.find(board, [n])],
                           "owned_stash": [self.item(n, board, stash)["name"] for n in names if self.find(stash, [n])],
                           "missing_roles": [role["name"] for role in roles if not any(i["owned"] == "场上" for i in role["items"])] if ready else [],
                           "roles": roles, "stages": stages, "pivot": route["pivot"], "caution": route["caution"],
                           "skills": route["skills"], "sources": [self.sources[s] for s in route["sources"]]})
        # Match observed components; route order remains the curated order, never a strength ranking.
        matched = max(routes, key=lambda r: (r["core_count"], r["role_count"])) if routes else None
        tier_days = []
        for tier in ("Silver", "Gold", "Diamond"):
            days = [int(day) for day, weights in self.catalog.mode.get("ItemSkillSpawnTierPercantagesByDay", {}).items()
                    if str(day).isdigit() and isinstance(weights, dict) and weights.get(tier, 0) > 0]
            if days: tier_days.append(f"{TIER_ZH[tier]} D{min(days)}+")
        return {"reviewed_at": self.data["reviewed_at"], "reviewed_patch": self.data["reviewed_patch"],
                "verified": self.verified, "live": ready, "stage": stage_key,
                "matched_route_id": matched["id"] if matched and matched["core_count"] else None,
                "stage_note": self.data["stage_note"], "unlocked_slots": len(state["unlocked_slots"]) if ready else None,
                "tier_note": "本机基础品质表首次正权重：" + "、".join(tier_days) + "。这是品质表，非单卡掉率；指定奖励和商人规则可能例外。" if tier_days else "未读取基础品质表，不推断卡牌出现日。",
                "version_note": "相关卡牌模板与复核版本一致；攻略仅作路线参考" if self.verified else "本机模板与复核版不同；旧路线仅供浏览，请核对游戏卡面。",
                "routes": routes}
