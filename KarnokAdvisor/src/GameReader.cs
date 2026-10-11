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
            ["unlocked_slots"] = Unlocked(Get(player, "Hand"))
        };
        return snapshot;
    }

    private static JArray? Inventory(object? inventory)
    {
        var container = Get(inventory, "Container");
        if (container == null) return null;
        var method = container.GetType().GetMethod("GetCardsAndSockets", Flags, null, Type.EmptyTypes, null);
        if (!(method?.Invoke(container, null) is IEnumerable entries)) return null;
        var result = new JArray();
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
        return new JObject {
            ["instance_id"] = Get(Get(card, "InstanceId"), "Value")?.ToString() ?? "",
            ["template_id"] = Get(card, "TemplateId")?.ToString() ?? "",
            ["tier"] = Get(card, "Tier")?.ToString() ?? "", ["socket"] = socket
        };
    }

    private static JObject Attributes(object? owner)
    {
        var result = new JObject();
        if (Get(owner, "Attributes") is IDictionary attrs)
            foreach (DictionaryEntry entry in attrs)
                if (entry.Key.ToString() is string key && (key == "Gold" || key == "Level")) result[key] = Number(entry.Value);
        return result;
    }

    private static int? Number(object? value) => value == null ? (int?)null : Convert.ToInt32(value, CultureInfo.InvariantCulture);

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
