using KarnokAdvisor;
using Newtonsoft.Json.Linq;

var now = DateTimeOffset.UtcNow;
JObject Snapshot() => new JObject { ["snapshot_id"] = "current", ["status"] = "live", ["hero"] = "Karnok", ["day"] = 2,
    ["state_type"] = "ChoiceState", ["board"] = new JArray(), ["stash"] = new JArray(), ["unlocked_slots"] = new JArray(0, 1), ["captured_at_utc"] = now.ToString("O") };
JObject Guide() => new JObject { ["kind"] = ReferenceViewState.DocumentKind, ["status"] = "live", ["snapshot_id"] = "current", ["generated_at_utc"] = now.ToString("O"),
    ["reference"] = new JObject { ["live"] = true, ["stage"] = "early", ["matched_route_id"] = "ammo", ["routes"] = new JArray(
        new JObject { ["id"] = "friends", ["stages"] = new JArray(new JObject { ["key"] = "early", ["base_items"] = new JArray(new JObject { ["template_id"] = "spear", ["owned"] = "场上" }) }, new JObject { ["key"] = "late", ["base_items"] = new JArray() }) },
        new JObject { ["id"] = "ammo", ["stages"] = new JArray(new JObject { ["key"] = "early", ["base_items"] = new JArray() }) }) } };
var count = 0;
void Check(bool pass, string name) { if (!pass) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
Check(ReferenceViewState.IsLive(Guide(), Snapshot(), now), "Fresh matching guide is live");
foreach (var kind in new[] { "legacy-recommendation", "karnok-build-reference-v2", "" })
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
updated["routes"]![0]!["stages"]![0]!["base_items"]![0]!["owned"] = "仓库";
view.Synchronize(updated, true);
Check((string?)view.Card(view.Stage(view.Route(updated)!)!)?["owned"] == "仓库", "Detail reprojects current card by identity");
view.SelectStage("late");
Check(view.CardId == "", "Phase change closes stale card detail");
view.SelectStage("early"); view.SelectCard("spear");
updated["routes"]![0]!["stages"]![0]!["base_items"] = new JArray();
view.Synchronize(updated, true);
Check(view.CardId == "", "Catalog update closes detail when its identity disappears");
view.SelectCard("spear"); view.SelectRoute("ammo");
Check(view.CardId == "", "Route change closes stale card detail");
view.SelectStage("late"); view.Synchronize(updated, false);
Check(view.RouteId == "ammo" && view.StageKey == "late", "Disconnect preserves browse choices");
var components = new JObject { ["key"] = "early",
    ["base_items"] = new JArray(new JObject { ["template_id"] = "spear", ["owned"] = "场上" }, new JObject { ["template_id"] = "pants", ["owned"] = "仓库" }),
    ["optional_items"] = new JArray(new JObject { ["template_id"] = "wolf", ["owned"] = "场上", ["note"] = "current condition" }) };
Check(ReferenceViewState.OwnedBaseCount(components, true) == 1, "Base progress excludes optional cards and stash");
Check(ReferenceViewState.OwnedBaseCount(components, false) == null, "Disconnect masks base progress");
view.SelectCard("wolf");
Check((string?)view.Card(components)?["note"] == "current condition", "Optional detail resolves by canonical identity");
var changedComponents = (JObject)components.DeepClone();
changedComponents["optional_items"]![0]!["owned"] = "仓库";
changedComponents["optional_items"]![0]!["note"] = "updated condition";
Check((string?)view.Card(changedComponents)?["owned"] == "仓库" && (string?)view.Card(changedComponents)?["note"] == "updated condition", "Optional detail reprojects latest ownership and conditions");
view.SelectRoute("friends"); view.SelectStage("early"); view.SelectCard("wolf");
var optionalReference = (JObject)Guide()["reference"]!;
optionalReference["routes"]![0]!["stages"] = new JArray(components);
view.Synchronize(optionalReference, true);
Check(view.CardId == "wolf", "Optional selection survives a matching live refresh");
components["optional_items"] = new JArray();
view.Synchronize(optionalReference, true);
Check(view.CardId == "", "Removing optional identity closes detail");
var unmatched = (JObject)Guide()["reference"]!;
unmatched["matched_route_id"] = null;
var noCoreView = new ReferenceViewState();
noCoreView.SelectRoute("ammo"); noCoreView.MatchOwned(); noCoreView.Synchronize(unmatched, true);
Check(noCoreView.RouteId == "ammo", "No observed core preserves the current route instead of jumping to the first route");
Check(noCoreView.MatchOwnedLabel(unmatched, true).Contains("等待核心"), "No-match follow state explains why the route did not move");
Check(noCoreView.MatchOwnedLabel(unmatched, false).Contains("待同步"), "Unavailable ownership does not claim a match");
unmatched["matched_route_id"] = "not-a-route";
noCoreView.Synchronize(unmatched, true);
Check(noCoreView.RouteId == "ammo", "Unknown matched identity never overwrites a valid browse choice");
unmatched["matched_route_id"] = "friends";
noCoreView.Synchronize(unmatched, true);
Check(noCoreView.RouteId == "friends", "A real core arriving later resumes automatic matching");
noCoreView.SelectRoute("ammo");
Check(noCoreView.MatchOwnedLabel(unmatched, true) == "匹配持有", "Manual selection restores the match action label");
Check(ReferenceViewState.TagText(new JObject { ["tags"] = new JArray("武器", "伙伴") }) == "武器 · 伙伴", "Compound item types retain native order above the art");
Check(ReferenceViewState.TagText(new JObject { ["tags"] = new JArray() }) == "无标签", "A known untyped item has an explicit empty-type label");
Check(ReferenceViewState.TagText(new JObject()) == "标签未读取", "Missing type metadata is not presented as an untyped item");
Check(ReferenceViewState.TagText(new JObject { ["tags"] = new JArray("FutureType") }) == "FutureType", "Untranslated item types remain visible");
Console.WriteLine($"{count} reference view checks passed.");
