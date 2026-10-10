using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;

namespace KarnokAdvisor;

// Only the installed game's named state accessors are read. No commands are dispatched.
internal sealed class GameReader
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.Static;
    private Type? _dataType;
    private Type? _appStateType;
    private static readonly string[] CardAttributes = {
        "BuyPrice", "SellPrice", "DamageAmount", "ShieldApplyAmount", "HealAmount",
        "PoisonApplyAmount", "BurnApplyAmount", "RegenApplyAmount", "CooldownMax",
        "CritChance", "Multicast", "RageApplyAmount", "TempoCost", "Custom_0", "Custom_1"
    };

    internal JObject Capture()
    {
        var runtime = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "TheBazaarRuntime");
        _dataType ??= runtime?.GetType("TheBazaar.Data");
        _appStateType ??= runtime?.GetType("TheBazaar.AppState");
        if (_dataType == null) return new JObject { ["status"] = "waiting_for_game" };

        var appState = Get(_appStateType, "CurrentState");
        var stateType = appState?.GetType().Name ?? "Unknown";
        var run = Get(_dataType, "Run");
        var player = Get(run, "Player");
        var active = Get(_dataType, "HasActiveRun") as bool? ?? false;
        var combat = Get(_dataType, "IsInCombat") as bool? ?? false;
        var snapshot = new JObject {
            ["status"] = player != null && active ? "live" : "menu",
            ["state_type"] = stateType,
            ["in_combat"] = combat,
            ["is_replay"] = stateType.IndexOf("Replay", StringComparison.OrdinalIgnoreCase) >= 0,
            ["hero"] = Get(player, "Hero")?.ToString() ?? "",
            ["run_token"] = run == null ? "" : RuntimeHelpers.GetHashCode(run).ToString(CultureInfo.InvariantCulture),
            ["day"] = Number(Get(run, "Day")),
            ["hour"] = Number(Get(run, "Hour")),
            ["victories"] = Number(Get(run, "Victories")),
            ["losses"] = Number(Get(run, "Losses")),
            ["player_attributes"] = Attributes(player),
            ["board"] = Inventory(Get(player, "Hand")),
            ["stash"] = Inventory(Get(player, "Stash")),
            ["skills"] = Cards(Get(player, "Skills")),
            ["unlocked_slots"] = Unlocked(Get(player, "Hand")),
            ["options"] = new JArray()
        };
        var state = Get(_dataType, "CurrentState");
        snapshot["encounter_id"] = Get(state, "CurrentEncounterId")?.ToString() ?? "";
        snapshot["reroll_cost"] = Number(Get(state, "RerollCost"));
        snapshot["rerolls_remaining"] = Number(Get(state, "RerollsRemaining"));
        var rules = Get(state, "SelectionContextRules");
        snapshot["selection_rules"] = new JObject {
            ["is_free"] = Token(Get(rules, "SelectionIsFree")),
            ["can_exit"] = Token(Get(rules, "CanExit")),
            ["can_select_multiple"] = Token(Get(rules, "CanSelectMultiple"))
        };

        var selections = stateType == "ChoiceState" || stateType == "EncounterState" ||
                         stateType == "LevelUpState" || stateType == "PedestalState";
        if (!selections || combat || !active) return snapshot;
        var entities = Get(_dataType, "Entities") as IDictionary;
        if (entities == null)
            throw new InvalidOperationException("TheBazaar.Data.Entities is unavailable; choices cannot be read reliably.");
        var byId = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in entities)
            if (entry.Value != null) byId[Get(entry.Key, "Value")?.ToString() ?? entry.Key.ToString() ?? ""] = entry.Value;
        var options = (JArray)snapshot["options"]!;
        var selectionSet = Get(state, "SelectionSet") as IEnumerable;
        if (selectionSet == null) return snapshot;
        foreach (var entry in selectionSet)
        {
            var id = entry?.ToString() ?? "";
            if (byId.TryGetValue(id, out var card)) options.Add(Card(card, null));
            else if (Guid.TryParse(id, out var templateId))
                options.Add(new JObject { ["instance_id"] = id, ["template_id"] = templateId.ToString(),
                    ["attributes"] = new JObject(), ["is_template_only"] = true });
            else throw new InvalidOperationException("A displayed choice could not be resolved: " + id);
        }
        return snapshot;
    }

    private static JArray Cards(object? source)
    {
        var result = new JArray();
        if (source is IEnumerable items)
            foreach (var item in items) if (item != null) result.Add(Card(item, null));
        return result;
    }

    private static JArray Inventory(object? inventory)
    {
        var result = new JArray();
        var container = Get(inventory, "Container");
        if (container == null) return result;
        var method = container.GetType().GetMethod("GetCardsAndSockets", Flags, null, Type.EmptyTypes, null)
                     ?? throw new MissingMethodException(container.GetType().FullName, "GetCardsAndSockets");
        if (method.Invoke(container, null) is IEnumerable entries)
            foreach (var entry in entries)
            {
                var item = Get(entry, "Item1");
                if (item != null) result.Add(Card(item, Number(Get(entry, "Item2"))));
            }
        return result;
    }

    private static JArray? Unlocked(object? inventory)
    {
        var container = Get(inventory, "Container");
        if (container == null) return null;
        var method = container.GetType().GetMethod("GetUnlockedSockets", Flags, null, Type.EmptyTypes, null);
        if (!(method?.Invoke(container, null) is bool[] slots)) return null;
        return new JArray(slots.Select((unlocked, index) => unlocked ? (int?)index : null)
            .Where(index => index.HasValue).Select(index => index!.Value));
    }

    private static JObject Card(object card, int? socket)
    {
        var attrs = Attributes(card);
        var getValue = card.GetType().GetMethods(Flags)
            .FirstOrDefault(m => m.Name == "GetAttributeValue" && m.GetParameters().Length == 1 &&
                                 m.GetParameters()[0].ParameterType.IsEnum);
        if (getValue != null)
        {
            var enumType = getValue.GetParameters()[0].ParameterType;
            foreach (var key in CardAttributes)
                if (Enum.IsDefined(enumType, key))
                {
                    var value = getValue.Invoke(card, new[] { Enum.Parse(enumType, key) });
                    if (value != null) attrs[key] = Number(value);
                }
        }
        return new JObject {
            ["instance_id"] = Get(Get(card, "InstanceId"), "Value")?.ToString() ?? "",
            ["template_id"] = Get(card, "TemplateId")?.ToString() ?? "",
            ["type"] = Get(card, "Type")?.ToString() ?? "",
            ["tier"] = Get(card, "Tier")?.ToString() ?? "",
            ["size"] = Get(card, "Size")?.ToString() ?? "",
            ["enchantment"] = Get(card, "Enchantment")?.ToString() ?? "",
            ["socket"] = socket,
            ["attributes"] = attrs,
            ["tags"] = Strings(Get(card, "Tags")),
            ["hidden_tags"] = Strings(Get(card, "HiddenTags"))
        };
    }

    private static JObject Attributes(object? owner)
    {
        var result = new JObject();
        if (Get(owner, "Attributes") is IDictionary attrs)
            foreach (DictionaryEntry entry in attrs)
                result[entry.Key.ToString() ?? ""] = Number(entry.Value);
        return result;
    }

    private static JArray Strings(object? values)
    {
        var result = new JArray();
        if (values is IEnumerable items) foreach (var item in items) result.Add(item?.ToString() ?? "");
        return result;
    }

    private static int? Number(object? value) => value == null ? (int?)null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static JToken Token(object? value) => value == null ? JValue.CreateNull() : JToken.FromObject(value);

    private static object? Get(object? owner, string name)
    {
        if (owner == null) return null;
        var type = owner as Type ?? owner.GetType();
        var target = owner is Type ? null : owner;
        var property = type.GetProperty(name, Flags);
        if (property != null) return property.GetValue(target, null);
        var field = type.GetField(name, Flags);
        return field?.GetValue(target);
    }
}
