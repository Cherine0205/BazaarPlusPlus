using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace KarnokAdvisor;

internal sealed class AdvisorPanel : IDisposable
{
    private const float Padding = 20;
    private static readonly Color Gold = Hex(0xD8B16B);
    private static readonly Color Ivory = Hex(0xF0E1C6);
    private static readonly Color Muted = Hex(0xAF9F92);
    private static readonly Color Green = Hex(0xA2C49A);
    private static readonly Color Cyan = Hex(0x83C5CB);
    private static readonly Color Amber = Hex(0xE0B27B);
    private readonly List<Texture2D> _textures = new List<Texture2D>();
    private readonly HashSet<string> _open = new HashSet<string>();
    private readonly ReferenceViewState _selection = new ReferenceViewState();
    private readonly CardArtCache _art;
    private readonly Font _bodyFont;
    private readonly Font _titleFont;
    private readonly GameObject _blocker;
    private readonly RectTransform _blockerRect;
    private readonly GUIStyle _frame, _card, _coreCard, _button, _selectedButton, _smallButton;
    private readonly GUIStyle _body, _muted, _small, _title, _heading, _tileTitle, _tileMeta;
    private readonly GUIStyle _windowStyle, _scrollTrack, _scrollThumb;
    private readonly Texture2D _diamond;
    private Rect _window;
    private Vector2 _scroll;
    private bool _initialized, _visible, _compact, _live;
    private float _scale = 1;
    private string _contentId = "";
    private string _message = "";
    private JObject? _reference;
    private Action? _refresh;
    private float Width => _window.width;
    internal string RenderDiagnostics => "visible=" + _visible + " compact=" + _compact + " window=" + _window + " scale=" + _scale;

    internal AdvisorPanel(Transform parent, Action<string> warn)
    {
        _art = new CardArtCache(warn);
        _bodyFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 18);
        _titleFont = Font.CreateDynamicFontFromOSFont(new[] { "SimSun", "Microsoft YaHei", "Georgia" }, 26);
        _body = TextStyle(_bodyFont, 14, Ivory, true);
        _muted = TextStyle(_bodyFont, 13, Muted, true);
        _small = TextStyle(_bodyFont, 11, Muted, false);
        _title = TextStyle(_titleFont, 24, Ivory, false, FontStyle.Bold);
        _heading = TextStyle(_bodyFont, 18, Ivory, true, FontStyle.Bold);
        _tileTitle = TextStyle(_bodyFont, 13, Ivory, true, FontStyle.Bold);
        _tileMeta = TextStyle(_bodyFont, 11, Muted, true);
        _frame = Surface(Hex(0x211D25), Hex(0xA47C48), 7, 2, .014f);
        _card = Surface(Hex(0x29252C), Hex(0x6B5748), 5, 1, .007f);
        _coreCard = Surface(Hex(0x362B2D), Hex(0xC49A58), 5, 1, .008f);
        _card.hover.background = Texture(Hex(0x3B3033), Gold, 5, 1, .008f);
        _coreCard.hover.background = _card.hover.background;
        _button = Surface(Hex(0x30282C), Hex(0x6E5746), 4, 1, .006f);
        _button.font = _bodyFont;
        _button.fontSize = 12;
        _button.alignment = TextAnchor.MiddleCenter;
        _button.normal.textColor = Muted;
        _button.hover.background = Texture(Hex(0x49382F), Hex(0xC6A16B), 4, 1, .006f);
        _button.hover.textColor = Ivory;
        _button.active.background = Texture(Hex(0x392D2B), Gold, 4, 1, 0);
        _button.active.textColor = Gold;
        _selectedButton = new GUIStyle(_button);
        _selectedButton.normal.background = Texture(Hex(0x4A372D), Hex(0xD0A76A), 4, 1, .008f);
        _selectedButton.normal.textColor = Ivory;
        _smallButton = new GUIStyle(_button) { fontSize = 11 };
        _smallButton.wordWrap = true;
        _windowStyle = new GUIStyle { padding = new RectOffset(), border = new RectOffset() };
        _scrollTrack = Surface(Hex(0x211D26), Hex(0x211D26), 2, 0, 0);
        _scrollThumb = Surface(Hex(0x8C704D), Hex(0xB39360), 2, 0, 0);
        _scrollTrack.padding = new RectOffset();
        _scrollThumb.padding = new RectOffset();
        _scrollThumb.fixedWidth = 6;
        _diamond = CreateDiamond();

        _blocker = new GameObject("KarnokAdvisor.PointerShield", typeof(Canvas), typeof(GraphicRaycaster));
        _blocker.transform.SetParent(parent, false);
        var canvas = _blocker.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 32000;
        var graphic = new GameObject("PanelHitArea", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        graphic.transform.SetParent(_blocker.transform, false);
        graphic.GetComponent<Image>().color = Color.clear;
        graphic.GetComponent<Image>().raycastTarget = true;
        _blockerRect = graphic.GetComponent<RectTransform>();
        _blockerRect.anchorMin = _blockerRect.anchorMax = new Vector2(0, 1);
        _blockerRect.pivot = new Vector2(0, 1);
        _blocker.SetActive(false);
    }

    internal void PumpArt() => _art.Pump();

    internal bool Draw(bool visible, JObject? snapshot, JObject? document, string message, Action refresh)
    {
        _visible = visible;
        _reference = document?["reference"] as JObject;
        _art.UseCatalog((string?)_reference?["catalog_digest"] ?? "");
        _live = ReferenceViewState.IsLive(document, snapshot, DateTimeOffset.UtcNow);
        _message = message;
        _refresh = refresh;
        _blocker.SetActive(visible);
        if (!visible) return false;
        if (_reference != null) _selection.Synchronize(_reference, _live);
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape && _selection.CardId.Length > 0)
        {
            _selection.CloseCard();
            Event.current.Use();
        }
        _scale = Mathf.Clamp(Screen.height / 1080f, .78f, 1.5f);
        var screenWidth = Screen.width / _scale;
        var screenHeight = Screen.height / _scale;
        var width = Mathf.Min(_compact ? 356 : 504, screenWidth - 20);
        var height = _compact ? 60 : Mathf.Min(820, screenHeight - 40);
        if (!_initialized)
        {
            _window = new Rect(screenWidth - width - 24, 54, width, height);
            _initialized = true;
        }
        _window.x += _window.width - width;
        _window.width = width;
        _window.height = height;
        _window.x = Mathf.Clamp(_window.x, 10, Math.Max(10, screenWidth - width - 10));
        _window.y = Mathf.Clamp(_window.y, 10, Math.Max(10, screenHeight - height - 10));
        var previousMatrix = GUI.matrix;
        var previousDepth = GUI.depth;
        var previousColor = GUI.color;
        var previousBackground = GUI.backgroundColor;
        var previousContent = GUI.contentColor;
        var previousEnabled = GUI.enabled;
        GUI.depth = 0;
        GUI.color = GUI.backgroundColor = GUI.contentColor = Color.white;
        GUI.enabled = true;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scale, _scale, 1));
        try
        {
            Fill(new Rect(_window.x + 4, _window.y + 7, width, height), new Color(0, 0, 0, .25f));
            _window = GUI.Window(914270, _window, DrawWindow, GUIContent.none, _windowStyle);
        }
        finally
        {
            GUI.matrix = previousMatrix;
            GUI.depth = previousDepth;
            GUI.color = previousColor;
            GUI.backgroundColor = previousBackground;
            GUI.contentColor = previousContent;
            GUI.enabled = previousEnabled;
        }
        _blockerRect.anchoredPosition = new Vector2(_window.x * _scale, -_window.y * _scale);
        _blockerRect.sizeDelta = new Vector2(_window.width * _scale, _window.height * _scale);
        if (!_visible) _blocker.SetActive(false);
        return _visible;
    }

    private void DrawWindow(int id)
    {
        GUI.Box(new Rect(0, 0, Width, _window.height), GUIContent.none, _frame);
        Rule(22, 5, Width - 44, Hex(0xB68D52));
        Rule(22, _window.height - 6, Width - 44, Hex(0x71563F));
        foreach (var p in new[] { new Vector2(10, 10), new Vector2(Width - 10, 10), new Vector2(10, _window.height - 10), new Vector2(Width - 10, _window.height - 10) })
            Diamond(p, 3, Gold);
        if (_compact) { DrawCompact(); return; }
        Label(new Rect(Padding, 16, 178, 31), "卡诺克阵容", _title, Ivory);
        Label(new Rect(203, 25, Width - 323, 20), _live ? "实时持有" : "持有未核对", _small, _live ? Green : Amber);
        if (GUI.Button(new Rect(Width - 112, 20, 64, 27), "收起", _smallButton)) _compact = true;
        if (GUI.Button(new Rect(Width - 39, 20, 22, 27), "×", _smallButton)) _visible = false;
        if (GUI.Button(new Rect(Padding, 56, 106, 25), _selection.MatchOwnedLabel(_reference, _live), _smallButton)) _selection.MatchOwned();
        if (GUI.Button(new Rect(Padding + 114, 56, 106, 25), (_selection.FollowDay ? "◆ " : "") + "跟随天数", _smallButton)) _selection.MatchDay();
        if (GUI.Button(new Rect(Width - 110, 56, 90, 25), "F10 同步", _smallButton)) _refresh?.Invoke();
        var routes = _reference?["routes"] as JArray;
        if (_reference == null || routes == null || routes.Count == 0)
        {
            Label(new Rect(Padding, 108, Width - Padding * 2, 40), "正在加载阵容资料", _body, Muted);
            Label(new Rect(Padding, 156, Width - Padding * 2, 100), _message, _muted, Amber);
            GUI.DragWindow(new Rect(0, 0, Width - 125, 50));
            return;
        }
        DrawRoutes(routes);
        DrawStages();
        _selection.Synchronize(_reference, _live);
        var route = _selection.Route(_reference);
        var stage = route == null ? null : _selection.Stage(route);
        if (route != null && stage != null)
        {
            Label(new Rect(Padding, 212, Width - 181, 29), Value(route["name"]), _heading, Gold);
            var slots = (int?)stage["base_slots"];
            var unlocked = _live ? (int?)_reference["unlocked_slots"] : null;
            Badge(new Rect(Width - 153, 212, 133, 27), "基础 " + Value(stage["base_slots"]) + " 格" + (unlocked.HasValue ? " / " + unlocked + " 格" : ""),
                slots.HasValue && slots > (unlocked ?? 10) ? Amber : Muted);
            var top = 250f;
            if ((bool?)_reference["verified"] != true)
            {
                Label(new Rect(Padding, top, Width - Padding * 2, 23), Value(_reference["version_note"]), _small, Amber);
                top += 28;
            }
            var contentId = _selection.RouteId + "/" + _selection.StageKey + "/" + _selection.CardId;
            if (_contentId != contentId) { _scroll = Vector2.zero; _contentId = contentId; }
            var viewport = new Rect(Padding, top, Width - Padding * 2, Math.Max(100, _window.height - top - 20));
            var card = _selection.Card(stage);
            if (card != null) DrawDetail(viewport, card);
            else DrawBuild(viewport, route, stage);
        }
        GUI.DragWindow(new Rect(0, 0, Width - 125, 50));
    }

    private void DrawCompact()
    {
        var route = _reference == null ? null : _selection.Route(_reference);
        var stage = route == null ? null : _selection.Stage(route);
        Label(new Rect(18, 10, Width - 130, 23), Value(route?["name"]), _tileTitle, Gold);
        Label(new Rect(18, 34, Width - 130, 18), Value(stage?["label"]) + " · " + (_live ? "实时持有" : "持有未核对"), _small, _live ? Green : Amber);
        if (GUI.Button(new Rect(Width - 101, 17, 61, 27), "展开", _smallButton)) _compact = false;
        if (GUI.Button(new Rect(Width - 32, 17, 20, 27), "×", _smallButton)) _visible = false;
        GUI.DragWindow(new Rect(0, 0, Width - 108, 60));
    }

    private void DrawRoutes(JArray routes)
    {
        const float gap = 6;
        var width = (Width - Padding * 2 - gap * 3) / 4;
        for (var n = 0; n < routes.Count; n++)
        {
            var route = routes[n];
            var id = (string?)route["id"] ?? "";
            var rect = new Rect(Padding + n % 4 * (width + gap), 90 + n / 4 * 38, width, 32);
            var style = id == _selection.RouteId ? _selectedButton : _button;
            if (GUI.Button(rect, GUIContent.none, style)) _selection.SelectRoute(id);
            DrawArt(new Rect(rect.x + 4, rect.y + 4, 24, 24), route["cover"]);
            var name = Value(route["name"]).Replace(" · ", "");
            Label(new Rect(rect.x + 32, rect.y + 4, width - 35, 24), name, _small, id == _selection.RouteId ? Ivory : Muted, TextAnchor.MiddleCenter);
            if (_live && (int?)route["core_count"] > 0) Diamond(new Vector2(rect.xMax - 5, rect.y + 5), 3, Green);
        }
    }

    private void DrawStages()
    {
        var keys = new[] { "early", "mid", "late" };
        var labels = new[] { "前期  D1–3", "中期  D4–7", "后期  D8+" };
        var width = (Width - Padding * 2 - 12) / 3;
        for (var n = 0; n < keys.Length; n++)
        {
            if (GUI.Button(new Rect(Padding + n * (width + 6), 170, width, 30),
                (_live && (string?)_reference?["stage"] == keys[n] ? "◆ " : "") + labels[n],
                _selection.StageKey == keys[n] ? _selectedButton : _button)) _selection.SelectStage(keys[n]);
        }
    }

    private void DrawBuild(Rect viewport, JObject route, JObject stage)
    {
        var width = viewport.width - 14;
        var textWidth = width - 24;
        var columns = width >= 400 ? 4 : 3;
        var tileWidth = (width - (columns - 1) * 8) / columns;
        var baseItems = ReferenceViewState.BaseCards(stage);
        var options = ReferenceViewState.OptionalCards(stage);
        var baseRows = GridRows(baseItems, tileWidth, columns);
        var optionRows = GridRows(options, tileWidth, columns);
        var planHeight = TextHeight(_muted, Value(stage["plan"]), textWidth) + 16;
        var positioning = stage["positioning"] as JObject;
        var positionNotes = positioning == null ? new List<string> { "站位资料待同步" } : Strings(positioning["notes"]);
        var hasPositioning = (bool?)positioning?["available"] == true;
        var positionBlockHeight = hasPositioning ? Math.Max(56, baseItems.Max(card => TextHeight(_tileMeta, Value(card["name"]), width * ((int?)card["span"] ?? 1) / 10 - 8) + 25)) : 56;
        var positionHeight = (hasPositioning ? positionBlockHeight + 34 : 28f) + positionNotes.Sum(note => TextHeight(_tileMeta, note, width - 12) + 7) + 8;
        var sections = new List<(string Key, string Title, bool Expanded, List<(string Text, Color Color, string Url)> Lines)>();
        var strategy = new List<(string Text, Color Color, string Url)> {
            ("转型：" + Value(route["pivot"]), Ivory, ""),
            ("技能：" + Value(route["skills"]), Ivory, ""),
            (Value(route["caution"]), Amber, "")
        };
        sections.Add(("strategy", "转型与技能", _open.Contains("strategy"), strategy));
        var sources = (route["sources"] as JArray ?? new JArray()).OfType<JObject>()
            .Select(source => (Value(source["title"]), Gold, (string?)source["url"] ?? "")).ToList();
        sections.Add(("sources", "攻略来源", _open.Contains("sources"), sources));
        float Height(string text) => TextHeight(_muted, text, textWidth) + 10;
        var contentHeight = 60 + positionHeight + baseRows.Sum(h => h + 8) + optionRows.Sum(h => h + 8) + planHeight
            + sections.Sum(s => 34 + (s.Expanded ? s.Lines.Sum(line => Height(line.Text)) : 0)) + 12;
        BeginScroll(viewport, width, contentHeight);
        var y = DrawPositioning(width, stage, positioning, positionNotes, positionBlockHeight);
        Label(new Rect(0, y, 140, 23), "基础组件", _body, Gold);
        var ownedCount = ReferenceViewState.OwnedBaseCount(stage, _live);
        if (ownedCount.HasValue) Label(new Rect(width - 200, y + 3, 200, 20), "基础在场 " + ownedCount + " / " + baseItems.Count, _small, Green, TextAnchor.UpperRight);
        y = DrawGrid(y + 30, baseItems, baseRows, tileWidth, columns, viewport.height);
        Label(new Rect(12, y, textWidth, planHeight - 8), Value(stage["plan"]), _muted, Ivory);
        y += planHeight;
        Label(new Rect(0, y, 140, 23), "可选组件", _body, Gold);
        Label(new Rect(width - 140, y + 3, 140, 20), "按需选用", _small, Muted, TextAnchor.UpperRight);
        y = DrawGrid(y + 30, options, optionRows, tileWidth, columns, viewport.height);
        foreach (var section in sections)
        {
            if (GUI.Button(new Rect(0, y, width, 28), (section.Expanded ? "−  " : "+  ") + section.Title, _smallButton))
            {
                if (!_open.Remove(section.Key)) _open.Add(section.Key);
            }
            y += 34;
            if (!section.Expanded) continue;
            foreach (var line in section.Lines)
            {
                var h = Height(line.Text);
                DrawLine(line, new Rect(12, y, textWidth, h));
                y += h;
            }
        }
        EndScroll(viewport, contentHeight);
    }

    private float DrawPositioning(float width, JObject stage, JObject? positioning, List<string> notes, float blockHeight)
    {
        Label(new Rect(0, 0, width - 150, 23), "参考站位  左 → 右", _body, Gold);
        var available = (bool?)positioning?["available"] == true;
        if (available)
        {
            var slots = (int?)positioning?["slots"];
            var unlocked = _live ? (int?)_reference?["unlocked_slots"] : null;
            Label(new Rect(width - 145, 3, 145, 20), "站位 " + Value(positioning?["slots"]) + " 格（含间隔）", _small,
                slots > (unlocked ?? 10) ? Amber : Muted, TextAnchor.UpperRight);
            var unit = width / 10;
            foreach (var block in positioning?["blocks"] as JArray ?? new JArray())
            {
                var start = (int?)block["start"] ?? -1;
                var span = (int?)block["span"] ?? 0;
                if (start < 0 || span < 1 || start + span > 10) continue;
                var rect = new Rect(start * unit + 1, 28, span * unit - 2, blockHeight);
                var card = ReferenceViewState.PositionCard(stage, block);
                if (card != null)
                {
                    if (GUI.Button(rect, GUIContent.none, (bool?)card["core"] == true ? _coreCard : _card))
                        _selection.SelectCard((string?)card["template_id"] ?? "");
                    Label(new Rect(rect.x + 3, rect.y + 5, rect.width - 6, blockHeight - 25), Value(card["name"]), _tileMeta, Ivory, TextAnchor.UpperCenter);
                    Label(new Rect(rect.x + 3, rect.yMax - 19, rect.width - 6, 15), span + "格", _tileMeta, Muted, TextAnchor.UpperCenter);
                }
                else
                {
                    var gap = (string?)block["kind"] == "gap";
                    GUI.Box(rect, GUIContent.none, _card);
                    Label(new Rect(rect.x + 3, rect.y + 10, rect.width - 6, blockHeight - 20), (gap ? "空" : "余") + span + "格", _tileMeta, gap ? Amber : Muted, TextAnchor.MiddleCenter);
                }
            }
        }
        var y = available ? blockHeight + 34 : 28f;
        foreach (var note in notes)
        {
            var height = TextHeight(_tileMeta, note, width - 12) + 7;
            Label(new Rect(6, y, width - 12, height), note, _tileMeta, available ? Muted : Amber);
            y += height;
        }
        return y + 8;
    }

    private float[] GridRows(JArray items, float tileWidth, int columns)
    {
        var rows = new float[(items.Count + columns - 1) / columns];
        for (var n = 0; n < items.Count; n++) rows[n / columns] = Math.Max(rows[n / columns], CardHeight(items[n], tileWidth));
        return rows;
    }

    private float DrawGrid(float y, JArray items, float[] rows, float tileWidth, int columns, float viewportHeight)
    {
        for (var row = 0; row < rows.Length; row++)
        {
            for (var column = 0; column < columns && row * columns + column < items.Count; column++)
            {
                var tile = new Rect(column * (tileWidth + 8), y, tileWidth, rows[row]);
                if (tile.yMax >= _scroll.y && tile.y <= _scroll.y + viewportHeight) DrawCard(tile, items[row * columns + column]);
            }
            y += rows[row] + 8;
        }
        return y;
    }

    private float CardHeight(JToken item, float width) => 156 + TagHeaderHeight(item, width - 14) + TextHeight(_tileMeta, CardUse(item), width - 14)
        + (ReplacementNames(item).Length > 0 ? TextHeight(_tileMeta, "换：" + ReplacementNames(item), width - 14) + 4 : 0);

    private float TagHeaderHeight(JToken item, float width) => TextHeight(_tileMeta, ReferenceViewState.TagText(item), width) + 4;

    private static string CardUse(JToken item) => (string?)item["group"] == "optional" ? Value(item["use"]) : Value(item["role"]);
    private static string ReplacementNames(JToken item) => string.Join("、", (item["replaces"] as JArray ?? new JArray()).Select(card => Value(card["name"])));

    private void DrawLine((string Text, Color Color, string Url) line, Rect rect)
    {
        if (line.Url.Length > 0)
        {
            if (GUI.Button(rect, line.Text, _smallButton)) Application.OpenURL(line.Url);
        }
        else Label(rect, line.Text, _muted, line.Color);
    }

    private void DrawCard(Rect tile, JToken item)
    {
        var core = (bool?)item["core"] == true;
        if (GUI.Button(tile, GUIContent.none, core ? _coreCard : _card)) _selection.SelectCard((string?)item["template_id"] ?? "");
        var tagHeight = TagHeaderHeight(item, tile.width - 14);
        Label(new Rect(tile.x + 7, tile.y + 7, tile.width - 14, tagHeight - 4), ReferenceViewState.TagText(item), _tileMeta, Muted, TextAnchor.UpperCenter);
        var art = new Rect(tile.x + 7, tile.y + 7 + tagHeight, tile.width - 14, 80);
        DrawArt(art, item);
        var coreLabel = (string?)item["core_label"] ?? "";
        if (coreLabel.Length > 0) Badge(new Rect(art.x + 2, art.y + 2, coreLabel.Length * 11 + 8, 18), coreLabel, Gold, true);
        var owned = _live ? (string?)item["owned"] : null;
        if (owned == "场上" || owned == "仓库") Badge(new Rect(art.xMax - 35, art.yMax - 20, 33, 18), owned, owned == "场上" ? Green : Amber, true);
        Label(new Rect(tile.x + 7, tile.y + 94 + tagHeight, tile.width - 14, 30), Value(item["name"]), _tileTitle, Ivory, TextAnchor.UpperCenter);
        Label(new Rect(tile.x + 7, tile.y + 126 + tagHeight, tile.width - 14, 17), Value(item["starting_tier"]) + "起 · " + Value(item["span"]) + "格", _tileMeta, Muted, TextAnchor.UpperCenter);
        var useHeight = TextHeight(_tileMeta, CardUse(item), tile.width - 14);
        Label(new Rect(tile.x + 7, tile.y + 149 + tagHeight, tile.width - 14, useHeight), CardUse(item), _tileMeta, core ? Gold : Cyan, TextAnchor.UpperCenter);
        var replaces = ReplacementNames(item);
        if (replaces.Length > 0) Label(new Rect(tile.x + 7, tile.y + 153 + tagHeight + useHeight, tile.width - 14, tile.height - 155 - tagHeight - useHeight), "换：" + replaces, _tileMeta, Amber, TextAnchor.UpperCenter);
    }

    private void DrawDetail(Rect viewport, JObject card)
    {
        var width = viewport.width - 14;
        var textWidth = width - 24;
        var effects = Strings(card["effects"]);
        if (effects.Count == 0) effects.Add("本机未提供可用说明，请以游戏卡面为准。");
        var details = new List<(string Text, Color Color, string Url)>();
        var optional = (string?)card["group"] == "optional";
        if (optional)
        {
            details.Add(("可选 · " + Value(card["use"]), Cyan, ""));
            details.Add((Value(card["note"]), Ivory, ""));
            details.Add(("站位：" + Value(card["position_note"]), Cyan, ""));
            var replaces = ReplacementNames(card);
            if (replaces.Length > 0) details.Add(("替换基础组件：" + replaces, Gold, ""));
            var slots = (int?)card["result_slots"];
            var available = _live ? (int?)_reference?["unlocked_slots"] : null;
            var space = (replaces.Length > 0 ? "替换后基础 " : "基础＋此牌 ") + Value(card["result_slots"]) + " 格";
            if (available.HasValue) space += " · 当前可用 " + available + " 格";
            if (slots > 10) space += " · 超上限 " + (slots - 10) + " 格，需再移除其他基础组件";
            details.Add((space, slots > (available ?? 10) ? Amber : Muted, ""));
            var positionSlots = (int?)card["result_position_slots"];
            if (positionSlots.HasValue && positionSlots != slots)
                details.Add(("组件＋保留间隔 " + positionSlots + " 格", positionSlots > (available ?? 10) ? Amber : Muted, ""));
        }
        else details.Add(("基础组件" + ((string?)card["core_label"] is string label && label.Length > 0 ? " · " + label : ""), Gold, ""));
        var detailHeights = details.Select(line => TextHeight(_muted, line.Text, textWidth) + 10).ToArray();
        var heights = effects.Select(effect => TextHeight(_body, effect, textWidth) + 12).ToArray();
        var tagHeight = TagHeaderHeight(card, 142);
        var total = 272 + tagHeight + detailHeights.Sum() + heights.Sum();
        BeginScroll(viewport, width, total);
        if (GUI.Button(new Rect(0, 0, width, 28), "‹  返回阵容", _smallButton)) _selection.CloseCard();
        Label(new Rect(7, 42, 142, tagHeight - 4), ReferenceViewState.TagText(card), _tileMeta, Muted, TextAnchor.UpperCenter);
        DrawArt(new Rect(7, 42 + tagHeight, 142, 142), card);
        Label(new Rect(165, 44 + tagHeight, width - 172, 55), Value(card["name"]), _heading, Gold);
        Label(new Rect(165, 106 + tagHeight, width - 172, 34), Value(card["role"]), _muted, Cyan);
        Label(new Rect(165, 146 + tagHeight, width - 172, 30), Value(card["starting_tier"]) + "起步 · " + Value(card["span"]) + "格", _muted, Muted);
        var owned = _live ? Value(card["owned"]) : "持有状态未核对";
        if (_live && (string?)card["owned_tier"] is string tier && tier.Length > 0) owned += " · 当前" + tier;
        Badge(new Rect(0, 196 + tagHeight, width, 28), owned, owned.StartsWith("场上", StringComparison.Ordinal) ? Green : Muted);
        var y = 234f + tagHeight;
        for (var n = 0; n < details.Count; n++)
        {
            DrawLine(details[n], new Rect(12, y, textWidth, detailHeights[n]));
            y += detailHeights[n];
        }
        Label(new Rect(12, y, textWidth, 22), "本机卡面 · " + Value(card["starting_tier"]) + "品质参考", _muted, Gold);
        y += 30;
        for (var n = 0; n < effects.Count; n++)
        {
            Label(new Rect(12, y, textWidth, heights[n]), effects[n], _body, Ivory);
            y += heights[n];
        }
        EndScroll(viewport, total);
    }

    private void DrawArt(Rect rect, JToken? item)
    {
        Fill(rect, Hex(0x1B1921));
        var texture = _art.Get((string?)item?["art_key"] ?? "", Event.current.type == EventType.Repaint, out var unavailable);
        // The alpha channel is the native shader's FX mask, not painting transparency.
        if (texture != null) GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, false);
        else if (rect.width > 40) Label(rect, unavailable ? "暂无卡图" : "加载卡图…", _tileMeta, Muted, TextAnchor.MiddleCenter);
    }

    private void BeginScroll(Rect viewport, float width, float contentHeight)
    {
        _scroll.y = Mathf.Clamp(_scroll.y, 0, Math.Max(0, contentHeight - viewport.height));
        _scroll = GUI.BeginScrollView(viewport, _scroll, new Rect(0, 0, width, contentHeight), false, false, GUIStyle.none, GUIStyle.none);
    }

    private void EndScroll(Rect viewport, float contentHeight)
    {
        GUI.EndScrollView();
        var limit = Math.Max(0, contentHeight - viewport.height);
        if (limit <= 0) return;
        _scrollThumb.fixedHeight = Math.Max(30, viewport.height * viewport.height / contentHeight);
        _scroll.y = GUI.VerticalSlider(new Rect(viewport.xMax - 6, viewport.y, 6, viewport.height), _scroll.y, 0, limit, _scrollTrack, _scrollThumb);
    }



    private void Badge(Rect rect, string text, Color color, bool opaque = false)
    {
        Fill(rect, opaque ? Hex(0x29232A) : new Color(color.r, color.g, color.b, .12f));
        Label(rect, text, _small, color, TextAnchor.MiddleCenter);
    }

    private static GUIStyle TextStyle(Font font, int size, Color color, bool wrap, FontStyle style = FontStyle.Normal) =>
        new GUIStyle { font = font, fontSize = size, fontStyle = style, wordWrap = wrap, richText = false,
            alignment = TextAnchor.UpperLeft, clipping = TextClipping.Clip, normal = { textColor = color } };

    private static void Label(Rect rect, string text, GUIStyle style, Color color, TextAnchor alignment = TextAnchor.UpperLeft)
    {
        var oldColor = style.normal.textColor;
        var oldAlignment = style.alignment;
        style.normal.textColor = color;
        style.alignment = alignment;
        GUI.Label(rect, text, style);
        style.normal.textColor = oldColor;
        style.alignment = oldAlignment;
    }

    private static float TextHeight(GUIStyle style, string text, float width) => Math.Max(style.lineHeight + 2, style.CalcHeight(new GUIContent(text), Math.Max(20, width)) + 2);
    private static List<string> Strings(JToken? token) => token is JArray array ? array.Select(t => t.ToString()).Where(s => s.Length > 0).ToList() : new List<string>();
    private static string Value(JToken? value) => value == null || value.Type == JTokenType.Null ? "—" : value.ToString();
    private static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
    private static void Fill(Rect rect, Color color) { var previous = GUI.color; GUI.color = color; GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = previous; }
    private static void Rule(float x, float y, float width, Color color) => Fill(new Rect(x, y, width, 1), color);
    private void Diamond(Vector2 center, float radius, Color color) { var previous = GUI.color; GUI.color = color; GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2, radius * 2), _diamond); GUI.color = previous; }

    private Texture2D CreateDiamond()
    {
        const int size = 48;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "KarnokAdvisor.Emblem", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++) pixels[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(size / 2f - Math.Abs(x + .5f - size / 2f) - Math.Abs(y + .5f - size / 2f)));
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        _textures.Add(texture);
        return texture;
    }

    private GUIStyle Surface(Color fill, Color border, int radius, int thickness, float grain) =>
        new GUIStyle { normal = { background = Texture(fill, border, radius, thickness, grain) }, border = new RectOffset(9, 9, 9, 9), padding = new RectOffset(12, 12, 12, 12) };

    private Texture2D Texture(Color fill, Color border, int radius, int thickness, float grain, int size = 32)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "KarnokAdvisor.Surface", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[size * size];
        var noise = new System.Random(317);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = Math.Max(radius - x - .5f, x - (size - radius) + .5f);
            var dy = Math.Max(radius - y - .5f, y - (size - radius) + .5f);
            var distance = Math.Sqrt(Math.Max(0, dx) * Math.Max(0, dx) + Math.Max(0, dy) * Math.Max(0, dy));
            var alpha = Mathf.Clamp01(radius - (float)distance + .5f);
            var edge = Math.Min(Math.Min(x, size - x - 1), Math.Min(y, size - y - 1));
            var color = edge < thickness || distance > radius - thickness ? border : fill;
            var shade = (float)(noise.NextDouble() - .5) * grain;
            pixels[y * size + x] = new Color(color.r + shade, color.g + shade, color.b + shade, alpha * color.a);
        }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        _textures.Add(texture);
        return texture;
    }

    public void Dispose()
    {
        _art.Dispose();
        UnityEngine.Object.Destroy(_blocker);
        UnityEngine.Object.Destroy(_bodyFont);
        UnityEngine.Object.Destroy(_titleFont);
        foreach (var texture in _textures) UnityEngine.Object.Destroy(texture);
    }
}
