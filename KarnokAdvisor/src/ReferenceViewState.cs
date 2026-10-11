using System;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace KarnokAdvisor;

// View choices are identities, never cached card objects or live ownership conclusions.
internal sealed class ReferenceViewState
{
    internal const string DocumentKind = "karnok-build-reference-v2";
    internal string RouteId { get; private set; } = "";
    internal string StageKey { get; private set; } = "early";
    internal string CardId { get; private set; } = "";
    internal bool FollowOwned { get; private set; } = true;
    internal bool FollowDay { get; private set; } = true;

    internal void Synchronize(JObject reference, bool live)
    {
        var routes = reference["routes"] as JArray ?? new JArray();
        var routeId = RouteId;
        if (FollowOwned && live) routeId = (string?)reference["matched_route_id"] ?? "";
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
    internal void MatchOwned() => FollowOwned = true;
    internal void MatchDay() => FollowDay = true;
    internal void SelectCard(string id) => CardId = id;
    internal void CloseCard() => CardId = "";
    internal JObject? Route(JObject reference) => (reference["routes"] as JArray)?.OfType<JObject>().FirstOrDefault(r => (string?)r["id"] == RouteId);
    internal JObject? Stage(JObject route) => (route["stages"] as JArray)?.OfType<JObject>().FirstOrDefault(s => (string?)s["key"] == StageKey);
    internal JObject? Card(JObject stage) => (stage["items"] as JArray)?.OfType<JObject>().FirstOrDefault(c => (string?)c["template_id"] == CardId && CardId.Length > 0);

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
