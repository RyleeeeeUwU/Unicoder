using System;
using System.IO;
using HarmonyLib;
using IPA;
using IPA.Loader;
using IPA.Utilities;
using TMPro;
using UnityEngine;
using IpaLogger = IPA.Logging.Logger;

namespace Unicoder;

[Plugin(RuntimeOptions.SingleStartInit)]
internal class Plugin
{
    internal static IpaLogger Log { get; private set; } = null!;

    private Harmony? _harmony;
    private GameObject? _host;
    
    [Init]
    public Plugin(IpaLogger ipaLogger, PluginMetadata pluginMetadata)
    {
        Log = ipaLogger;
        _harmony = new Harmony(pluginMetadata.Id);
        Log.Info($"{pluginMetadata.Name} {pluginMetadata.HVersion} initialized.");
    }

    [OnStart]
    public void OnApplicationStart()
    {
        try
        {
            // Drop .ttf/.otf/.ttc files in UserData/Unicoder/Fonts to have them preferred over system fonts.
            string userFonts = Path.Combine(UnityGame.UserDataPath, "Unicoder", "Fonts");
            try
            {
                Directory.CreateDirectory(userFonts);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not create '{userFonts}': {ex.Message}");
            }

            string[] osFonts = Array.Empty<string>();
            try
            {
                osFonts = Font.GetPathsToOSFonts() ?? osFonts;
            }
            catch (Exception ex)
            {
                Log.Warn($"Font.GetPathsToOSFonts failed: {ex.Message}");
            }

            FontIndex.BeginBuild(osFonts, userFonts);

            _host = new GameObject("Unicoder") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.AddComponent<UnicoderRunner>();

            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(TmpHooks.OnTextChanged);
            if (_harmony != null) TmpHooks.Apply(_harmony);
        }
        catch (Exception ex)
        {
            Log.Error($"Unicoder failed to start: {ex}");
        }
    }

    [OnExit]
    public void OnApplicationQuit()
    {
        try
        {
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(TmpHooks.OnTextChanged);
            _harmony?.UnpatchSelf();
        }
        catch (Exception ex)
        {
            Log.Debug($"Shutdown: {ex.Message}");
        }
    }
}