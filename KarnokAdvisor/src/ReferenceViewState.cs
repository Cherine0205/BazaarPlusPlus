using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace KarnokAdvisor;

// View choices are identities, never cached card objects or live ownership conclusions.
internal sealed class ReferenceViewState
{
    internal const string DocumentKind = "karnok-build-reference-v4";
    internal string SeasonId { get; private set; } = "s19";
    internal string RouteId { get; private set; } = "";
    internal string StageKey { get; private set; } = "early";
    internal string CardId { get; private set; } = "";
    internal bool FollowOwned { get; private set; } = true;
    internal bool FollowDay { get; private set; } = true;

    internal void Synchronize(JObject reference, bool live)
    {
        var seasons = reference["seasons"] as JArray ?? new JArray();
        if (!seasons.Any(s => (string?)s["id"] == SeasonId)) SelectSeason((string?)reference["default_season"] ?? (string?)seasons.First?["id"] ?? "");
        var routes = Routes(reference);
        var routeId = RouteId;
        var match = OwnedMatch(reference);
        if (FollowOwned && live && match != null) routeId = (string)match["id"]!;
        if (!routes.Any(r => (string?)r["id"] == routeId)) routeId = (string?)routes.FirstOrDefault()?["id"] ?? "";
        var stage = FollowDay && live ? (string?)reference["stage"] ?? StageKey : StageKey;
        if (routeId != RouteId || stage != StageKey) CardId = "";
        RouteId = routeId;
        StageKey = stage;
        if (CardId.Length > 0)
        {
            var route = Route(reference);
            var selectedStage = route == null ? null : Stage(route);
            if (selectedStage == null || Card(selectedStage) == null) CardId = "";
        }
    }

    internal void SelectRoute(string id) { if (id != RouteId) CardId = ""; RouteId = id; FollowOwned = false; }
    internal void SelectStage(string key) { if (key != StageKey) CardId = ""; StageKey = key; FollowDay = false; }
    internal void SelectSeason(string id) { if (id != SeasonId) CardId = ""; SeasonId = id; }
    internal IReadOnlyList<JObject> Routes(JObject? reference) => (reference?["routes"] as JArray ?? new JArray()).OfType<JObject>()
        .Where(route => (route["seasons"] as JArray)?.Values<string>().Contains(SeasonId) == true).ToArray();
    internal static int RouteColumns(float width) => width >= 480 ? 4 : 3;
    internal static int RouteRows(int count, float width = 504) => (Math.Max(0, count) + RouteColumns(width) - 1) / RouteColumns(width);
    internal static float RoutesTop(float width) => width >= 480 ? 90 : 119;
    internal static JObject CachedReference(JObject reference, bool catalogError)
    {
        var cached = (JObject)reference.DeepClone();
        cached["live"] = false;
        if (!catalogError) return cached;
        cached["verified"] = false;
        cached["version_note"] = "卡牌资料暂不可读，站位与持有暂停核对。";
        cached["matched_route_ids"] = new JObject();
        foreach (var route in cached["routes"] as JArray ?? new JArray())
        {
            route["core_count"] = 0;
            route["role_count"] = 0;
            foreach (var stage in route["stages"] as JArray ?? new JArray())
            {
                stage["positioning"] = new JObject { ["available"] = false, ["slots"] = null, ["blocks"] = new JArray(),
                    ["notes"] = new JArray("站位资料暂不可读，请先核对本机卡面。") };
                foreach (var card in BaseCards((JObject)stage).Concat(OptionalCards((JObject)stage)))
                {
                    card["owned"] = "未核对";
                    card["owned_tier"] = "";
                    if ((string?)card["group"] != "optional") continue;
                    card["result_position_slots"] = null;
                    card["position_note"] = "站位资料暂不可读，请先核对本机卡面。";
                }
            }
        }
        return cached;
    }
    private JObject? OwnedMatch(JObject? reference)
    {
        var ids = (reference?["matched_route_ids"]?[SeasonId] as JArray)?.Values<string>().ToArray() ?? Array.Empty<string>();
        var matches = Routes(reference).Where(route => ids.Contains((string?)route["id"])).ToArray();
        return matches.FirstOrDefault(route => (string?)route["id"] == RouteId) ?? matches.FirstOrDefault();
    }

    internal string MatchOwnedLabel(JObject? reference, bool live) => !FollowOwned ? "匹配持有"
        : !live ? "◆ 待同步" : OwnedMatch(reference) != null ? "◆ 匹配持有" : "◆ 等待核心";

    internal static string TagText(JToken card)
    {
        if (!(card["tags"] is JArray tags)) return "标签未读取";
        var labels = tags.Values<string>().Where(tag => !string.IsNullOrEmpty(tag)).Distinct().ToArray();
        return labels.Length > 0 ? string.Join(" · ", labels) : "无标签";
    }

    internal void MatchOwned() => FollowOwned = true;
    internal void MatchDay() => FollowDay = true;
    internal void SelectCard(string id) => CardId = id;
    internal void CloseCard() => CardId = "";
    internal JObject? Route(JObject reference) => Routes(reference).FirstOrDefault(r => (string?)r["id"] == RouteId);
    internal JObject? Stage(JObject route) => (route["stages"] as JArray)?.OfType<JObject>().FirstOrDefault(s => (string?)s["key"] == StageKey);
    internal static JArray BaseCards(JObject stage) => stage["base_items"] as JArray ?? new JArray();
    internal static JArray OptionalCards(JObject stage) => stage["optional_items"] as JArray ?? new JArray();
    internal static JObject? PositionCard(JObject stage, JToken block) => (string?)block["kind"] == "item"
        ? BaseCards(stage).OfType<JObject>().FirstOrDefault(card => (string?)card["template_id"] == (string?)block["template_id"])
        : null;

    internal static int? OwnedBaseCount(JObject stage, bool live) => live ? BaseCards(stage).Count(c => (string?)c["owned"] == "场上") : (int?)null;
    internal JObject? Card(JObject stage) => BaseCards(stage).Concat(OptionalCards(stage)).OfType<JObject>()
        .FirstOrDefault(c => (string?)c["template_id"] == CardId && CardId.Length > 0);

    internal static bool IsLive(JObject? document, JObject? snapshot, DateTimeOffset now)
    {
        if (document == null || snapshot == null) return false;
        var reference = document?["reference"] as JObject;
        if ((string?)document?["kind"] != DocumentKind || (bool?)reference?["live"] != true
            || (string?)document?["status"] != "live" || (string?)snapshot?["status"] != "live"
            || (string?)snapshot?["hero"] != "Karnok" || (bool?)snapshot?["is_replay"] == true
            || !(snapshot?["board"] is JArray) || !(snapshot["stash"] is JArray)
            || !(snapshot["unlocked_slots"] is JArray slots) || slots.Count == 0) return false;
        var state = (string?)snapshot["state_type"];
        if (!((int?)snapshot["day"] is int day) || day < 1
            || (state != "ChoiceState" && state != "EncounterState" && state != "LevelUpState" && state != "PedestalState" && state != "CombatState"
                && (bool?)snapshot["in_combat"] != true)) return false;
        var id = (string?)snapshot["snapshot_id"];
        if (string.IsNullOrEmpty(id) || id != (string?)document["snapshot_id"]) return false;
        return Fresh(document["generated_at_utc"], now) && Fresh(snapshot["captured_at_utc"], now);
    }

    private static bool Fresh(JToken? value, DateTimeOffset now) => DateTimeOffset.TryParse((string?)value, out var at)
        && (now - at).TotalSeconds >= -5 && (now - at).TotalSeconds <= 4;
}
