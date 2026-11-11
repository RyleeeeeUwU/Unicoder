using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using TMPro;
using UObject = UnityEngine.Object;

namespace Unicoder;

internal static class TmpHooks
{
    internal static void Apply(Harmony harmony)
    {
        TryPatch(harmony, AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.text)), nameof(TextSetterPrefix));
        TryPatch(harmony, AccessTools.Method(typeof(TMP_Text), "SetText", new[] { typeof(string), typeof(bool) }),
            nameof(SetTextPrefix));
    }

    private static void TryPatch(Harmony harmony, MethodBase? original, string prefixName)
    {
        if (original == null)
        {
            Plugin.Log.Warn($"Harmony target for {prefixName} not found; relying on the TMP text-changed event only");
            return;
        }

        try
        {
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(TmpHooks), prefixName));
            harmony.Patch(original, prefix: prefix);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warn($"Could not patch {original.Name}: {ex.Message}");
        }
    }

    private static void TextSetterPrefix(TMP_Text __instance, ref string value)
    {
        Prepare(__instance, ref value);
    }

    private static void SetTextPrefix(TMP_Text __instance, [HarmonyArgument(0)] ref string text)
    {
        Prepare(__instance, ref text);
    }

    private static void Prepare(TMP_Text owner, ref string? s)
    {
        try
        {
            if (string.IsNullOrEmpty(s) || !FontManager.HasNonAscii(s)) return;
            s = StripInvisible(s!);
            FontManager.EnsureString(owner, s);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"Prepare failed: {ex.Message}");
        }
    }

    internal static void OnTextChanged(UObject obj)
    {
        try
        {
            if (obj is not TMP_Text t || t == null) return;
            string s = t.text;
            if (FontManager.HasNonAscii(s) && FontManager.EnsureString(t, s))
                UnicoderRunner.Enqueue(t);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug($"OnTextChanged failed: {ex.Message}");
        }
    }
    
    private static bool ShouldStrip(char c) =>
        c == '‍' || c == '⁠' || c == '﻿' || (c >= '︀' && c <= '️');

    private static bool ShouldStripAstral(int cp) =>
        (cp >= 0xE0000 && cp <= 0xE007F) || (cp >= 0xE0100 && cp <= 0xE01EF);

    internal static string StripInvisible(string s)
    {
        int first = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (ShouldStrip(c) ||
                (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) &&
                 ShouldStripAstral(char.ConvertToUtf32(c, s[i + 1]))))
            {
                first = i;
                break;
            }
        }

        if (first < 0) return s;

        var sb = new StringBuilder(s.Length);
        sb.Append(s, 0, first);
        for (int i = first; i < s.Length; i++)
        {
            char c = s[i];
            if (ShouldStrip(c)) continue;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                int cp = char.ConvertToUtf32(c, s[i + 1]);
                if (ShouldStripAstral(cp))
                {
                    i++;
                    continue;
                }

                sb.Append(c).Append(s[i + 1]);
                i++;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }
}
