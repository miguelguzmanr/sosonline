using System;
using SosOnline.Game;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SosOnline.UI;

/// <summary>
/// "Online Co-op Version 0.56" in the title screen's bottom-left corner, mirroring the game's own version line in the
/// bottom-right: a copy of that text object (same font, size and colour) on the canvas, as far from the left edge as
/// the game's line is from the right edge, on the same line. (The game's line sits in a layout group, so a sibling
/// would be stacked under it.) Adds
/// "(Update available)" once the release lookup (<see cref="SosOnline.Update.AutoUpdate.Available"/>) finds a newer one.
/// </summary>
internal static class VersionLabel
{
    private const string Name = "SosOnline_VersionLabel";

    private static TextMeshProUGUI _label;
    private static GameObject _source;
    private static float _next;

    /// <summary>World Y of the top of the game's own version line (the bottom row with the copyright), or null.</summary>
    public static float? GameLineTop()
    {
        try
        {
            if (!Alive(_source) || !_source.activeInHierarchy) return null;
            var text = _source.GetComponent<TextMeshProUGUI>();
            if (text == null) return null;
            text.ForceMeshUpdate();
            return text.transform.TransformPoint(new Vector3(0f, text.textBounds.max.y, 0f)).y;
        }
        catch { return null; }
    }
    private static bool _missingLogged;

    public static string Text
    {
        get
        {
            var current = SosOnline.Update.AutoUpdate.DisplayCurrent;
            return SosOnline.Update.AutoUpdate.Available != null
                ? $"Online Co-op Version {current} (Update available)"
                : $"Online Co-op Version {current}";
        }
    }

    /// <summary>Main thread, every frame; does its work twice a second while the title screen is up.</summary>
    public static void Update()
    {
        if (Time.unscaledTime < _next) return;
        _next = Time.unscaledTime + 0.5f;
        if (!GameBridge.AtTitleScreen) return;
        try
        {
            if (!Alive(_label) || !Alive(_source)) _label = Create();
            if (_label == null) return;
            bool show = _source.activeInHierarchy;
            if (_label.gameObject.activeSelf != show) _label.gameObject.SetActive(show);
            string text = Text;
            if (_label.text != text) _label.text = text;
            Place();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[menu] version label: {e.Message}");
            _next = Time.unscaledTime + 10f;
        }
    }

    private static TextMeshProUGUI Create()
    {
        _source = FindGameVersion();
        if (_source == null)
        {
            if (!_missingLogged) Plugin.Log.LogWarning($"[menu] the game's version line ({Application.version}) wasn't found; no co-op version line");
            _missingLogged = true;
            return null;
        }
        var canvas = _source.GetComponentInParent<Canvas>();
        if (canvas == null) return null;
        var parent = canvas.rootCanvas.transform;
        var old = parent.Find(Name);
        if (old != null) Object.Destroy(old.gameObject);

        var go = Object.Instantiate(_source, parent, true);
        go.name = Name;
        // Only the text stays: whatever sets the game's version (or localizes it) would overwrite ours.
        foreach (var c in go.GetComponents<MonoBehaviour>())
            if (c != null && c.TryCast<TextMeshProUGUI>() == null) Object.DestroyImmediate(c);
        for (int i = go.transform.childCount - 1; i >= 0; i--) Object.DestroyImmediate(go.transform.GetChild(i).gameObject);

        var src = _source.GetComponent<RectTransform>();
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = Vector2.zero;
        rt.pivot = Vector2.zero;
        rt.sizeDelta = new Vector2(Mathf.Max(src.rect.width, 1600f), src.rect.height);

        var label = go.GetComponent<TextMeshProUGUI>();
        label.alignment = Mirror(label.alignment);
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.margin = Vector4.zero;
        label.text = Text;
        _label = label;
        Place();
        Plugin.Log.LogInfo($"[menu] co-op version line added next to '{_source.name}': {label.text}");
        return label;
    }

    /// <summary>Bottom-left corner of our line: mirrored from the game's line's right edge, same bottom.</summary>
    private static void Place()
    {
        var canvas = _label.transform.parent.GetComponent<RectTransform>();
        var src = _source.GetComponent<RectTransform>();
        var c = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        canvas.GetWorldCorners(c);
        float canvasLeft = c[0].x, canvasRight = c[2].x;
        src.GetWorldCorners(c);
        float bottom = c[0].y;
        // The game's text ends before its box does (alignment, margins): measure from the drawn text.
        var text = _source.GetComponent<TextMeshProUGUI>();
        float right = text != null && text.textBounds.size.x > 0f ? text.transform.TransformPoint(text.textBounds.max).x : c[2].x;
        float margin = canvasRight - right;
        var p = _label.transform.position;
        var want = new Vector3(canvasLeft + margin, bottom, p.z);
        if ((p - want).sqrMagnitude > 1e-6f) _label.transform.position = want;
    }

    /// <summary>The game's "Version 3.0.60151" text on the title screen.</summary>
    private static GameObject FindGameVersion()
    {
        string version = Application.version;
        foreach (var t in Object.FindObjectsOfType<TextMeshProUGUI>())
        {
            if (t == null || t.gameObject.name == Name) continue;
            string text = t.text;
            if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(version) && text.Contains(version, StringComparison.Ordinal))
                return t.gameObject;
        }
        return null;
    }

    private static TextAlignmentOptions Mirror(TextAlignmentOptions a)
    {
        string name = a.ToString();
        string flipped = name.Contains("Right") ? name.Replace("Right", "Left") : name.Contains("Left") ? name.Replace("Left", "Right") : name;
        return Enum.TryParse(flipped, out TextAlignmentOptions m) ? m : a;
    }

    private static bool Alive(Object o)
    {
        try { return o != null && o.GetInstanceID() != 0 && o; }
        catch { return false; }
    }
}
