"""Read the game's own cached database. All database connections are read only."""
from __future__ import annotations

from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import time

TIERS = ("Bronze", "Silver", "Gold", "Diamond", "Legendary")
TIER_ZH = dict(zip(TIERS, ("青铜", "白银", "黄金", "钻石", "传说")))
SIZES = {"Small": 1, "Medium": 2, "Large": 3}


def default_cache() -> Path:
    return Path(os.environ["USERPROFILE"]) / "AppData/LocalLow/Tempo Storm/The Bazaar/prod/cache"


class Catalog:
    def __init__(self, cache: Path | None = None, *, cards=None, translations=None, mode=None):
        self.cache = (cache or default_cache()).resolve()
        if cards is not None:
            self.cards = {c["Id"]: c for c in cards}
            self.translations = translations or {}
            self.mode = mode or {}
            return
        path = self.cache / "GameData.db"
        with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as connection:
            connection.execute("PRAGMA query_only=ON")
            self.cards = {c["Id"]: c for (data,) in connection.execute("SELECT Data FROM cards")
                          if (c := json.loads(data))}
            modes = [json.loads(data) for (data,) in connection.execute("SELECT Data FROM game_modes")]
            self.mode = modes[0] if modes else {}
        self.translations = {}
        translation = self.cache / "translations/zh-CN.bytes"
        if translation.exists():
            with closing(sqlite3.connect(translation.as_uri() + "?mode=ro", uri=True)) as connection:
                self.translations = dict(connection.execute("SELECT hash,text FROM translation"))

    def localize(self, text: dict | None) -> str:
        text = text or {}
        return self.translations.get(text.get("Key"), text.get("Text", ""))

    def name(self, template_id: str) -> str:
        card = self.cards.get(template_id, {})
        return self.localize(card.get("Localization", {}).get("Title")) or card.get("InternalName") or template_id

    def template_card(self, template_id: str, tier: str | None = None) -> dict:
        template = self.cards.get(template_id, {})
        return {"template_id": template_id, "tier": tier or template.get("StartingTier", "Bronze"),
                "name": self.name(template_id), "span": SIZES.get(template.get("Size"))}

    def tags(self, card: dict) -> list[str]:
        template = self.cards.get(card.get("template_id"), {})
        # Native tag translation keys are the MD5 of the English type name.
        return [self.localize({"Key": hashlib.md5(tag.encode("utf-8"), usedforsecurity=False).hexdigest(), "Text": tag})
                for tag in dict.fromkeys(template.get("Tags", []))]

    def texts(self, card: dict) -> list[str]:
        template = self.cards.get(card.get("template_id"), {})
        localization = template.get("Localization", {})
        result = [self.localize(localization.get("Description"))]
        ids = template.get("Tiers", {}).get(card.get("tier"), {}).get("TooltipIds")
        tooltips = localization.get("Tooltips", [])
        for index, tooltip in enumerate(tooltips):
            if ids is None or index in ids:
                result.append(self.localize(tooltip.get("Content")))
        # Keep unsupported values visibly unknown rather than silently deleting them.
        return [re.sub(r"\{(?:ability|aura)\.[^}]+\}", "［数值待确认］", s) for s in result if s]


def read_json(path: Path) -> dict:
    deadline = time.monotonic() + .2
    while True:
        try:
            return json.loads(path.read_text(encoding="utf-8-sig"))
        except (FileNotFoundError, PermissionError):
            # Mono may briefly remove the old path during its atomic file publication.
            if time.monotonic() >= deadline:
                raise
            time.sleep(.01)


def atomic_json(path: Path, value: dict) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    deadline = time.monotonic() + .5
    while True:
        try:
            os.replace(temporary, path)
            return
        except PermissionError:
            # Windows readers may briefly hold the destination without FILE_SHARE_DELETE.
            if time.monotonic() >= deadline:
                raise
            time.sleep(.01)
