"""Curated build components and observed ownership. No action scoring or network access."""
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


def component_names(route):
    names = set(route["anchors"])
    for role in route["roles"]:
        names.update(role["items"])
    for stage in route["stages"]:
        names.update(stage["base_items"])
        names.update(option["name"] for option in stage["optional_items"])
    return names


def validate_components(data):
    route_ids = [route["id"] for route in data["routes"]]
    if len(set(route_ids)) != len(route_ids):
        raise ValueError("Duplicate route identity")
    for route in data["routes"]:
        if [stage["key"] for stage in route["stages"]] != [key for key, _ in STAGES]:
            raise ValueError("Route must define early, mid and late stages")
        for stage in route["stages"]:
            base, cores, options = stage["base_items"], stage["core_items"], stage["optional_items"]
            names = base + [option["name"] for option in options]
            if not base or not cores or len(set(names)) != len(names) or len(set(cores)) != len(cores):
                raise ValueError("Stage components and cores must be nonempty and unique")
            if not set(cores) <= set(base):
                raise ValueError("Phase cores must belong to the base components")
            for option in options:
                replaces = option.get("replaces", [])
                if not option["use"] or not option["note"] or len(set(replaces)) != len(replaces) or not set(replaces) <= set(base):
                    raise ValueError("An option needs its use, condition and valid base replacement targets")


class StrategyBook:
    def __init__(self, catalog: Catalog, data=None):
        self.catalog = catalog
        self.data = data if data is not None else json.loads(Path(__file__).with_name("strategies.json").read_text(encoding="utf-8"))
        validate_components(self.data)
        names = set().union(*(component_names(route) for route in self.data["routes"]))
        self.templates = {}
        for name in sorted(names):
            found = [t for t in catalog.cards.values() if t.get("Type") == "Item"
                     and t.get("Localization", {}).get("Title", {}).get("Text") == name]
            self.templates[name] = found[0] if len(found) == 1 else None
        self.digest = hashlib.sha256(json.dumps(self.templates, sort_keys=True, ensure_ascii=False,
                                               separators=(",", ":")).encode("utf-8")).hexdigest()
        self.verified = self.digest == self.data["verified_catalog_digest"] and all(self.templates.values())
        self.sources = {source["id"]: source for source in self.data["sources"]}

    def find(self, cards, names):
        ids = {self.templates[name]["Id"] for name in names if self.templates.get(name)}
        return [card for card in cards or [] if card.get("template_id") in ids]

    def item(self, name, board=None, stash=None):
        template = self.templates.get(name)
        if not template:
            return {"name": name + "（模板未找到）", "known": False, "owned": "未核对", "span": None,
                    "template_id": "", "art_key": "", "effects": [], "tags": None}
        card = self.catalog.template_card(template["Id"])
        on_board, in_stash = self.find(board, [name]), self.find(stash, [name])
        observed = next(iter(on_board or in_stash), {})
        return {"name": card["name"], "template_id": template["Id"], "known": True, "span": card["span"],
                "starting_tier": TIER_ZH.get(card["tier"], card["tier"]), "art_key": template.get("ArtKey", ""),
                "effects": self.catalog.texts(card), "tags": self.catalog.tags(card),
                "owned": "未核对" if board is None or stash is None else "场上" if on_board else "仓库" if in_stash else "未持有",
                "owned_tier": TIER_ZH.get(observed.get("tier"), observed.get("tier", ""))}

    def stage(self, stage, route, board, stash):
        def enrich(name):
            return {**self.item(name, board, stash),
                    "role": " / ".join(role["name"] for role in route["roles"] if name in role["items"]) or "辅助"}
        base = [{**enrich(name), "group": "base", "core": name in stage["core_items"],
                 "core_label": ("过渡核心" if stage["key"] == "early" and name not in route["anchors"] else "核心")
                 if name in stage["core_items"] else ""} for name in stage["base_items"]]
        slots = sum(item["span"] for item in base) if all(item["span"] for item in base) else None
        by_name = dict(zip(stage["base_items"], base))
        options = []
        for option in stage["optional_items"]:
            item = enrich(option["name"])
            replaced = [by_name[name] for name in option.get("replaces", [])]
            total = slots + item["span"] - sum(card["span"] for card in replaced) if slots is not None and item["span"] else None
            options.append({**item, "group": "optional", "core": False,
                            "core_label": "核心替代" if any(card["core"] for card in replaced) else "",
                            "use": option["use"], "note": option["note"],
                            "replaces": [{"name": card["name"], "template_id": card["template_id"]} for card in replaced],
                            "result_slots": total})
        return {"key": stage["key"], "label": dict(STAGES)[stage["key"]], "plan": stage["plan"],
                "base_items": base, "optional_items": options, "base_slots": slots}

    def reference(self, state):
        ready = state_ready(state)
        board, stash = (state["board"], state["stash"]) if ready else (None, None)
        stage_key = stage_for(state.get("day")) if ready else None
        routes = []
        for route in self.data["routes"]:
            roles = [{"name": role["name"], "items": [self.item(name, board, stash) for name in role["items"]]}
                     for role in route["roles"]]
            stages = [self.stage(stage, route, board, stash) for stage in route["stages"]]
            core_count = len(self.find(board, route["anchors"]))
            routes.append({"id": route["id"], "name": route["name"],
                           "cover": self.item(route["anchors"][0]), "core_count": core_count,
                           "role_count": sum(any(item["owned"] == "场上" for item in role["items"]) for role in roles),
                           "stages": stages, "pivot": route["pivot"], "caution": route["caution"],
                           "skills": route["skills"], "sources": [self.sources[source] for source in route["sources"]]})
        # Route identity anchors are separate from phase cores such as a shared transition Spear.
        matched = max(routes, key=lambda route: (route["core_count"], route["role_count"])) if routes else None
        return {"catalog_digest": self.digest, "verified": self.verified, "live": ready, "stage": stage_key,
                "matched_route_id": matched["id"] if matched and matched["core_count"] else None,
                "unlocked_slots": len(state["unlocked_slots"]) if ready else None,
                "version_note": "" if self.verified else "卡牌模板已变化，请核对当前游戏卡面。",
                "routes": routes}
