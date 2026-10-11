using KarnokAdvisor;
using Newtonsoft.Json.Linq;

var now = DateTimeOffset.UtcNow;
JObject Snapshot() => new JObject { ["snapshot_id"] = "current", ["status"] = "live", ["hero"] = "Karnok", ["day"] = 2,
    ["state_type"] = "ChoiceState", ["board"] = new JArray(), ["stash"] = new JArray(), ["unlocked_slots"] = new JArray(0, 1), ["captured_at_utc"] = now.ToString("O") };
JObject Guide() => new JObject { ["kind"] = ReferenceViewState.DocumentKind, ["status"] = "live", ["snapshot_id"] = "current", ["generated_at_utc"] = now.ToString("O"),
    ["reference"] = new JObject { ["live"] = true, ["stage"] = "early", ["matched_route_id"] = "ammo", ["routes"] = new JArray(
        new JObject { ["id"] = "friends", ["stages"] = new JArray(new JObject { ["key"] = "early", ["items"] = new JArray(new JObject { ["template_id"] = "spear", ["owned"] = "场上" }) }, new JObject { ["key"] = "late", ["items"] = new JArray() }) },
        new JObject { ["id"] = "ammo", ["stages"] = new JArray(new JObject { ["key"] = "early", ["items"] = new JArray() }) }) } };
var count = 0;
void Check(bool pass, string name) { if (!pass) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
Check(ReferenceViewState.IsLive(Guide(), Snapshot(), now), "Fresh matching guide is live");
foreach (var kind in new[] { "legacy-recommendation", "" })
{
    var doc = Guide(); doc["kind"] = kind;
    Check(!ReferenceViewState.IsLive(doc, Snapshot(), now), "Old document kind cannot supply ownership");
}
foreach (var field in new[] { "snapshot_id", "generated_at_utc" })
{
    var doc = Guide(); doc[field] = field == "snapshot_id" ? "previous-run" : now.AddSeconds(-6).ToString("O");
    Check(!ReferenceViewState.IsLive(doc, Snapshot(), now), "Reject stale response " + field);
}
foreach (var field in new[] { "captured_at_utc", "hero", "state_type", "stash" })
{
    var snap = Snapshot(); snap[field] = field switch { "captured_at_utc" => now.AddSeconds(-6).ToString("O"), "hero" => "Dooley", "state_type" => "Unknown", _ => null };
    Check(!ReferenceViewState.IsLive(Guide(), snap, now), "Reject unavailable snapshot " + field);
}
var replay = Snapshot(); replay["is_replay"] = true;
Check(!ReferenceViewState.IsLive(Guide(), replay, now), "Replay never claims current ownership");
var browsing = Guide(); browsing["reference"]!["live"] = false;
Check(!ReferenceViewState.IsLive(browsing, Snapshot(), now), "Freshness cannot promote backend browse mode");
var future = Snapshot(); future["captured_at_utc"] = now.AddSeconds(10).ToString("O");
Check(!ReferenceViewState.IsLive(Guide(), future, now), "Future timestamps cannot supply ownership");

var reference = (JObject)Guide()["reference"]!;
var view = new ReferenceViewState();
view.Synchronize(reference, true);
Check(view.RouteId == "ammo" && view.StageKey == "early", "Follow observed core and day");
view.SelectRoute("friends"); view.SelectStage("late");
reference["stage"] = "mid";
reference["routes"] = new JArray(((JArray)reference["routes"]!).Reverse());
view.Synchronize(reference, true);
Check(view.RouteId == "friends" && view.StageKey == "late", "Manual route and phase survive live updates");
view.MatchOwned(); view.MatchDay(); view.Synchronize(reference, true);
Check(view.RouteId == "ammo" && view.StageKey == "mid", "Explicit follow controls restore matching");
view.SelectRoute("friends"); view.SelectStage("early"); view.SelectCard("spear");
var updated = (JObject)Guide()["reference"]!;
updated["routes"]![0]!["stages"]![0]!["items"]![0]!["owned"] = "仓库";
view.Synchronize(updated, true);
Check((string?)view.Card(view.Stage(view.Route(updated)!)!)?["owned"] == "仓库", "Detail reprojects current card by identity");
view.SelectStage("late");
Check(view.CardId == "", "Phase change closes stale card detail");
view.SelectStage("early"); view.SelectCard("spear");
updated["routes"]![0]!["stages"]![0]!["items"] = new JArray();
view.Synchronize(updated, true);
Check(view.CardId == "", "Catalog update closes detail when its identity disappears");
view.SelectCard("spear"); view.SelectRoute("ammo");
Check(view.CardId == "", "Route change closes stale card detail");
view.SelectStage("late"); view.Synchronize(updated, false);
Check(view.RouteId == "ammo" && view.StageKey == "late", "Disconnect preserves browse choices");
Console.WriteLine($"{count} reference view checks passed.");
