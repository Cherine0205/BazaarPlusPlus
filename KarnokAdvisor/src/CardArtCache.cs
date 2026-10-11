using System;
using System.Collections.Generic;
using TheBazaar.Assets.Scripts.ScriptableObjectsScripts;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace KarnokAdvisor;

// Own only the Addressables references acquired here. Materials and textures belong to the game.
internal sealed class CardArtCache : IDisposable
{
    private const int Capacity = 64;
    private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    private readonly Queue<Entry> _pending = new Queue<Entry>();
    private readonly Action<string> _warn;
    private bool _disposed;

    internal CardArtCache(Action<string> warn) => _warn = warn;

    internal Texture? Get(string key, bool request, out bool unavailable)
    {
        unavailable = _disposed || !Guid.TryParseExact(key, "N", out _);
        if (unavailable) return null;
        if (!_entries.TryGetValue(key, out var entry))
        {
            if (!request) return null;
            if (_entries.Count >= Capacity) { unavailable = true; return null; }
            entry = new Entry(key);
            _entries.Add(key, entry);
            _pending.Enqueue(entry);
        }
        unavailable = entry.Finished && entry.Texture == null;
        return entry.Texture;
    }

    // Called on Unity's main thread before the snapshot timer's early return. No blocking awaits.
    internal void Pump()
    {
        if (_disposed) return;
        var loading = 0;
        foreach (var entry in _entries.Values)
        {
            if (!entry.HasHandle || entry.Finished) continue;
            try
            {
                if (entry.Handle.IsDone) Complete(entry);
                else loading++;
            }
            catch (Exception ex) { Fail(entry, ex.GetBaseException().Message); }
        }
        for (var started = 0; started < 2 && loading < 4 && _pending.Count > 0; started++)
        {
            var entry = _pending.Dequeue();
            try
            {
                entry.Handle = Addressables.LoadAssetAsync<CardAssetDataSO>(entry.Key);
                entry.HasHandle = true;
                if (entry.Handle.IsDone) Complete(entry);
                else loading++;
            }
            catch (Exception ex) { Fail(entry, ex.GetBaseException().Message); }
        }
    }

    private void Complete(Entry entry)
    {
        if (_disposed) return;
        if (entry.Handle.Status != AsyncOperationStatus.Succeeded)
        {
            Fail(entry, "Addressables load failed");
            return;
        }
        var asset = entry.Handle.Result;
        var material = asset != null ? asset.cardMaterial : null;
        // Item card materials store the actual painting in _MainTex; other textures are FX masks.
        var texture = material != null && material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
        if (texture == null) { Fail(entry, "Card painting unavailable"); return; }
        entry.Texture = texture;
        entry.Finished = true;
    }

    private void Fail(Entry entry, string reason)
    {
        entry.Finished = true;
        Release(entry);
        _warn("Advisor card art unavailable: " + entry.Key + " · " + reason);
    }

    private void Release(Entry entry)
    {
        if (!entry.HasHandle || entry.Released) return;
        entry.Released = true;
        try { if (entry.Handle.IsValid()) Addressables.Release(entry.Handle); }
        catch (Exception ex) { _warn("Advisor card art release failed: " + ex.GetBaseException().Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries.Values) Release(entry);
        _pending.Clear();
        _entries.Clear();
    }

    private sealed class Entry
    {
        internal Entry(string key) => Key = key;
        internal readonly string Key;
        internal AsyncOperationHandle<CardAssetDataSO> Handle;
        internal bool HasHandle;
        internal bool Released;
        internal bool Finished;
        internal Texture? Texture;
    }
}
