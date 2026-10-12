using KarnokAdvisor;
using Newtonsoft.Json.Linq;

var now = DateTimeOffset.UtcNow;
JObject Snapshot() => new JObject { ["snapshot_id"] = "current", ["status"] = "live", ["hero"] = "Karnok", ["day"] = 2,
    ["state_type"] = "ChoiceState", ["board"] = new JArray(), ["stash"] = new JArray(), ["unlocked_slots"] = new JArray(0, 1), ["captured_at_utc"] = now.ToString("O") };
JObject Guide() => new JObject { ["kind"] = ReferenceViewState.DocumentKind, ["status"] = "live", ["snapshot_id"] = "current", ["generated_at_utc"] = now.ToString("O"),
    ["reference"] = new JObject { ["live"] = true, ["stage"] = "early", ["default_season"] = "s19", ["seasons"] = new JArray(new JObject { ["id"] = "s19" }, new JObject { ["id"] = "s18" }), ["matched_route_ids"] = new JObject { ["s19"] = new JArray("ammo"), ["s18"] = new JArray("friends") }, ["routes"] = new JArray(
        new JObject { ["id"] = "friends", ["seasons"] = new JArray("s19", "s18"), ["stages"] = new JArray(new JObject { ["key"] = "early", ["base_items"] = new JArray(new JObject { ["template_id"] = "spear", ["owned"] = "场上" }) }, new JObject { ["key"] = "late", ["base_items"] = new JArray() }) },
        new JObject { ["id"] = "ammo", ["seasons"] = new JArray("s19", "s18"), ["stages"] = new JArray(new JObject { ["key"] = "early", ["base_items"] = new JArray() }) }) } };
var count = 0;
void Check(bool pass, string name) { if (!pass) throw new Exception(name); count++; Console.WriteLine("PASS " + name); }
Check(ReferenceViewState.IsLive(Guide(), Snapshot(), now), "Fresh matching guide is live");
foreach (var kind in new[] { "legacy-recommendation", "karnok-build-reference-v2", "karnok-build-reference-v3", "" })
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
unmatched["matched_route_ids"]!["s19"] = new JArray();
var noCoreView = new ReferenceViewState();
noCoreView.SelectRoute("ammo"); noCoreView.MatchOwned(); noCoreView.Synchronize(unmatched, true);
Check(noCoreView.RouteId == "ammo", "No observed core preserves the current route instead of jumping to the first route");
Check(noCoreView.MatchOwnedLabel(unmatched, true).Contains("等待核心"), "No-match follow state explains why the route did not move");
Check(noCoreView.MatchOwnedLabel(unmatched, false).Contains("待同步"), "Unavailable ownership does not claim a match");
unmatched["matched_route_ids"]!["s19"] = new JArray("not-a-route");
noCoreView.Synchronize(unmatched, true);
Check(noCoreView.RouteId == "ammo", "Unknown matched identity never overwrites a valid browse choice");
unmatched["matched_route_ids"]!["s19"] = new JArray("friends");
noCoreView.Synchronize(unmatched, true);
Check(noCoreView.RouteId == "friends", "A real core arriving later resumes automatic matching");
noCoreView.SelectRoute("ammo");
Check(noCoreView.MatchOwnedLabel(unmatched, true) == "匹配持有", "Manual selection restores the match action label");
Check(ReferenceViewState.TagText(new JObject { ["tags"] = new JArray("武器", "伙伴") }) == "武器 · 伙伴", "Compound item types retain native order above the art");
Check(ReferenceViewState.TagText(new JObject { ["tags"] = new JArray() }) == "无标签", "A known untyped item has an explicit empty-type label");
Check(ReferenceViewState.TagText(new JObject()) == "标签未读取", "Missing type metadata is not presented as an untyped item");
Check(ReferenceViewState.TagText(new JObject { ["tags"] = new JArray("FutureType") }) == "FutureType", "Untranslated item types remain visible");
var positionedStage = new JObject { ["base_items"] = new JArray(new JObject { ["template_id"] = "spear", ["owned"] = "场上" }) };
var positionBlock = new JObject { ["kind"] = "item", ["template_id"] = "spear", ["start"] = 1, ["span"] = 2 };
var positionedCard = ReferenceViewState.PositionCard(positionedStage, positionBlock);
Check((string?)positionedCard?["template_id"] == "spear", "Position block resolves its component by identity");
positionedStage["base_items"]![0]!["owned"] = "仓库";
Check((string?)ReferenceViewState.PositionCard(positionedStage, positionBlock)?["owned"] == "仓库", "Position block does not cache previous ownership");
Check(ReferenceViewState.PositionCard(positionedStage, new JObject { ["kind"] = "gap" }) == null, "An intentional empty slot never resolves as a card");
Check(ReferenceViewState.PositionCard(positionedStage, new JObject { ["kind"] = "item", ["template_id"] = "missing" }) == null, "Removed position identity is not shown as another component");
var seasonal = (JObject)Guide()["reference"]!;
seasonal["routes"]![1]!["seasons"] = new JArray("s19");
var seasonView = new ReferenceViewState();
seasonView.Synchronize(seasonal, true);
Check(seasonView.SeasonId == "s19" && seasonView.Routes(seasonal).Count == 2, "Default season shows current season routes");
seasonView.SelectSeason("s18"); seasonView.Synchronize(seasonal, true);
Check(seasonView.RouteId == "friends" && seasonView.Routes(seasonal).Count == 1, "Season filter and ownership match use the same eligible routes");
seasonView.SelectRoute("friends"); seasonView.SelectStage("late"); seasonView.SelectCard("spear");
seasonView.SelectSeason("s19"); seasonView.Synchronize(seasonal, true);
Check(seasonView.RouteId == "friends" && seasonView.StageKey == "late" && seasonView.CardId == "", "Season switch retains manual route and phase but closes stale detail");
seasonal["matched_route_ids"]!["s19"] = new JArray("ammo", "friends");
seasonView.MatchOwned(); seasonView.Synchronize(seasonal, true);
Check(seasonView.RouteId == "friends", "Matching preserves current route when the best candidates tie");
seasonView.SelectSeason("s18"); seasonal["matched_route_ids"]!["s18"] = new JArray("ammo");
seasonView.Synchronize(seasonal, true);
Check(seasonView.RouteId == "friends" && seasonView.MatchOwnedLabel(seasonal, true).Contains("等待核心"), "Foreign-season core never promotes a route or match label");
Check(seasonView.Route(seasonal)?["id"]?.Value<string>() == "friends", "Detail lookup stays within the selected season");
var emptySeason = (JObject)seasonal.DeepClone(); emptySeason["routes"] = new JArray();
seasonView.Synchronize(emptySeason, false);
Check(seasonView.RouteId == "" && seasonView.Route(emptySeason) == null, "Empty season has no fabricated fallback route");
seasonView.SelectSeason("unknown"); seasonView.Synchronize(seasonal, false);
Check(seasonView.SeasonId == "s19", "Unknown season recovers to the declared default");
var grouped = (JObject)Guide()["reference"]!;
grouped["matched_route_ids"]!["s19"] = new JArray();
grouped["routes"]![0]!["sources"] = new JArray(new JObject { ["url"] = "https://mobalytics.gg/the-bazaar/guides/meta-builds" });
grouped["routes"]![1]!["sources"] = new JArray(new JObject { ["url"] = "https://www.bilibili.com/video/BV1aZty6JEcd/" });
var groupedView = new ReferenceViewState();
groupedView.Synchronize(grouped, false);
Check(groupedView.RouteId == "ammo" && !groupedView.OtherRoutesExpanded, "Default browse selects a Bilibili route and folds other references");
Check(groupedView.Routes(grouped).Select(route => (string?)route["id"]).SequenceEqual(new[] { "ammo", "friends" }), "Bilibili priority preserves every eligible route for matching");
var misleadingSource = new JObject { ["sources"] = new JArray(new JObject { ["title"] = "B站攻略", ["url"] = "https://bilibili.com.example.net/video" }) };
Check(!ReferenceViewState.IsBilibiliRoute(misleadingSource), "Group membership comes from the source host rather than its title");
groupedView.ToggleOtherRoutes(); groupedView.SelectRoute("friends"); groupedView.SelectStage("early"); groupedView.SelectCard("spear");
groupedView.Synchronize(grouped, false); groupedView.ToggleOtherRoutes(); groupedView.Synchronize(grouped, false);
Check(!groupedView.OtherRoutesExpanded && groupedView.RouteId == "friends" && groupedView.StageKey == "early" && groupedView.CardId == "spear",
    "Folding the selected group preserves route, phase and card detail");
Check(!groupedView.FollowOwned && !groupedView.FollowDay, "Folding a group does not re-enable automatic selection");
groupedView.SelectRoute("ammo"); groupedView.MatchOwned();
grouped["matched_route_ids"]!["s19"] = new JArray("friends"); groupedView.Synchronize(grouped, true);
Check(groupedView.RouteId == "friends" && groupedView.OtherRoutesExpanded, "A newly matched secondary route is revealed automatically");
groupedView.ToggleOtherRoutes(); groupedView.Synchronize(grouped, true);
Check(!groupedView.OtherRoutesExpanded && groupedView.RouteId == "friends", "Repeated ownership updates respect a user-collapsed group");
grouped["matched_route_ids"]!["s19"] = new JArray("ammo", "friends"); groupedView.Synchronize(grouped, true);
Check(groupedView.RouteId == "friends" && !groupedView.OtherRoutesExpanded, "Bilibili priority does not reset an existing tied selection");
var groupedSeason = (JObject)grouped.DeepClone(); groupedSeason["routes"]![1]!["seasons"] = new JArray("s18");
Check(groupedView.Routes(groupedSeason).Select(route => (string?)route["id"]).SequenceEqual(new[] { "friends" }), "Season filtering precedes source grouping");
var noPrimary = (JObject)Guide()["reference"]!;
var noPrimaryView = new ReferenceViewState(); noPrimaryView.Synchronize(noPrimary, false);
Check(noPrimaryView.RouteId == "friends" && noPrimaryView.OtherRoutesExpanded, "A catalog without Bilibili routes still exposes its fallback selection");
foreach (var countCase in new[] { 0, 1, 4, 5, 8, 9, 12, 13, 16 })
{
    Check(ReferenceViewState.RouteRows(countCase) == (countCase + 3) / 4, "Wide route grid height for " + countCase);
    Check(ReferenceViewState.RouteRows(countCase, 420) == (countCase + 2) / 3, "Narrow route grid height for " + countCase);
}
Check(ReferenceViewState.RoutesTop(504) == 90 && ReferenceViewState.RoutesTop(420) == 119, "Narrow controls reserve a separate season row");
var cachedSource = (JObject)Guide()["reference"]!;
cachedSource["verified"] = true;
cachedSource["routes"]![0]!["stages"]![0]!["positioning"] = new JObject { ["available"] = true, ["slots"] = 4, ["blocks"] = new JArray() };
cachedSource["routes"]![0]!["stages"]![0]!["base_items"]![0]!["art_key"] = "keep-art";
cachedSource["routes"]![0]!["stages"]![0]!["optional_items"] = new JArray(new JObject { ["group"] = "optional", ["result_position_slots"] = 5 });
var catalogFailure = ReferenceViewState.CachedReference(cachedSource, true);
var failureStage = catalogFailure["routes"]![0]!["stages"]![0]!;
Check((bool?)catalogFailure["live"] == false && (bool?)catalogFailure["verified"] == false, "Null-reference catalog failure cannot resurrect cached verification");
Check((bool?)failureStage["positioning"]!["available"] == false && failureStage["positioning"]!["slots"]!.Type == JTokenType.Null, "Cached catalog failure disables positioning geometry");
Check(failureStage["optional_items"]![0]!["result_position_slots"]!.Type == JTokenType.Null, "Cached catalog failure clears optional positioning conclusions");
Check((string?)failureStage["base_items"]![0]!["art_key"] == "keep-art" && (string?)failureStage["base_items"]![0]!["owned"] == "未核对", "Cached catalog failure retains static art and masks ownership");
Check((bool?)cachedSource["verified"] == true, "Fallback projection does not mutate last-good reference");
Check((bool?)ReferenceViewState.CachedReference(cachedSource, false)["routes"]![0]!["stages"]![0]!["positioning"]!["available"] == true, "A disconnected readable catalog retains reference positions");
Console.WriteLine($"{count} reference view checks passed.");
