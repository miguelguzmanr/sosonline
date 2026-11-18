using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Rewired;
using SosOnline.Game;
using SosOnline.Lockstep;
using SosOnline.Net;
using SosOnline.Steam;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SosOnline.UI;

/// <summary>
/// The "Online" entries, built from the game's own widgets so they look, sound and navigate like the rest of the UI:
///  - Title screen: a copy of the Options button. Choosing it swaps the title's button list for our own entries
///    (copies of the same button) with a status line above them; "Back" restores the game's list.
///  - Pause menu: an item added through the game's own PauseMenu.AddPauseMenuItem, opening the game's
///    two-choice popup (the one used for "Return to title?").
///
/// Determinism: during a running session the pause menu is opened and navigated with synchronized input, so every
/// peer opens the same popup and receives the same answer on the same frame. Choices made there act as
/// deterministic commands ("End co-op session" stops lockstep on every peer at that frame).
/// </summary>
internal static class NativeMenus
{
    private const string TitleButtonName = "SosOnline_OnlineButton";
    private const string EntryName = "SosOnline_Entry";
    private const string StatusName = "SosOnline_Status";
    private const string PauseItemId = "sosonline";

    private static Sabotage.Localization.LocalizationId OnlineId => OnlineText.Static("online", "Online");

    // ================================================================== popups (two choices max)

    private static (GenericPopupScreen screen, int index, string label, float at)? _testAnswer;
    private static float _testBack;

    /// <summary>Test harness: acts as if UI_Back was pressed on the next frame (--sosonline-pageback, popupanswer=B).</summary>
    internal static void TestPressBack() => _fakeBackFrame = Time.frameCount + 1;

    /// <summary>Test harness (--sosonline-popupanswer=Label,...): answer a popup offering one of these, a moment after it opens.</summary>
    private static void TestAnswer(GenericPopupScreen screen, string[] labels)
    {
        if (!TestHarness.TryArg("popupanswer", out var list)) return;
        foreach (var want in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (want == "B") { _testBack = UnityEngine.Time.realtimeSinceStartup + 1.5f; return; }
            int i = Array.IndexOf(labels, want.Replace('_', ' '));
            if (i >= 0) { _testAnswer = (screen, i, labels[i], UnityEngine.Time.realtimeSinceStartup + 1.5f); return; }
        }
    }

    /// <summary>Test harness, every frame.</summary>
    internal static void TestAnswerTick()
    {
        if (_testBack > 0 && UnityEngine.Time.realtimeSinceStartup >= _testBack)
        {
            _testBack = 0;
            Plugin.Log.LogInfo("[test] popup: pressing Back");
            _fakeBackFrame = Time.frameCount + 1;
        }
        if (_testAnswer is not { } a || UnityEngine.Time.realtimeSinceStartup < a.at) return;
        _testAnswer = null;
        try
        {
            Plugin.Log.LogInfo($"[test] popup: choosing '{a.label}'");
            // The answer is the cursor's index, not just the selected button's.
            var button = a.screen.buttons[a.index];
            EventSystem.current?.SetSelectedGameObject(button.gameObject);
            a.screen.SelectButton(button);
            a.screen.cursorIndex = a.index;
            Plugin.Log.LogInfo($"[test] popup: cursor {a.screen.cursorIndex}, selected {EventSystem.current?.currentSelectedGameObject?.name}");
            a.screen.OnChoiceDone();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[test] popup answer failed: {e.Message}"); }
    }

    private static bool _popupOpen;
    private static int _popupToken;

    // The cursor before a popup opened, given back once it closes. Our title entries aren't part of a game view, so
    // nothing else restores them: after Back on "This is a local build…" the Online page had no selection and no
    // longer answered the pad or keyboard (2026-11-17).
    private static GameObject _selectedBeforePopup;
    private static int _popupClosedFrame = -100;
    private static bool _restorePending;
    private static GenericPopupScreen _openPopup;
    private static int _popupOpenedFrame;
    private static int _fakeBackFrame = -1;

    /// <summary>
    /// Every frame: B (UI_Back) on one of our popups chooses its last choice (Back, Later, OK). The game's own handling
    /// (resetOnBackInput) only moves the popup's cursor, and with our entries behind it the selection then went to the
    /// title menu: the popup stayed open, unanswered, and blocked every popup after it (2026-11-18).
    /// </summary>
    private static void PopupBackTick()
    {
        if (!_popupOpen || _openPopup == null || Time.frameCount - _popupOpenedFrame < 2) return;
        if (!BackPressed() && Time.frameCount != _fakeBackFrame) return;
        var screen = _openPopup;
        try
        {
            if (!Alive(screen) || !screen.gameObject.activeInHierarchy)
            {
                // Closed without an answer: don't keep blocking the next popup.
                Plugin.Log.LogWarning("[menu] popup closed without an answer");
                _popupOpen = false;
                _openPopup = null;
                _popupClosedFrame = Time.frameCount;
                _restorePending = true;
                return;
            }
            int last = screen.buttons.Count - 1;
            if (last < 0) return;
            Plugin.Log.LogInfo("[menu] popup: Back pressed, choosing the last choice");
            var button = screen.buttons[last];
            EventSystem.current?.SetSelectedGameObject(button.gameObject);
            screen.SelectButton(button);
            screen.cursorIndex = last;
            screen.OnChoiceDone();
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[menu] popup Back failed: {e.Message}"); }
    }

    /// <summary>
    /// The game's generic popup: a message, up to two choices; onAnswer(index), -1 for cancel. False when it can't
    /// open now (another one is open, or the UI isn't up).
    /// </summary>
    public static bool Popup(string message, string yes, string no, Action<int> onAnswer)
    {
        var ui = Manager<UIManager>.instance;
        if (ui == null || _popupOpen) return false;
        try
        {
            var p = new GenericPopupScreenParams();
            p.Init(message, true, no != null ? 1 : 0);
            p.choices.Clear();
            p.AddChoice(OnlineText.Dynamic(yes));
            if (no != null) p.AddChoice(OnlineText.Dynamic(no));
            var choiceLabels = no != null ? new[] { yes, no } : new[] { yes };
            Plugin.Log.LogInfo($"[menu] popup '{message.Replace("\n", " / ")}': {string.Join(" | ", choiceLabels)}");
            _popupOpen = true;
            try { _selectedBeforePopup = EventSystem.current?.currentSelectedGameObject; } catch { _selectedBeforePopup = null; }
            int token = ++_popupToken;
            Action<View> done = view =>
            {
                var screen = view?.TryCast<GenericPopupScreen>();
                if (screen == null) { if (token == _popupToken) { _popupOpen = false; _restorePending = true; } return; }
                bool handled = false;
                Action<GenericPopupScreen, bool, int> answered = (_, success, index) =>
                {
                    // The game reuses its popup view and adds its own handler with +=, so ours stays on the view after
                    // this answer. Without these guards, the game's next question (e.g. "Overwrite this save?") also
                    // ran our old choice: a Yes there ended the co-op session.
                    if (handled || token != _popupToken || !_popupOpen) return;
                    handled = true;
                    _popupOpen = false;
                    _openPopup = null;
                    _popupClosedFrame = Time.frameCount;
                    _restorePending = true;
                    try { screen.onPopupAnswered = null; } catch { }
                    Plugin.Log.LogInfo($"[menu] popup answered: {(success ? index.ToString() : "cancelled")}");
                    try { onAnswer?.Invoke(success ? index : -1); }
                    catch (Exception e) { Plugin.Log.LogError($"[menu] {e}"); }
                };
                screen.onPopupAnswered = answered;
                _openPopup = screen;
                _popupOpenedFrame = Time.frameCount;
                FitButtons(screen);
                TestAnswer(screen, choiceLabels);
            };
            ui.InstantiateViewAsync(Il2CppType.Of<GenericPopupScreen>(), EScreenLayers.MODAL, p.Cast<IViewParams>(), true,
                AnimatorUpdateMode.UnscaledTime, done);
            return true;
        }
        catch (Exception e)
        {
            _popupOpen = false;
            Plugin.Log.LogError($"[menu] popup failed: {e}");
            return false;
        }
    }

    /// <summary>Space left and right of a choice's label inside its highlight, in the popup's reference pixels.</summary>
    private const float ChoicePadX = 20f;

    /// <summary>
    /// Widest a popup choice may grow (padding included), about two thirds of the popup, so two choices still fit
    /// side by side. Keep labels within it (about 25 characters); a longer one is cut with an ellipsis and logged.
    /// </summary>
    private const float MaxChoiceWidth = 200f;

    private static bool _loggedPopupLayout;

    /// <summary>
    /// The game's popup buttons have a fixed width made for its own short choices ("Yes", "No"): a longer label of
    /// ours ran past the highlight. Widens each button, and every fixed-width part of it (the highlight), to its
    /// label; stretched parts follow the button. The buttons' container lays them out again.
    /// </summary>
    private static void FitButtons(GenericPopupScreen screen)
    {
        try
        {
            var buttons = screen.buttons;
            if (buttons == null) return;
            Canvas.ForceUpdateCanvases();
            for (int i = 0; i < buttons.Count; i++)
            {
                var b = buttons[i];
                var text = b?.textfield;
                if (text == null) continue;
                var rt = b.GetComponent<RectTransform>();
                float width = rt.rect.width;
                float need = Mathf.Ceil(text.GetPreferredValues(text.text, float.PositiveInfinity, float.PositiveInfinity).x + 2 * ChoicePadX);
                if (!_loggedPopupLayout)
                    Plugin.Log.LogInfo($"[menu] popup choice '{text.text}': button {width:F0}, label {need - 2 * ChoicePadX:F0}, text rect {text.rectTransform.rect.width:F0}");
                if (need > MaxChoiceWidth)
                {
                    Plugin.Log.LogWarning($"[menu] popup choice '{text.text}' is too long for a button ({need:F0} > {MaxChoiceWidth:F0}); shortened");
                    need = MaxChoiceWidth;
                    text.enableWordWrapping = false;
                    text.overflowMode = TextOverflowModes.Ellipsis;
                }
                if (need <= width) continue;

                // Fixed-width children as wide as the button (highlight, label box) grow with it.
                foreach (var child in b.GetComponentsInChildren<RectTransform>(true))
                    if (child != rt && Mathf.Approximately(child.anchorMin.x, child.anchorMax.x) && Mathf.Abs(child.rect.width - width) < 2f)
                        child.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, need);
                rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, need);
                var le = b.GetComponent<LayoutElement>() ?? b.gameObject.AddComponent<LayoutElement>();
                le.minWidth = le.preferredWidth = need;
            }
            _loggedPopupLayout = true;
            if (screen.buttonsContainer != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(screen.buttonsContainer.GetComponent<RectTransform>());
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[menu] popup buttons not resized: {e.Message}"); }
    }

    /// <summary>
    /// Every frame: after a popup closed (and no other one opened from its answer), put the cursor back where it was,
    /// or on the Online page's first entry, once the popup has let go of the selection.
    /// </summary>
    private static void RestoreAfterPopup()
    {
        if (!_restorePending || _popupOpen || Time.frameCount - _popupClosedFrame < 2) return;
        _restorePending = false;
        try
        {
            var current = EventSystem.current?.currentSelectedGameObject;
            // The answer moved the cursor to one of our entries itself (a new page): keep it. Anything else (the title
            // screen's own buttons, the popup's) isn't where the player was.
            if (current != null && current.activeInHierarchy && _entries.Any(b => b != null && Alive(b) && b.gameObject == current))
                if (current != _selectedBeforePopup) { Plugin.Log.LogInfo($"[menu] cursor after popup: {current.name} (kept)"); return; }
            var before = _selectedBeforePopup;
            if (before != null && Alive(before) && before.activeInHierarchy) { Select(before.GetComponent<Selectable>()); Plugin.Log.LogInfo($"[menu] cursor after popup: {before.name} (restored)"); return; }
            if (SubmenuOpen) { var first = _entries.FirstOrDefault(b => b != null && Alive(b) && b.gameObject.activeSelf); if (first != null) { Select(first); Plugin.Log.LogInfo($"[menu] cursor after popup: {first.name} (first entry)"); } }
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[menu] cursor not restored after a popup: {e.Message}"); }
        finally { _selectedBeforePopup = null; }
    }

    private static void Confirm(string message, string yes, Action onYes, string no = "Back", Action onNo = null) =>
        _ = Popup(message, yes, no, i => { if (i == 0) onYes?.Invoke(); else onNo?.Invoke(); });

    // ================================================================== title screen

    private static TitleScreen _title;
    private static readonly List<GameObject> _hiddenOriginals = new();
    private static readonly List<UIButton> _entries = new();
    private static TextMeshProUGUI _status;
    private static Func<IEnumerable<(string label, Action action)>> _currentPage;
    private static Phase _pagePhase;
    private static string _pageUpdateState;

    /// <summary>What the main page's update entries depend on, to rebuild it when they change.</summary>
    private static string UpdateState() =>
        $"{SosOnline.Update.AutoUpdate.CheckLabel}|{SosOnline.Update.AutoUpdate.RestartLabel}|{SosOnline.Update.AutoUpdate.Downloaded}";
    private static int _pagePeers;
    private static float _nextStatusRefresh;
    public static bool SubmenuOpen { get; private set; }

    [HarmonyPatch(typeof(TitleScreen), nameof(TitleScreen.ShowChoices))]
    private static class P_TitleShowChoices
    {
        private static void Postfix(TitleScreen __instance)
        {
            if (!NativeCode.Is<TitleScreen>(__instance)) return;
            ResetTitleMenu(__instance);
            InjectTitleButton(__instance);
        }
    }

    /// <summary>
    /// The game shows its title choices (at start, or back from a game): its own entries only. Its menu turns every
    /// button in the list back on, the Online page's entries included, which showed both menus mixed after a session
    /// (2026-11-16). Our entries and status line go, and the page state starts over.
    /// </summary>
    private static void ResetTitleMenu(TitleScreen title)
    {
        try
        {
            _starting = false;
            SubmenuOpen = false;
            _currentPage = null;
            foreach (var go in _hiddenOriginals) if (Alive(go)) SetActive(go, true);
            _hiddenOriginals.Clear();
            var container = title.optionsButton?.transform.parent;
            if (container != null)
                for (int i = 0; i < container.childCount; i++)
                {
                    var child = container.GetChild(i).gameObject;
                    if (child.name.StartsWith(EntryName, StringComparison.Ordinal)) SetActive(child, false);
                }
            if (Alive(_status)) SetActive(_status.gameObject, false);
        }
        catch (Exception e) { Plugin.Log.LogWarning($"[menu] title menu reset: {e.Message}"); }
    }

    [HarmonyPatch(typeof(TitleScreen), nameof(TitleScreen.RefreshButtons))]
    private static class P_TitleRefresh { private static void Postfix(TitleScreen __instance) { if (NativeCode.Is<TitleScreen>(__instance)) InjectTitleButton(__instance); } }

    private static void InjectTitleButton(TitleScreen title)
    {
        try
        {
            _title = title;
            var template = title.optionsButton;
            if (template == null) return;
            var parent = template.transform.parent;
            var existing = parent.Find(TitleButtonName);
            UIButton button;
            if (existing == null)
            {
                button = CloneButton(template, TitleButtonName, OnlineId, "Online", OpenTitleMenu);
                button.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);
                Plugin.Log.LogInfo("[menu] title screen Online button added");
            }
            else button = existing.GetComponent<UIButton>();
            if (!SubmenuOpen) SetActive(button.gameObject, template.gameObject.activeSelf);
            SpliceNavigation(template, button);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[menu] title injection failed: {e}");
        }
    }

    private static UIButton CloneButton(UIButton template, string name, Sabotage.Localization.LocalizationId id, string text, Action onClick)
    {
        var go = Object.Instantiate(template.gameObject, template.transform.parent);
        go.name = name;
        var button = go.GetComponent<UIButton>();
        // Persistent listeners (e.g. "open Options") are copied with the object, so replace the event itself.
        button.onClick = new UnityEvent();
        if (onClick != null) button.onClick.AddListener((UnityAction)onClick);
        SetLabel(go, id, text);
        return button;
    }

    /// <summary>
    /// Buttons can carry several labels (e.g. a separate one for the highlighted state), so relabel all of them.
    /// </summary>
    private static void SetLabel(GameObject go, Sabotage.Localization.LocalizationId id, string text)
    {
        var localizers = go.GetComponentsInChildren<Sabotage.Localization.TextLocalizer>(true);
        foreach (var l in localizers) l.LocalizeText(id);
        var tb = go.GetComponent<UITextButton>();
        if (tb != null) tb.SetText(text);
        foreach (var tmp in go.GetComponentsInChildren<TextMeshProUGUI>(true)) tmp.text = text;
    }

    private static void SetActive(GameObject go, bool active)
    {
        if (go != null && go.activeSelf != active) go.SetActive(active);
    }

    /// <summary>If the title list uses explicit navigation, insert <paramref name="ours"/> right below <paramref name="above"/>.</summary>
    private static void SpliceNavigation(Selectable above, Selectable ours)
    {
        var nav = above.navigation;
        if (nav.mode != Navigation.Mode.Explicit || nav.selectOnDown == ours) return;
        var below = nav.selectOnDown;
        nav.selectOnDown = ours;
        above.navigation = nav;
        var n = ours.navigation;
        n.mode = Navigation.Mode.Explicit;
        n.selectOnUp = above;
        n.selectOnDown = below;
        ours.navigation = n;
        if (below != null) { var b = below.navigation; b.selectOnUp = ours; below.navigation = b; }
    }

    // ---------------------------------------------------------------- submenu

    internal static void OpenTitleMenu() => ShowPage(MainPage);

    /// <summary>Test harness: a page of n entries plus Back (a long save list), to check the status stays off the copyright.</summary>
    internal static void ShowLongPageForTest(int n) => ShowPage(() =>
        Enumerable.Range(1, n).Select(i => ($"Slot {i}", (Action)(() => { }))).Append(("Back", () => ShowPage(MainPage))));

    /// <summary>Test harness: whether the status lines are drawn.</summary>
    internal static bool StatusShown => _status != null && _status.gameObject.activeInHierarchy && _status.alpha > 0f;

    /// <summary>Test harness: the title list's visible buttons, in order, and what is selected.</summary>
    internal static string TitleMenuState()
    {
        try
        {
            var title = GameBridge.FindTitleScreen();
            var container = title?.optionsButton?.transform.parent;
            if (container == null) return "no title menu";
            var names = new List<string>();
            for (int i = 0; i < container.childCount; i++)
            {
                var c = container.GetChild(i).gameObject;
                if (!c.activeInHierarchy) continue;
                var t = c.GetComponentInChildren<TextMeshProUGUI>(true);
                names.Add(t != null && !string.IsNullOrEmpty(t.text) ? t.text : c.name);
            }
            var sel = EventSystem.current?.currentSelectedGameObject;
            return $"[{string.Join(" | ", names)}] selected {(sel == null ? "none" : sel.name)}";
        }
        catch (Exception e) { return "state: " + e.Message; }
    }

    /// <summary>Test harness: press the visible entry with this label.</summary>
    internal static void ClickEntry(string label) => ClickEntry(_labels.FindIndex(l => l == label));

    /// <summary>Test harness: put the cursor on an entry (its label before any ": value").</summary>
    internal static void SelectEntryForTest(string key)
    {
        int i = _labels.FindIndex(l => EntryKey(l) == key);
        if (i >= 0 && i < _entries.Count) Select(_entries[i]);
    }

    /// <summary>Test harness: press the n-th visible entry.</summary>
    internal static void ClickEntry(int index)
    {
        // Selected first, as a player's cursor would be.
        if (index >= 0 && index < _entries.Count && _entries[index].gameObject.activeSelf) { Select(_entries[index]); _entries[index].onClick.Invoke(); }
    }

    /// <param name="select">The entry to put the cursor on (its label before any ": value"); else the same entry as
    /// before when rebuilding the page that is showing, else the first.</param>
    private static void ShowPage(Func<IEnumerable<(string label, Action action)>> page, string select = null)
    {
        if (_title == null) return;
        try
        {
            var template = _title.optionsButton;
            var container = template.transform.parent;
            // The title screen is rebuilt when the game returns to it: buttons made for the old one are gone.
            _entries.RemoveAll(b => !Alive(b) || b.transform.parent != container);
            _hiddenOriginals.RemoveAll(go => !Alive(go));
            if (!SubmenuOpen)
            {
                _hiddenOriginals.Clear();
                for (int i = 0; i < container.childCount; i++)
                {
                    var child = container.GetChild(i).gameObject;
                    if (child.activeSelf && !child.name.StartsWith(EntryName, StringComparison.Ordinal))
                    {
                        _hiddenOriginals.Add(child);
                        child.SetActive(false);
                    }
                }
                SubmenuOpen = true;
            }

            // Rebuilding the page that is showing (a toggle, a player joining): keep the cursor on the same entry.
            string keep = page == _currentPage ? SelectedEntryKey() : null;
            int keepIndex = keep == null ? -1 : _labels.FindIndex(l => EntryKey(l) == keep);
            _currentPage = page;
            _pagePhase = Session.Instance.Phase;
            _pagePeers = Session.Instance.Peers.Count();
            _pageUpdateState = UpdateState();
            var items = page().ToList();

            // Reuse entry buttons; create more as needed.
            while (_entries.Count < items.Count)
            {
                var b = CloneButton(template, EntryName + _entries.Count, OnlineText.Dynamic(""), "", null);
                _entries.Add(b);
            }
            for (int i = 0; i < _entries.Count; i++)
            {
                var b = _entries[i];
                bool used = i < items.Count;
                SetActive(b.gameObject, used);
                if (!used) continue;
                b.transform.SetAsLastSibling();
                var (label, action) = items[i];
                b.onClick = new UnityEvent();
                var captured = action;
                b.onClick.AddListener((UnityAction)(Action)(() => captured?.Invoke()));
                SetLabel(b.gameObject, OnlineText.Dynamic(label), label);
            }

            // Vertical wrap-around navigation through the visible entries.
            for (int i = 0; i < items.Count; i++)
            {
                var n = _entries[i].navigation;
                n.mode = Navigation.Mode.Explicit;
                n.selectOnUp = _entries[(i - 1 + items.Count) % items.Count];
                n.selectOnDown = _entries[(i + 1) % items.Count];
                n.selectOnLeft = n.selectOnRight = null;
                _entries[i].navigation = n;
            }

            _labels.Clear();
            _labels.AddRange(items.Select(it => it.label));
            UpdateStatus(container);
            int at = keep == null ? -1 : _labels.FindIndex(l => EntryKey(l) == keep);
            // An entry whose label changed ("Check for Updates" → "Checking for Updates…") keeps the cursor by position.
            if (at < 0 && keepIndex >= 0) at = Math.Min(keepIndex, items.Count - 1);
            if (select != null) { int wanted = _labels.FindIndex(l => EntryKey(l) == select); if (wanted >= 0) at = wanted; }
            Select(_entries[Math.Max(at, 0)]);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"[menu] submenu failed: {e}");
            CloseSubmenu(true);
        }
    }

    private static readonly List<string> _labels = new();

    /// <summary>An entry's label without its current value ("Open to Friends: On" → "Open to Friends").</summary>
    private static string EntryKey(string label)
    {
        int colon = label.IndexOf(':');
        return colon >= 0 ? label[..colon] : label;
    }

    private static string SelectedEntryKey()
    {
        try
        {
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected == null) return null;
            for (int i = 0; i < _labels.Count && i < _entries.Count; i++)
                if (_entries[i] != null && _entries[i].gameObject == selected) return EntryKey(_labels[i]);
        }
        catch { }
        return null;
    }

    /// <summary>The game's cancel (B / Esc) on any player, read straight from Rewired: no lockstep at the title.</summary>
    private static bool BackPressed()
    {
        try
        {
            if (!ReInput.isReady) return false;
            foreach (var p in ReInput.players.Players.ToList())
                if (p != null && p.GetButtonDown("UI_Back")) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Not destroyed (a C# null check misses destroyed Unity objects).</summary>
    private static bool Alive(Object o)
    {
        try { return o != null && o.GetInstanceID() != 0 && o; }
        catch { return false; }
    }

    private static void Select(Selectable s)
    {
        if (s == null) return;
        try
        {
            EventSystem.current?.SetSelectedGameObject(s.gameObject);
            s.Select();
        }
        catch { }
    }

    // A save was chosen to start the game: the menu is hidden until the game leaves the title screen (or the start
    // falls through and the page comes back).
    private static bool _starting;

    /// <summary>
    /// The host chose a save: hide the whole menu and drop the cursor right away. Otherwise it stayed visible, and
    /// could still be used, while the guests got ready and the game loaded.
    /// </summary>
    private static void HideForStart()
    {
        _starting = true;
        foreach (var b in _entries) if (Alive(b)) SetActive(b.gameObject, false);
        if (Alive(_status)) SetActive(_status.gameObject, false);
        try { EventSystem.current?.SetSelectedGameObject(null); } catch { }
        Plugin.Log.LogInfo("[menu] save chosen: title menu hidden while the game starts");
    }

    private static void CloseSubmenu(bool reselect)
    {
        if (!SubmenuOpen) return;
        SubmenuOpen = false;
        _currentPage = null;
        foreach (var b in _entries) if (Alive(b)) SetActive(b.gameObject, false);
        foreach (var go in _hiddenOriginals) SetActive(go, true);
        _hiddenOriginals.Clear();
        if (_status != null) SetActive(_status.gameObject, false);
        if (reselect && _title != null)
        {
            var online = _title.optionsButton.transform.parent.Find(TitleButtonName);
            if (online != null) Select(online.GetComponent<Selectable>());
        }
    }

    /// <summary>A text line above the list, styled like the game's own button labels.</summary>
    private static void UpdateStatus(Transform container)
    {
        if (_status == null)
        {
            var templateText = _title.optionsButton.GetComponentInChildren<TextMeshProUGUI>(true);
            var go = new GameObject(StatusName);
            go.transform.SetParent(container.parent, false);
            _status = go.AddComponent<TextMeshProUGUI>();
            if (templateText != null)
            {
                _status.font = templateText.font;
                _status.fontSharedMaterial = templateText.fontSharedMaterial;
                _status.fontSize = templateText.fontSize;
                _status.color = new Color(1f, 0.88f, 0.55f);
            }
            _status.alignment = TextAlignmentOptions.Top;
            _status.enableWordWrapping = true;
            var rt = _status.rectTransform;
            var crt = container.GetComponent<RectTransform>();
            rt.anchorMin = crt.anchorMin;
            rt.anchorMax = crt.anchorMax;
            // Below the button list, above the copyright line.
            rt.pivot = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(Mathf.Max(crt.rect.width, 760f), 140f);
            rt.anchoredPosition = crt.anchoredPosition - new Vector2(0f, crt.rect.height * crt.pivot.y + 12f);
        }
        SetActive(_status.gameObject, true);
        _status.text = PageStatus(Session.Instance);
        PlaceStatus();
    }

    /// <summary>Just below the last visible entry, whatever the list length.</summary>
    private static void PlaceStatus()
    {
        if (_status == null) return;
        UIButton last = null;
        foreach (var b in _entries) if (b != null && b.gameObject.activeSelf) last = b;
        if (last == null) return;
        Canvas.ForceUpdateCanvases();
        var lastRt = last.GetComponent<RectTransform>();
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        lastRt.GetWorldCorners(corners);
        float bottom = Mathf.Min(corners[0].y, corners[3].y);
        float centerX = (corners[0].x + corners[3].x) * 0.5f;
        var p = _status.transform.position;
        _status.transform.position = new Vector3(centerX, bottom, p.z);
        _status.rectTransform.anchoredPosition -= new Vector2(0f, 6f);
        // A long page (many saves or friends) would push it onto the copyright row: hide it there instead.
        try
        {
            float? floor = VersionLabel.GameLineTop();
            _status.ForceMeshUpdate();
            float statusBottom = _status.transform.TransformPoint(new Vector3(0f, _status.textBounds.min.y, 0f)).y;
            _status.alpha = floor is { } f && statusBottom < f ? 0f : 1f;
        }
        catch { _status.alpha = 1f; }
    }

    /// <summary>Keeps the submenu current while it is open (players joining, connection progress…).</summary>
    public static void Update()
    {
        PopupBackTick();
        RestoreAfterPopup();
        if (!SubmenuOpen || _title == null) return;
        if (!GameBridge.AtTitleScreen) { _starting = false; CloseSubmenu(false); return; }
        if (_starting)
        {
            // Still at the title: wait for the game to start, unless the start fell through (an error, a guest left).
            var phase = Session.Instance.Phase;
            if (phase == Phase.AwaitingReady || phase == Phase.Running) return;
            _starting = false;
            Plugin.Log.LogInfo("[menu] the start didn't happen: title menu back");
            ShowPage(MainPage);
            return;
        }
        // The Back that answered a popup must not also close the page (it can still read as pressed this frame).
        if (!_popupOpen && Time.frameCount - _popupClosedFrame > 2 && (BackPressed() || Time.frameCount == _fakeBackFrame))
        {
            // B puts the cursor on the page's Back entry; B on Back does what Back does (a sub-page goes back to the
            // main page, the main page closes).
            int back = _labels.FindLastIndex(l => l == "Back");
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (back >= 0 && back < _entries.Count && _entries[back] != null && _entries[back].gameObject != selected)
            {
                Select(_entries[back]);
                return;
            }
            if (back >= 0 && back < _entries.Count && _entries[back] != null) _entries[back].onClick.Invoke();
            else if (_currentPage == MainPage) CloseSubmenu(true);
            else ShowPage(MainPage);
            return;
        }
        var s = Session.Instance;
        if (s.Phase != _pagePhase || s.Peers.Count() != _pagePeers)
        {
            // Session state changed under us: rebuild the main page (other pages may no longer apply).
            ShowPage(MainPage);
            return;
        }
        if ((_currentPage == MainPage || _currentPage == OptionsPage) && !_popupOpen && !_restorePending && UpdateState() != _pageUpdateState)
        {
            // The update entries changed (a check finished, a download is ready): "Checking for updates…" stayed on
            // the page after its answer was shown (2026-11-18).
            ShowPage(_currentPage);
            return;
        }
        // The Options page describes the highlighted entry: refresh as soon as the cursor moves.
        string selectedKey = _currentPage == OptionsPage ? SelectedEntryKey() : null;
        if (Time.unscaledTime >= _nextStatusRefresh || selectedKey != _statusKey)
        {
            _nextStatusRefresh = Time.unscaledTime + 0.5f;
            _statusKey = selectedKey;
            if (_status != null) { _status.text = PageStatus(s); PlaceStatus(); }
        }
    }

    // ---------------------------------------------------------------- pages

    private static IEnumerable<(string, Action)> MainPage()
    {
        var s = Session.Instance;
        // Not a synced menu (the title screen runs no lockstep), so a per-PC entry is fine here.
        if (SosOnline.Update.AutoUpdate.RestartLabel is { } restart)
            yield return (restart, () =>
            {
                if (s.InSession) Confirm("Restart the game now?", "Restart", SosOnline.Update.AutoUpdate.Restart);
                else SosOnline.Update.AutoUpdate.Restart();
            });
        switch (s.Phase)
        {
            case Phase.Idle:
                yield return ("Host", () => s.Host());
                yield return ("Join", () => ShowPage(FriendsPage));
                break;
            case Phase.CreatingLobby:
            case Phase.JoiningLobby:
            case Phase.Connecting:
                yield return ("Cancel", () => s.Leave());
                break;
            case Phase.Lobby:
            case Phase.AwaitingReady:
                if (s.IsHost)
                {
                    if (s.Phase == Phase.Lobby) yield return ("Load Game", () => ShowPage(SavesPage));
                    if (!s.UsingUdp) yield return ("Invite Friends", () => s.InviteFriends());
                    yield return ("Stop Hosting", () => s.Leave());
                }
                else yield return ("Leave Co-op", () => s.Leave());
                break;
        }
        yield return ("Options", () => ShowPage(OptionsPage));
        yield return ("Back", () => CloseSubmenu(true));
    }

    /// <summary>Settings and tools, off the main page so the list and its status lines stay clear of the copyright.</summary>
    private static IEnumerable<(string, Action)> OptionsPage()
    {
        var s = Session.Instance;
        if (s.IsHost && (s.Phase == Phase.Lobby || s.Phase == Phase.AwaitingReady))
            yield return ($"Input Delay: {(s.ConfiguredDelay == 0 ? "Auto" : s.ConfiguredDelay.ToString())}", () => ShowPage(DelayPage));
        if (!s.InSession || s.IsHost)
            yield return ($"Open to Friends: {(Settings.OpenToFriends ? "On" : "Off")}", () => { Settings.OpenToFriends = !Settings.OpenToFriends; ShowPage(OptionsPage); });
        // Per PC: only outside a run (the in-session pause popup runs on every PC and may only offer the same choices).
        if (s.Phase != Phase.Running && SosOnline.Update.AutoUpdate.Downloaded == null)
            yield return (SosOnline.Update.AutoUpdate.CheckLabel, () => { SosOnline.Update.AutoUpdate.CheckNow(); ShowPage(OptionsPage); });
        // Here rather than on the main page, which then never reaches the copyright (the pause screen has them too).
        if (s.InSession && !s.IsHost) yield return ("Send Logs to Host", () => s.SendLogsToHost());
        else if (s.IsHost && s.Peers.Any()) yield return ("Ask for Logs", () => s.AskForLogs());
        yield return ("Open Logs Folder", SessionLogs.OpenFolder);
        yield return ("Back", () => ShowPage(MainPage, "Options"));
    }

    private static List<(ulong id, string name, ulong lobby)> _friendsCache = new();

    private static IEnumerable<(string, Action)> FriendsPage()
    {
        if (SteamApi.TryInit()) _friendsCache = SteamApi.FriendsInGame().Where(f => f.lobby != 0).Take(6).ToList();
        foreach (var f in _friendsCache)
        {
            var friend = f;
            yield return (friend.name, () => Session.Instance.JoinFriend(friend.id));
        }
        yield return ("Refresh", () => ShowPage(FriendsPage));
        yield return ("Back", () => ShowPage(MainPage, "Join"));
    }

    private static IEnumerable<(string, Action)> SavesPage()
    {
        foreach (var slot in GameBridge.ListSaveSlots().Take(6))
        {
            var sl = slot;
            yield return (sl.label, () => { HideForStart(); Session.Instance.StartFromSave(sl.index, sl.auto); });
        }
        yield return ("Back", () => ShowPage(MainPage, "Load Game"));
    }

    private static IEnumerable<(string, Action)> DelayPage()
    {
        var s = Session.Instance;
        foreach (int d in new[] { 0, 3, 5, 8 })
        {
            int delay = d;
            yield return (delay == 0 ? "Auto" : $"{delay} Frames ({delay * 17} ms)", () => { s.ConfiguredDelay = delay; ShowPage(OptionsPage, "Input Delay"); });
        }
        yield return ("Back", () => ShowPage(OptionsPage, "Input Delay"));
    }

    // ================================================================== pause menu

    [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Init))]
    private static class P_PauseInit
    {
        private static void Postfix(PauseMenu __instance)
        {
            if (!NativeCode.Is<PauseMenu>(__instance)) return;
            try
            {
                if (__instance.GetItem(PauseItemId) != null) return;
                __instance.AddPauseMenuItem(PauseItemId, PickPauseAnchor(__instance), OnlineId, (UnityAction)(Action)OpenPauseMenu);
            }
            catch (Exception e) { Plugin.Log.LogError($"[menu] pause injection failed: {e}"); }
        }
    }

    /// <summary>
    /// Options from the shared pause (battles and cutscenes; elsewhere the pause is local, see <see cref="LocalPause"/>)
    /// only for the host. A guest's controls page is built from the controllers assigned to that player on each PC, so
    /// it is empty on every PC but the guest's and ignores the guest's Back there: the menus diverge (findings §7). The
    /// UI player is synced game state, so every PC skips it on the same frame.
    /// </summary>
    [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Options))]
    private static class P_PauseOptions
    {
        private static bool Prefix(PauseMenu __instance)
        {
            if (!NativeCode.Is<PauseMenu>(__instance) || Session.Instance.Phase != Phase.Running) return true;
            int slot = -1;
            try { slot = GameBridge.SlotOf(GameBridge.Coop?.GetUIInputsPlayer()); } catch { }
            if (slot <= 0) return true;
            Plugin.Log.LogInfo($"[menu] f{Session.Instance.Engine.AppliedFrame} Options from the shared pause blocked for P{slot + 1}");
            if (slot == Session.Instance.LocalSlot)
                Toast.Show("Open Options from your own pause screen");
            return false;
        }
    }

    private static bool _loggedPauseIds;

    private static string PickPauseAnchor(PauseMenu menu)
    {
        var ids = new List<string>();
        var items = menu.pauseMenuItems;
        for (int i = 0; i < items.Count; i++) ids.Add(items[i].id);
        if (!_loggedPauseIds) { _loggedPauseIds = true; Plugin.Log.LogInfo("[menu] pause items: " + string.Join(", ", ids)); }
        var match = ids.FirstOrDefault(id => id.IndexOf("option", StringComparison.OrdinalIgnoreCase) >= 0);
        return match ?? (ids.Count > 0 ? ids[0] : "");
    }

    /// <summary>
    /// While lockstep runs this executes on every peer on the same frame (the pause menu is driven by synced input),
    /// so it only does deterministic things.
    /// </summary>
    /// <summary>Test harness: the pause menu's Online popup, as if chosen from the pause menu.</summary>
    internal static void OpenPauseMenuForTest() => OpenPauseMenu();

    private static void OpenPauseMenu()
    {
        var s = Session.Instance;
        if (s.Phase == Phase.Running)
        {
            // Runs on every peer on the same frame: the choices may only depend on synced game state (who opened the
            // menu), never on network state, so each answer means the same thing everywhere. Only the text may differ.
            int slot = -1;
            try { slot = GameBridge.SlotOf(GameBridge.Coop?.GetUIInputsPlayer()); } catch { }
            if (slot > 0)
            {
                Confirm($"{PlayersLine(s)}\nLeave co-op, P{slot + 1}?",
                    "Leave", () => s.LeaveSlotDeterministic(slot));
                return;
            }
            string waiting = s.HasPending ? $"\nJoining: {string.Join(", ", s.PendingNames)}" : "";
            Confirm($"{PlayersLine(s)}{waiting}", "End co-op",
                () => s.EndSessionDeterministic("the host ended it"));
            return;
        }
        if (!s.InSession && SosOnline.Update.AutoUpdate.Downloaded != null)
        {
            // Not synced (no lockstep runs here), so the choice may depend on this PC's download. Friends get the
            // update too, so playing together waits for the restart anyway.
            Confirm($"Update {SosOnline.Update.AutoUpdate.Display(SosOnline.Update.AutoUpdate.Downloaded)} is ready.\nEveryone needs the same version to play together.",
                "Restart", SosOnline.Update.AutoUpdate.Restart);
            return;
        }
        if (!s.InSession)
        {
            Confirm("Open the game to friends?\nThey can join at any point if a save is possible.",
                "Open", () => { Settings.OpenToFriends = true; s.Host(); });
            return;
        }
        if (s.IsHost && !s.UsingUdp)
            Popup(StatusText(s) + "\nFriends who join drop in right here.", "Invite", "Stop hosting",
                i => { if (i == 0) s.InviteFriends(); else if (i == 1) { Settings.OpenToFriends = false; s.Leave(); } });
        else
            Confirm(StatusText(s), s.IsHost ? "Stop hosting" : "Leave", () => s.Leave());
    }

    // ================================================================== text

    private static string PlayersLine(Session s) => string.Join("    ",
        Enumerable.Range(0, InputOverride.SlotCount).Where(i => !string.IsNullOrEmpty(s.SlotName[i])).Select(i => $"P{i + 1} {s.SlotName[i]}"));

    private static string _statusKey;

    /// <summary>What the Options page's entries do, shown under the list for the highlighted one.</summary>
    private static readonly Dictionary<string, string> OptionsHelp = new()
    {
        ["Input Delay"] = "Frames each press waits so every PC stays in step.\nAuto adapts it to the ping.",
        ["Open to Friends"] = "Keep a Steam lobby open while you play,\nso friends can drop into your game",
        ["Check for Updates"] = "Look for a newer version of the mod",
        ["Checking for Updates…"] = "Look for a newer version of the mod",
        ["Send Logs to Host"] = "Send your recent logs to the host,\nto help find a problem",
        ["Ask for Logs"] = "Ask friends to send you their logs",
        ["Open Logs Folder"] = "Open the folder with this PC's logs",
        ["Back"] = "Back to the Online menu",
    };

    /// <summary>The status lines for the page showing: on Options, what the highlighted entry does.</summary>
    private static string PageStatus(Session s)
    {
        if (_currentPage == OptionsPage && SelectedEntryKey() is { } key && OptionsHelp.TryGetValue(key, out var help)) return help;
        return StatusText(s);
    }

    private static string StatusText(Session s)
    {
        if (s.Phase == Phase.Idle)
            return string.IsNullOrEmpty(s.LastError)
                ? "Play with up to two friends online.\nEach player controls a party member, like local co-op."
                : s.LastError;
        var lines = new List<string> { s.Status, PlayersLine(s) };
        if (s.Peers.Any()) lines.Add($"Ping {s.Peers.Max(p => p.rtt):F0} ms");
        return string.Join("\n", lines);
    }
}
