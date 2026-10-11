"""Conservative, explainable estimates. Scores are heuristics, never win probabilities."""
from __future__ import annotations

import copy
from dataclasses import dataclass, field
from typing import Any

from catalog import Catalog, SIZES, TIERS, TIER_ZH
from strategy import StrategyBook

CHANNELS = {"DamageAmount": (1.0, "伤害"), "ShieldApplyAmount": (0.8, "护盾"),
            "HealAmount": (0.55, "治疗"), "BurnApplyAmount": (2.0, "灼烧"),
            "PoisonApplyAmount": (5.0, "中毒"), "RegenApplyAmount": (3.0, "再生"),
            "RageApplyAmount": (1.5, "怒火")}
THEMES = {"Poison": "中毒", "Burn": "灼烧", "Rage": "怒火", "Shield": "护盾",
          "Heal": "治疗", "Regen": "再生", "Freeze": "冻结", "Haste": "加速"}
SELECTED_TRIGGERS = {"TTriggerOnCardSelected", "TTriggerOnEncounterSelected"}


class UnknownRule(Exception):
    pass


def kind(node: Any) -> str:
    return node.get("$type", "") if isinstance(node, dict) else ""


def tags(card: dict) -> set[str]:
    return set(card.get("tags", [])) | set(card.get("hidden_tags", []))


def compare(left, operator: str, right) -> bool:
    operations = {"Equal": lambda: left == right, "NotEqual": lambda: left != right,
                  "GreaterThan": lambda: left > right, "GreaterThanOrEqual": lambda: left >= right,
                  "LessThan": lambda: left < right, "LessThanOrEqual": lambda: left <= right}
    if operator not in operations:
        raise UnknownRule("未支持比较条件：" + operator)
    return operations[operator]()


def set_matches(actual: set, required: list, operator: str = "Any") -> bool:
    wanted = set(required)
    if operator == "Any": return bool(actual & wanted)
    if operator == "All": return wanted <= actual
    if operator == "None": return not actual & wanted
    raise UnknownRule("未支持集合条件：" + operator)


@dataclass
class Benefit:
    score: float = 0.0
    reasons: list[str] = field(default_factory=list)
    uncertainties: list[str] = field(default_factory=list)
    known: int = 0
    critical_unknown: bool = False
    gold_delta: float = 0.0
    required_gold: float = 0.0
    player_changes: dict[str, float] = field(default_factory=dict)

    def add(self, other: "Benefit") -> None:
        self.score += other.score
        self.reasons.extend(other.reasons)
        self.uncertainties.extend(other.uncertainties)
        self.known += other.known
        self.critical_unknown |= other.critical_unknown
        self.required_gold = max(self.required_gold, other.required_gold - self.gold_delta)
        self.gold_delta += other.gold_delta
        for attribute, delta in other.player_changes.items():
            self.player_changes[attribute] = self.player_changes.get(attribute, 0) + delta


class Engine:
    def __init__(self, catalog: Catalog, *, strategy_data=None):
        self.catalog = catalog
        self.strategies = StrategyBook(catalog, strategy_data)

    def prepare(self, snapshot: dict) -> dict:
        state = copy.deepcopy(snapshot)
        for section in ("board", "stash", "skills", "options"):
            state[section] = [self.catalog.enrich(c) for c in state.get(section, [])]
        return state

    def card_condition(self, condition: dict | None, card: dict) -> bool:
        if not condition: return True
        t = kind(condition)
        if t in ("TCardConditionalAnd", "ConstraintAnd"):
            return all(self.card_condition(c, card) for c in condition.get("Conditions", condition.get("Constraints", [])))
        if t in ("TCardConditionalOr", "ConstraintOr"):
            return any(self.card_condition(c, card) for c in condition.get("Conditions", condition.get("Constraints", [])))
        if t in ("TCardConditionalTag", "ConstraintTag"):
            result = set_matches(set(card.get("tags", [])), condition.get("Tags", []), condition.get("Operator", "Any"))
        elif t in ("TCardConditionalHiddenTag", "ConstraintHiddenTag"):
            result = set_matches(set(card.get("hidden_tags", [])), condition.get("Tags", condition.get("HiddenTags", [])), condition.get("Operator", "Any"))
        elif t in ("TCardConditionalTier", "ConstraintTier"):
            result = card.get("tier") in condition.get("Tiers", [])
        elif t in ("TCardConditionalSize", "ConstraintSize"):
            result = card.get("size") in condition.get("Sizes", [])
        elif t in ("ConstraintCardType", "TCardConditionalType"):
            result = card.get("type") in condition.get("Types", [])
        elif t == "TCardConditionalId":
            result = card.get("template_id") == condition.get("Id")
        elif t in ("ConstraintHero", "ConstraintIsOnlyHero"):
            heroes = set(self.catalog.cards.get(card.get("template_id"), {}).get("Heroes", []))
            result = heroes == set(condition.get("Heroes", [])) if t == "ConstraintIsOnlyHero" else bool(heroes & set(condition.get("Heroes", [])))
        else:
            raise UnknownRule("未解析卡牌条件：" + (t or "unknown"))
        return not result if condition.get("IsNot") else result

    def targets(self, target: dict | None, state: dict, source: dict) -> list[dict]:
        if not target: raise UnknownRule("缺少效果目标")
        t = kind(target)
        if t == "TTargetCardSelf": return [source]
        if t not in ("TTargetCardSection", "TTargetCardXMost"):
            raise UnknownRule("未解析目标：" + t)
        section = target.get("TargetSection", "")
        sections = {"SelfHand": ("board",), "SelfStash": ("stash",), "SelfHandAndStash": ("board", "stash"),
                    "AbsolutePlayerHand": ("board",), "AbsolutePlayerStash": ("stash",),
                    "AbsolutePlayerHandAndStash": ("board", "stash"), "SelfBoard": ("board",)}
        if section not in sections: raise UnknownRule("未解析目标区域：" + section)
        cards = [c for s in sections[section] for c in state.get(s, [])]
        if target.get("ExcludeSelf"):
            cards = [c for c in cards if c.get("instance_id") != source.get("instance_id") or not source.get("instance_id")]
        cards = [c for c in cards if self.card_condition(target.get("Conditions"), c)]
        cards.sort(key=lambda c: c.get("socket") if c.get("socket") is not None else 100)
        if t == "TTargetCardXMost":
            mode = target.get("TargetMode")
            if mode == "LeftMostCard": cards = cards[:1]
            elif mode == "RightMostCard": cards = cards[-1:]
            else: raise UnknownRule("未解析目标排序：" + str(mode))
        return cards

    def value(self, node: dict | None, state: dict, source: dict) -> float:
        if node is None: raise UnknownRule("缺少数值规则")
        t = kind(node)
        if t == "TFixedValue": return float(node["Value"])
        if t == "TReferenceValueCardAttribute":
            target = node.get("Target", {})
            if kind(target) != "TTargetCardSelf": raise UnknownRule("动态卡牌数值的目标尚未解析")
            attribute = node.get("AttributeType")
            if attribute not in source.get("attributes", {}): raise UnknownRule("实际属性未读取：" + str(attribute))
            result = float(source["attributes"][attribute])
        elif t in ("TReferenceValuePlayerAttribute", "TReferenceValuePlayerAttributeUnscaled"):
            if node.get("Target", {}).get("TargetMode") not in ("Player", "Self"):
                raise UnknownRule("数值引用了其他玩家")
            attribute = node.get("AttributeType")
            if attribute not in state.get("player_attributes", {}): raise UnknownRule("玩家属性未读取：" + str(attribute))
            result = float(state["player_attributes"][attribute])
        elif t == "TReferenceValueCardCount":
            result = float(len(self.targets(node.get("Target"), state, source)))
        elif t == "TReferenceValueCardAttributeAggregate":
            cards = self.targets(node.get("Target"), state, source)
            attribute = node.get("AttributeType")
            if any(attribute not in c.get("attributes", {}) for c in cards): raise UnknownRule("聚合所需实际属性缺失")
            result = float(sum(c["attributes"][attribute] for c in cards))
        elif t == "TRangeValue":
            raise UnknownRule(f"随机数值范围 {node.get('MinValue')}–{node.get('MaxValue')}，未确认分布")
        else: raise UnknownRule("未解析数值规则：" + t)
        modifier = node.get("Modifier")
        if modifier:
            value = self.value(modifier.get("Value"), state, source)
            operation = modifier.get("ModifyMode")
            if operation == "Multiply": result *= value
            elif operation == "Add": result += value
            elif operation == "Subtract": result -= value
            elif operation == "Divide" and value: result /= value
            else: raise UnknownRule("未解析数值修正：" + str(operation))
            if modifier.get("ShouldRound"): result = round(result)
        return result

    def prerequisite(self, node: dict | None, state: dict, source: dict) -> bool:
        if not node: return True
        t = kind(node)
        if t == "TPrerequisiteRun": return self.prerequisite(node.get("Conditions"), state, source)
        if t == "TRunConditionalCurrentDay": return compare(state["day"], node["ComparisonOperator"], node["CurrentDay"])
        if t == "TRunConditionalCurrentHour": return compare(state["hour"], node["ComparisonOperator"], node["CurrentHour"])
        if t == "TRunConditionalPlayerHero": return set_matches({state.get("hero")}, node.get("Heroes", []), node.get("Operator", "Any"))
        if t in ("TRunConditionalAnd", "TPrerequisiteAnd"):
            return all(self.prerequisite(c, state, source) for c in node.get("Conditions", node.get("Prerequisites", [])))
        if t in ("TRunConditionalOr", "TPrerequisiteOr"):
            return any(self.prerequisite(c, state, source) for c in node.get("Conditions", node.get("Prerequisites", [])))
        if t == "TPrerequisiteCardCount":
            return compare(len(self.targets(node.get("Subject"), state, source)), node.get("ComparisonOperator", "GreaterThanOrEqual"), self.value(node.get("Value"), state, source))
        raise UnknownRule("未解析前置条件：" + t)

    def gold_value(self, state: dict) -> float:
        return 1.0 if state.get("day", 1) <= 5 else 0.65

    def power(self, card: dict, state: dict) -> float:
        attrs = card.get("attributes", {})
        cooldown = attrs.get("CooldownMax")
        seconds = max(0.5, cooldown / 1000) if isinstance(cooldown, (int, float)) and cooldown > 0 else 6.0
        direct = sum(max(0, attrs.get(key, 0)) * weight for key, (weight, _) in CHANNELS.items())
        direct *= 1 + max(0, attrs.get("CritChance", 0)) / 100
        direct *= max(1, attrs.get("Multicast", 1))
        tier = TIERS.index(card["tier"]) + 1 if card.get("tier") in TIERS else 1
        # A small floor keeps passive cards visible without inventing their unparsed effects.
        score = 1.5 * tier + direct / seconds
        for key, weight in (("FreezeAmount", 0.002), ("SlowAmount", 0.001), ("HasteAmount", 0.001)):
            score += max(0, attrs.get(key, 0)) * weight / seconds
        existing = set().union(*(tags(c) for c in state.get("board", []) + state.get("skills", [])))
        overlaps = [theme for theme in THEMES if theme in tags(card) and (theme in existing or theme + "Reference" in existing)]
        return score * (1 + min(0.3, len(overlaps) * 0.1))

    def action(self, action: dict, state: dict, source: dict, depth: int) -> Benefit:
        benefit = Benefit()
        if not action: return benefit
        t = kind(action)
        try:
            if action.get("Cost"):
                raise UnknownRule("该动作含独立消耗条件，需游戏界面确认")
            if t == "TActionAnd":
                working = copy.deepcopy(state)
                for child in action.get("Actions", []):
                    change = self.action(child, working, source, depth)
                    benefit.add(change)
                    for attribute, delta in change.player_changes.items():
                        attrs = working.setdefault("player_attributes", {})
                        attrs[attribute] = attrs.get(attribute, 0) + delta
            elif t == "TActionPlayerModifyAttribute":
                target = action.get("Target", {})
                if target.get("TargetMode") not in ("Self", "Player"):
                    raise UnknownRule("未解析玩家属性效果目标")
                value = self.value(action.get("Value"), state, source)
                attribute = action.get("AttributeType", "")
                operation = action.get("Operation")
                current = state.get("player_attributes", {}).get(attribute)
                if operation == "Add": delta = value
                elif operation == "Multiply" and current is not None: delta = current * (value - 1)
                elif operation == "Set" and current is not None: delta = value - current
                else: raise UnknownRule("未解析属性运算：" + str(operation))
                weights = {"Gold": self.gold_value(state), "Income": self.gold_value(state) * max(1, min(5, 11-state.get("day", 1))),
                           "Experience": 2.3, "HealthMax": 0.015, "HealthRegen": 1.0, "Prestige": 12.0,
                           "EnragedDurationMax": 0.004, "RageMax": -0.2}
                names = {"Gold": "金币", "Income": "每日收入", "Experience": "经验", "HealthMax": "生命上限",
                         "HealthRegen": "再生", "Prestige": "声望", "EnragedDurationMax": "暴怒持续时间（毫秒）", "RageMax": "暴怒阈值"}
                if attribute not in weights: raise UnknownRule("未评估属性：" + attribute)
                if action.get("Duration"): raise UnknownRule("属性变化有持续时间，不能作为永久收益计入")
                benefit.score += delta * weights[attribute]
                benefit.reasons.append(f"{names[attribute]}变化 {delta:+g}（按游戏规则计算）")
                if attribute == "HealthMax":
                    adjustment = self.strategies.health_adjustment(state, delta)
                    if adjustment:
                        benefit.score += adjustment
                        benefit.reasons.append(f"场上已有生命关联武器，生命路线估算调整 {adjustment:+.1f}（非胜率）")
                benefit.known += 1
                benefit.player_changes[attribute] = delta
                if attribute == "Gold":
                    benefit.gold_delta += delta
                    benefit.required_gold = max(0, -delta)
            elif t in ("TActionGameDealCards", "TActionGameSpawnCards"):
                benefit.add(self.spawn(action.get("SpawnContext", {}), action.get("SelectionContextRules", {"SelectionIsFree": True}), state, source, depth + 1))
            elif t == "TActionCardModifyAttribute":
                affected = self.targets(action.get("Target"), state, source)
                count = len(affected)
                if action.get("TargetCount"):
                    count = min(count, max(0, int(self.value(action["TargetCount"], state, source))))
                value = self.value(action.get("Value"), state, source)
                attribute = action.get("AttributeType")
                if action.get("Operation") != "Add": raise UnknownRule("卡牌属性效果不是简单加法")
                if action.get("Duration"): raise UnknownRule("卡牌效果有持续时间，暂未量化")
                if attribute not in CHANNELS: raise UnknownRule("未量化卡牌属性：" + str(attribute))
                benefit.score += value * CHANNELS[attribute][0] * count / 6
                benefit.reasons.append(f"{count} 件符合条件的物品各获得 {value:+g} {CHANNELS[attribute][1]}")
                benefit.known += 1
            elif t == "TActionCardUpgrade":
                benefit.add(self.upgrade(action, state, source))
            else:
                raise UnknownRule("暂未量化效果：" + (t or "unknown"))
        except UnknownRule as error:
            benefit.uncertainties.append(str(error))
            if ("Destroy" in t or "Transform" in t or "消耗" in str(error)
                    or t == "TActionCardUpgrade" and not action.get("InheritEnchantment", True)):
                benefit.critical_unknown = True
        return benefit

    def upgrade(self, action: dict, state: dict, source: dict) -> Benefit:
        affected = self.targets(action.get("Target"), state, source)
        if action.get("TargetCount"):
            count = max(0, int(self.value(action["TargetCount"], state, source)))
            if 0 < count < len(affected):
                raise UnknownRule("升级只影响部分目标，实际目标未确定，暂停量化")
            if count == 0: affected = []
        destination = action.get("UpgradeToTier")
        if destination is not None and destination not in TIERS:
            raise UnknownRule("升级目标品质未解析")
        benefit, replacements = Benefit(known=1), {}
        for card in affected:
            tier = card.get("tier")
            if tier not in TIERS: raise UnknownRule("升级前品质未读取")
            index = TIERS.index(tier)
            next_tier = destination or TIERS[min(index + 1, len(TIERS) - 1)]
            if TIERS.index(next_tier) <= index: continue
            template = self.catalog.cards.get(card["template_id"], {})
            tiers = template.get("Tiers", {})
            if tier not in tiers or next_tier not in tiers:
                raise UnknownRule("本地模板缺少升级品质，暂停量化")
            if card.get("enchantment") not in (None, "", "None") and not action.get("InheritEnchantment", True):
                raise UnknownRule("升级不继承附魔，暂未量化附魔损失")
            previous = tiers[tier].get("Attributes", {})
            following = tiers[next_tier].get("Attributes", {})
            upgraded = copy.deepcopy(card)
            upgraded["tier"] = next_tier
            upgraded["attributes"] = {key: card["attributes"].get(key, previous.get(key, 0))
                                      + following.get(key, 0) - previous.get(key, 0)
                                      for key in card["attributes"].keys() | previous.keys() | following.keys()}
            benefit.score += self.power(upgraded, state) - self.power(card, state)
            benefit.reasons.append(f"升级 {card['name']} 至{TIER_ZH[next_tier]}；按模板差值估算，保留已读强化偏移")
            replacements[id(card)] = upgraded
        if replacements:
            trial = {**state, "board": [replacements.get(id(c), c) for c in state["board"]]}
            delta, reasons = self.strategies.change(state, trial)
            benefit.score += delta
            benefit.reasons.extend(reasons)
            benefit.uncertainties.append("升级后的被动、附魔和触发速度未完整模拟，模板差值不是实战收益保证")
        else:
            benefit.reasons.append("当前没有能提升至指定品质的目标")
        return benefit

    def spawn(self, context: dict, rules: dict, state: dict, source: dict, depth: int) -> Benefit:
        benefit = Benefit()
        if depth > 3:
            return Benefit(uncertainties=["奖励后续分支超过第一版的分析深度"])
        branches = []
        for group in context.get("Groups") or []:
            try:
                if not all(self.prerequisite(p, state, source) for p in group.get("Prerequisites") or []): continue
                ids = []
                queries = []
                for filter_ in group.get("Filters") or []:
                    if kind(filter_) == "TSpawnFilterIdList": ids.extend(filter_.get("Ids", []))
                    elif kind(filter_) == "TSpawnFilterQuery": queries.append(filter_.get("Constraints"))
                    elif kind(filter_) == "TSpawnFilterUpgrade":
                        benefit.uncertainties.append("奖励含已有卡牌升级概率，尚未展开")
                    else: raise UnknownRule("未解析奖励过滤：" + kind(filter_))
                behaviors = (context.get("Behaviors") or []) + (group.get("Behaviors") or [])
                tier_rules = [b for b in behaviors if kind(b) == "TSpawnBehaviorTier" and not b.get("IsNot")]
                tier = tier_rules[-1].get("Tiers", [None])[0] if tier_rules else None
                if queries:
                    candidates = []
                    ignore_hero = any(kind(b) == "TSpawnBehaviorIgnoreHero" and b.get("IgnoreHero") for b in behaviors)
                    for template in self.catalog.cards.values():
                        if template.get("Type") not in ("Item", "Skill") or template.get("SpawningEligibility") != "Always": continue
                        if not ignore_hero and not ({state.get("hero"), "Common"} & set(template.get("Heroes", []))): continue
                        c = self.catalog.template_card(template["Id"], tier)
                        if tier and tier not in template.get("Tiers", {}): continue
                        if all(self.card_condition(q, c) for q in queries): candidates.append(c)
                    if ids: candidates = [c for c in candidates if c["template_id"] in ids]
                    if candidates:
                        scores = sorted(self.power(c, state) for c in candidates)
                        estimate = scores[len(scores)//2] * 0.35
                        branches.append(Benefit(score=estimate, known=1,
                            reasons=[f"可获得符合条件的卡牌；本地模板筛出 {len(candidates)} 个候选"],
                            uncertainties=["随机卡牌按候选中位强度估算，未验证实际抽取分布和动态排除项"]))
                elif ids:
                    values = [self.template_benefit(self.catalog.template_card(i, tier), state, depth) for i in dict.fromkeys(ids) if i in self.catalog.cards]
                    if values:
                        chosen = max(values, key=lambda b: b.score)
                        if len(values) > 1:
                            chosen = Benefit(score=sum(v.score for v in values)/len(values), known=1,
                                reasons=[f"奖励引用 {len(values)} 个指定候选"], uncertainties=["后续展示数量、候选抽样和选择结果尚未确定"])
                        branches.append(chosen)
                else: benefit.uncertainties.append("奖励候选尚未解析")
            except UnknownRule as error: benefit.uncertainties.append(str(error))
        if branches:
            if context.get("SelectionMethod") == "Random" or not rules.get("CanSelectMultiple", False):
                benefit.add(max(branches, key=lambda b: b.score))
                if len(branches) > 1: benefit.uncertainties.append("后续选项尚未实际展示，收益为候选估算")
            else:
                for branch in branches: benefit.add(branch)
        if rules.get("SelectionIsFree") is False:
            benefit.score = max(0.0, benefit.score) * 0.15
            benefit.reasons = ["该选项提供购买机会；商品未当作免费奖励计入"]
            benefit.uncertainties.append("进入后需按实际商品和价格重新评估")
        return benefit

    def template_benefit(self, source: dict, state: dict, depth: int = 0) -> Benefit:
        if not source.get("template_known"):
            return Benefit(uncertainties=["本地数据库没有该选项的模板"], critical_unknown=True)
        if source["type"] in ("Item", "Skill"):
            return Benefit(score=self.power(source, state), reasons=[f"{TIER_ZH.get(source['tier'], source['tier'])}级{source['name']}；强度为启发式估算"], known=1)
        template = self.catalog.cards[source["template_id"]]
        benefit = Benefit()
        for ability in template.get("Abilities", {}).values():
            if kind(ability.get("Trigger")) not in SELECTED_TRIGGERS: continue
            try:
                if not all(self.prerequisite(p, state, source) for p in ability.get("Prerequisites") or []): continue
                benefit.add(self.action(ability.get("Action", {}), state, source, depth))
            except UnknownRule as error: benefit.uncertainties.append(str(error))
        selection = template.get("SelectionContext")
        if selection:
            benefit.add(self.spawn(selection.get("SpawnContext", {}), selection.get("Rules", {}), state, source, depth + 1))
        xp = template.get("ExperienceAwardUponSelection", 0)
        if xp:
            benefit.score += xp * 2.3
            benefit.reasons.append(f"选择该事件获得 {xp} 经验")
            benefit.known += 1
        return benefit

    def shop_plan(self, option: dict, state: dict) -> dict:
        result = {"action_id": option.get("instance_id") or option["template_id"], "title": "购买 " + option["name"],
                  "score": -10000.0, "eligible": False, "reasons": [], "steps": [], "uncertainties": []}
        price = option.get("attributes", {}).get("BuyPrice")
        gold = state.get("player_attributes", {}).get("Gold")
        if price is None or gold is None:
            result["uncertainties"].append("实际价格或金币未读取，停止购买建议")
            return result
        if price < 0:
            result["uncertainties"].append("价格异常，停止购买建议")
            return result
        if any(c["template_id"] == option["template_id"] for c in state["board"] + state["stash"]):
            result["uncertainties"].append("同名物品可能触发合成；第一版未验证合成后的实际属性，暂不建议按普通换卡处理")
            return result
        span, unlocked = option.get("span"), state.get("unlocked_slots")
        if not span or unlocked is None:
            result["uncertainties"].append("物品体型或解锁格子未读取，无法校验摆放")
            return result
        board = state["board"]
        if any(c.get("socket") is None or not c.get("span") for c in board):
            result["uncertainties"].append("场上物品位置不完整，无法可靠计算换卡")
            return result
        baseline = sum(self.power(c, state) for c in board)
        baseline_blade = self.strategies.find(board, ["Lost Blade"])
        baseline_solo = sum(c["attributes"].get("CooldownMax", 0) > 0 for c in board) == 1 if baseline_blade else None
        best = None
        for start in range(max(unlocked, default=-1) + 1):
            slots = set(range(start, start + span))
            if not slots <= set(unlocked): continue
            removed = [c for c in board if slots & set(range(c["socket"], c["socket"] + c["span"]))]
            if any("SellPrice" not in c["attributes"] for c in removed): continue
            sales = sum(c["attributes"]["SellPrice"] for c in removed)
            if sales < 0 or gold + sales < price: continue
            retained = [c for c in board if c not in removed]
            trial = {**state, "board": retained + [{**option, "socket": start}]}
            # A sole-active Lost Blade changes cooldown when another active item is added.
            # The existing stat estimator cannot safely propagate that aura counterfactual.
            if self.strategies.verified and self.strategies.find(trial["board"], ["Lost Blade"]):
                trial_solo = sum(c["attributes"].get("CooldownMax", 0) > 0 for c in trial["board"]) == 1
                if (baseline_solo is None and not trial_solo) or (baseline_solo is not None and baseline_solo != trial_solo):
                    continue
            gain = sum(self.power(c, trial) for c in trial["board"]) - baseline
            synergy, synergy_reasons = self.strategies.change(state, trial)
            risk = 1.4 if state.get("player_attributes", {}).get("Prestige", 20) <= 5 else 1.0
            score = gain * risk + synergy - (price - sales) * self.gold_value(state) * 0.5
            plan = (score, gain, start, removed, sales, synergy, synergy_reasons)
            if best is None or (score, -len(removed), -start) > (best[0], -len(best[3]), -best[2]): best = plan
        if best is None:
            result["uncertainties"].append("金币或连续摆位未通过校验，或涉及尚未支持的失落之刃单主动条件")
            return result
        score, gain, start, removed, sales, synergy, synergy_reasons = best
        result.update(eligible=True, score=score)
        result["reasons"].append(f"实际购买价格 {price:g} 金币；操作后预计剩余 {gold+sales-price:g} 金币")
        result["reasons"].append(f"已校验第 {start+1}–{start+span} 格连续可用；阵容强度估算变化 {gain:+.1f}")
        if synergy_reasons:
            result["reasons"].append(f"攻略联动估算变化 {synergy:+.1f}；" + "；".join(synergy_reasons))
        if removed:
            result["reasons"].append("替换方案按先卖出腾位计算，请确认愿意放弃这些物品")
            result["steps"].extend(f"先卖出 {c['name']}（实际售值 {c['attributes']['SellPrice']:g} 金币）" for c in removed)
        result["steps"] += [f"购买 {option['name']}", f"放到第 {start+1}–{start+span} 格"]
        if gold < price: result["reasons"].append("当前金币不足，必须先执行上述卖出步骤")
        result["uncertainties"].append("强度分数不是胜率；被动触发链、摆位联动和战斗结果尚未完整模拟")
        return result

    def recommend(self, snapshot: dict) -> dict:
        state = self.prepare(snapshot)
        base = {"snapshot_id": snapshot.get("snapshot_id", ""), "summary": "", "model_status": "本地规则估算 · 模型接口未配置",
                "recommendations": [], "candidates": [], "notes": [], "strategy": self.strategies.reference(state)}
        if snapshot.get("status") != "live":
            base["summary"] = "等待进入对局" if snapshot.get("status") != "read_error" else "状态读取失败，已停止推荐"
            return base
        if snapshot.get("is_replay") or snapshot.get("in_combat"):
            base["summary"] = "战斗或回放中暂停选择建议"
            return base
        if snapshot.get("hero") != "Karnok":
            base["summary"] = "第一版仅支持卡诺克"
            return base
        if not state["options"]:
            base["summary"] = "等待事件、升级或商店选项"
            return base
        candidates = []
        for source in state["options"]:
            free = state.get("selection_rules", {}).get("is_free")
            if source["type"] == "Item" and free is False:
                candidate = self.shop_plan(source, state)
            else:
                benefit = self.template_benefit(source, state)
                candidate = {"action_id": source.get("instance_id") or source["template_id"], "title": "选择 " + source["name"],
                             "score": benefit.score, "eligible": not benefit.critical_unknown and benefit.known > 0,
                             "reasons": benefit.reasons, "steps": [], "uncertainties": benefit.uncertainties}
                if source["type"] in ("Item", "Skill") and free is not True:
                    price = source.get("attributes", {}).get("BuyPrice")
                    gold = state.get("player_attributes", {}).get("Gold")
                    if price is None or gold is None or price > gold:
                        candidate["eligible"] = False
                        candidate["uncertainties"].append("实际价格或支付能力未通过校验")
                    else:
                        candidate["score"] -= price * self.gold_value(state)
                        candidate["reasons"].append(f"实际价格 {price:g} 金币，购买后剩余 {gold-price:g}")
                if state.get("player_attributes", {}).get("Gold", 0) < benefit.required_gold:
                    candidate["eligible"] = False
                    candidate["uncertainties"].append("当前金币无法支付已解析的消耗")
                description = self.catalog.texts(source)
                if not candidate["reasons"] and description: candidate["reasons"].append(description[0])
                candidate["uncertainties"] = list(dict.fromkeys(candidate["uncertainties"]))
            requirements = self.catalog.cards.get(source["template_id"], {}).get("SelectionRequirements") or []
            if isinstance(requirements, dict): requirements = [requirements]
            try:
                if not all(self.prerequisite(p, state, source) for p in requirements):
                    candidate["eligible"] = False
                    candidate["uncertainties"].append("尚不满足选项的前置条件")
            except UnknownRule as error:
                candidate["eligible"] = False
                candidate["uncertainties"].append(str(error))
            candidates.append(candidate)
            hint = self.strategies.option_hint(source) if self.strategies.verified else ""
            if hint: candidate["reasons"].append(hint)
        if state.get("selection_rules", {}).get("can_exit"):
            candidates.append({"action_id": "__skip__", "title": "保留金币，离开当前选择", "score": 0.0, "eligible": True,
                               "reasons": ["游戏允许退出；保留当前阵容与金币，避免为收益不足的选项付费"], "steps": [], "uncertainties": []})
        candidates.sort(key=lambda c: (not c["eligible"], -c["score"], c["action_id"]))
        for candidate in candidates:
            candidate["score"] = round(candidate["score"], 3)
            candidate["reasons"] = list(dict.fromkeys(candidate["reasons"]))[:5]
            candidate["uncertainties"] = list(dict.fromkeys(candidate["uncertainties"]))[:4]
        base["candidates"] = candidates
        eligible = [c for c in candidates if c["eligible"]]
        base["recommendations"] = eligible[:3]
        base["summary"] = ("优先：" + eligible[0]["title"]) if eligible else "这些选项暂不足以可靠排序，请结合原始效果选择"
        if not eligible:
            base["recommendations"] = candidates[:3]
            base["notes"].append("当前只展示选项和未解析原因，未把它们标记为可执行推荐")
        base["notes"].append("分数用于本地排序，尚未通过实战胜率对照；所有操作由你在游戏中执行")
        return base
