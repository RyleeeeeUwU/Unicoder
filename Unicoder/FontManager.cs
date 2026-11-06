using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace Unicoder;

internal static class FontManager
{
    private sealed class LoadedFont
    {
        public readonly FontEntry Entry;
        public readonly TMP_FontAsset Asset;

        public LoadedFont(FontEntry entry, TMP_FontAsset asset)
        {
            Entry = entry;
            Asset = asset;
        }
    }

    private const string AssetPrefix = "Unicoder-";

    private static readonly List<LoadedFont> Chain = new();
    private static readonly HashSet<TMP_FontAsset> Ours = new();
    private static readonly HashSet<string> LoadedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> FailedPaths = new(StringComparer.OrdinalIgnoreCase);
    
    private static readonly HashSet<int> Handled = new();
    private static bool _usingLocalTables;
    
    internal static bool EnsureString(TMP_Text? owner, string? s)
    {
        if (string.IsNullOrEmpty(s) || !FontIndex.Ready) return false;

        TMP_FontAsset? primary = null;
        if (owner != null)
        {
            try
            {
                primary = owner.font;
            }
            catch (Exception)
            {
                // font can be null on half-initialised objects
            }
        }

        bool changed = false;
        for (int i = 0; i < s!.Length; i++)
        {
            int cp = s[i];
            if (cp < 0xA0) continue; // ASCII + C1 controls: every game font has what it needs

            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cp = char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                continue;
            }

            if (Handled.Contains(cp) || IsInvisible(cp) || PrimaryHas(primary, cp)) continue;
            if (Resolve(cp)) changed = true;
        }

        return changed;
    }

    internal static bool HasNonAscii(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s!)
            if (c >= 0xA0)
                return true;
        return false;
    }

    // Called once the background font index has finished: fix up everything already on screen
    internal static void OnIndexReady()
    {
        Plugin.Log.Info($"Font index ready ({FontIndex.Count} fonts), refreshing existing text.");
        try
        {
            foreach (var t in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (t == null || !t.gameObject.scene.IsValid()) continue;
                string s = t.text;
                if (!HasNonAscii(s)) continue;
                EnsureString(t, s);
                UnicoderRunner.Enqueue(t);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"Refreshing text after indexing failed: {ex}");
        }
    }

    // Called on scene changes: keep runtime assets alive and re-attach to freshly loaded font assets
    internal static void OnSceneChanged()
    {
        foreach (var c in Chain) KeepAlive(c.Asset);
        if (_usingLocalTables && Chain.Count > 0) ApplyChain();
    }
    

    private static bool PrimaryHas(TMP_FontAsset? primary, int cp)
    {
        if (primary == null) return false;
        try
        {
            return cp <= 0xFFFF
                ? primary.HasCharacter((char)cp, true, false)
                : primary.HasCharacter(cp);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Characters that draw nothing; they must not trigger font loading
    internal static bool IsInvisible(int cp)
    {
        return cp == 0xAD
               || (cp >= 0x200B && cp <= 0x200F)
               || (cp >= 0x2028 && cp <= 0x202E)
               || (cp >= 0x2060 && cp <= 0x206F)
               || (cp >= 0x180B && cp <= 0x180E)
               || (cp >= 0xFE00 && cp <= 0xFE0F)
               || cp == 0xFEFF
               || cp == 0x034F
               || (cp >= 0xE0000 && cp <= 0xE01EF);
    }

    private static bool Resolve(int cp)
    {
        var candidates = FontIndex.Candidates(cp);
        
        foreach (var e in candidates)
        {
            if (LoadedPaths.Contains(e.Path))
            {
                Handled.Add(cp);
                return false;
            }
        }

        foreach (var e in candidates)
        {
            if (FailedPaths.Contains(e.Path)) continue;
            var asset = LoadAsset(e);
            if (asset == null)
            {
                FailedPaths.Add(e.Path);
                continue;
            }

            LoadedPaths.Add(e.Path);
            Chain.Add(new LoadedFont(e, asset));
            Chain.Sort((a, b) =>
            {
                int c = a.Entry.Priority.CompareTo(b.Entry.Priority);
                return c != 0 ? c : string.Compare(a.Entry.Path, b.Entry.Path, StringComparison.OrdinalIgnoreCase);
            });
            Ours.Add(asset);
            ApplyChain();
            Handled.Add(cp);
            Plugin.Log.Info($"Fallback font '{Path.GetFileName(e.Path)}' added for U+{cp:X4}.");
            return true;
        }

        Handled.Add(cp);
        Plugin.Log.Debug($"No installed font can draw U+{cp:X4}.");
        return false;
    }

    private static TMP_FontAsset? LoadAsset(FontEntry entry)
    {
        Font? font = null;
        try
        {
            font = new Font(entry.Path);
            var asset = TMP_FontAsset.CreateFontAsset(font);
            if (asset == null)
            {
                UObject.Destroy(font);
                return null;
            }

            asset.name = AssetPrefix + Path.GetFileNameWithoutExtension(entry.Path);
            try
            {
                asset.isMultiAtlasTexturesEnabled = true;
            }
            catch (Exception)
            {
            }

            font.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            KeepAlive(asset);
            return asset;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warn($"Could not load font '{entry.Path}': {ex.Message}");
            if (font != null) UObject.Destroy(font);
            return null;
        }
    }
    
    private static void KeepAlive(TMP_FontAsset asset)
    {
        if (asset == null) return;
        try
        {
            asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            if (asset.material != null) asset.material.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            var atlases = asset.atlasTextures;
            if (atlases != null)
                foreach (var tex in atlases)
                    if (tex != null)
                        tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
        }
        catch (Exception)
        {
        }
    }

    private static void ApplyChain()
    {
        List<TMP_FontAsset>? global = null;
        try
        {
            global = TMP_Settings.fallbackFontAssets;
        }
        catch (Exception)
        {
            // TMP Settings asset not available
        }

        if (global != null)
        {
            global.RemoveAll(a => a != null && Ours.Contains(a));
            foreach (var c in Chain) global.Add(c.Asset);
            _usingLocalTables = false;
            return;
        }
        
        _usingLocalTables = true;
        foreach (var fa in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (fa == null || Ours.Contains(fa)) continue;
            var table = fa.fallbackFontAssetTable;
            if (table == null)
            {
                table = new List<TMP_FontAsset>();
                fa.fallbackFontAssetTable = table;
            }

            foreach (var c in Chain)
                if (!table.Contains(c.Asset))
                    table.Add(c.Asset);
        }
    }
}
