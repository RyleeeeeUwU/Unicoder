using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Unicoder;

internal sealed class UnicoderRunner : MonoBehaviour
{
    private static readonly Queue<TMP_Text> Pending = new();
    private static readonly HashSet<TMP_Text> Queued = new();
    private bool _indexHandled;
    
    internal static void Enqueue(TMP_Text text)
    {
        if (text != null && Queued.Add(text)) Pending.Enqueue(text);
    }

    private void OnEnable()
    {
        SceneManager.activeSceneChanged += OnSceneChanged;
    }

    private void OnDisable()
    {
        SceneManager.activeSceneChanged -= OnSceneChanged;
    }

    private static void OnSceneChanged(Scene from, Scene to)
    {
        try
        {
            FontManager.OnSceneChanged();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex);
        }
    }

    private void Update()
    {
        if (!_indexHandled && FontIndex.Ready)
        {
            _indexHandled = true;
            FontManager.OnIndexReady();
        }
    }

    private void LateUpdate()
    {
        int budget = 64;
        while (budget-- > 0 && Pending.Count > 0)
        {
            var t = Pending.Dequeue();
            Queued.Remove(t);
            if (t == null || !t.isActiveAndEnabled) continue; //re-generated on enable anyway
            try
            {
                t.ForceMeshUpdate();
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug($"ForceMeshUpdate failed: {ex.Message}");
            }
        }
    }
}
