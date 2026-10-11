using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace KarnokAdvisor;

// View choices are identities, never cached card objects or live ownership conclusions.
internal sealed class ReferenceViewState
{
    internal const string DocumentKind = "karnok-build-reference-v3";
    internal string RouteId { get; private set; } = "";
    internal string StageKey { get; private set; } = "early";
    internal string CardId { get; private set; } = "";
    internal bool FollowOwned { get; private set; } = true;
    internal bool FollowDay { get; private set; } = true;

    internal void Synchronize(JObject reference, bool live)
    {
        var routes = reference["routes"] as JArray ?? new JArray();
        var routeId = RouteId;
        if (FollowOwned && live && HasOwnedMatch(reference)) routeId = (string)reference["matched_route_id"]!;
        if (!routes.Any(r => (string?)r["id"] == routeId)) routeId = (string?)routes.First?["id"] ?? "";
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
    private static bool HasOwnedMatch(JObject? reference)
    {
        var id = (string?)reference?["matched_route_id"];
        return !string.IsNullOrEmpty(id) && (reference?["routes"] as JArray)?.Any(route => (string?)route["id"] == id) == true;
    }

    internal string MatchOwnedLabel(JObject? reference, bool live) => !FollowOwned ? "匹配持有"
        : !live ? "◆ 待同步" : HasOwnedMatch(reference) ? "◆ 匹配持有" : "◆ 等待核心";

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
    internal JObject? Route(JObject reference) => (reference["routes"] as JArray)?.OfType<JObject>().FirstOrDefault(r => (string?)r["id"] == RouteId);
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
