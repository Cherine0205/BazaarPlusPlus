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
    private const float CardHeight = 208;
    private static readonly Color Gold = Hex(0xD8B16B);
    private static readonly Color Ivory = Hex(0xF0E1C6);
    private static readonly Color Muted = Hex(0xAF9F92);
    private static readonly Color Green = Hex(0xA2C49A);
    private static readonly Color Cyan = Hex(0x83C5CB);
    private static readonly Color Amber = Hex(0xE0B27B);
    private static readonly Color Line = Hex(0x655142);
    private readonly List<Texture2D> _textures = new List<Texture2D>();
    private readonly HashSet<string> _open = new HashSet<string>();
    private readonly ReferenceViewState _selection = new ReferenceViewState();
    private readonly CardArtCache _art;
    private readonly Font _bodyFont;
    private readonly Font _titleFont;
    private readonly GameObject _blocker;
    private readonly RectTransform _blockerRect;
    private readonly GUIStyle _frame, _surface, _card, _coreCard, _button, _selectedButton, _smallButton;
    private readonly GUIStyle _body, _muted, _small, _title, _heading, _tileTitle, _tileMeta, _number;
    private readonly GUIStyle _windowStyle, _scrollTrack, _scrollThumb;
    private readonly Texture2D _diamond;
    private Rect _window;
    private Vector2 _scroll;
    private bool _initialized, _visible, _compact, _live;
    private float _scale = 1;
    private string _contentId = "";
    private string _message = "";
    private JObject? _snapshot, _reference;
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
        _number = TextStyle(_bodyFont, 18, Ivory, false, FontStyle.Bold);
        _frame = Surface(Hex(0x211D25), Hex(0xA47C48), 7, 2, .014f);
        _surface = Surface(Hex(0x2B252B), Hex(0x5E4D46), 5, 1, .008f);
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
        _snapshot = snapshot;
        _reference = document?["reference"] as JObject;
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
        var height = _compact ? 72 : Mathf.Min(844, screenHeight - 40);
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
        Diamond(new Vector2(38, 40), 21, Hex(0x896741));
        Diamond(new Vector2(38, 40), 18, Hex(0x30252B));
        Label(new Rect(19, 20, 38, 37), "K", _title, Gold, TextAnchor.MiddleCenter);
        Label(new Rect(71, 19, Width - 204, 31), "卡诺克 · 阵容手册", _title, Ivory);
        Label(new Rect(73, 53, Width - 151, 20), _live ? "已同步持有 · 自由规划你的路线" : "自由浏览 · 持有状态尚未核对", _small, _live ? Green : Muted);
        if (GUI.Button(new Rect(Width - 112, 22, 64, 27), "收起", _smallButton)) _compact = true;
        if (GUI.Button(new Rect(Width - 39, 22, 22, 27), "×", _smallButton)) _visible = false;
        DrawStats();
        var routes = _reference?["routes"] as JArray;
        if (_reference == null || routes == null || routes.Count == 0)
        {
            Label(new Rect(Padding + 12, 157, Width - Padding * 2 - 24, 100), "阵容资料加载中\n本地资料就绪后，七条路线会在这里展开。", _body, Muted);
            Label(new Rect(Padding + 12, 279, Width - Padding * 2 - 24, 80), _message, _muted, Amber);
            DrawFooter();
            GUI.DragWindow(new Rect(0, 0, Width - 125, 76));
            return;
        }
        Label(new Rect(Padding, 137, 150, 23), "选择流派", _muted, Gold);
        if (GUI.Button(new Rect(Width - 212, 134, 93, 25), (_selection.FollowOwned ? "◆ " : "") + "匹配持有", _smallButton)) _selection.MatchOwned();
        if (GUI.Button(new Rect(Width - 111, 134, 91, 25), (_selection.FollowDay ? "◆ " : "") + "跟随天数", _smallButton)) _selection.MatchDay();
        DrawRoutes(routes);
        DrawStages();
        _selection.Synchronize(_reference, _live);
        var route = _selection.Route(_reference);
        var stage = route == null ? null : _selection.Stage(route);
        if (route != null && stage != null)
        {
            Label(new Rect(Padding, 298, Width - 181, 29), Value(route["name"]), _heading, Gold);
            var slots = (int?)stage["slots"];
            var unlocked = _live ? (int?)_reference["unlocked_slots"] : null;
            Badge(new Rect(Width - 153, 298, 133, 27), "组合 " + Value(stage["slots"]) + " 格" + (unlocked.HasValue ? " / " + unlocked + " 格" : ""),
                slots.HasValue && unlocked.HasValue && slots > unlocked ? Amber : Muted);
            var items = stage["items"] as JArray ?? new JArray();
            var count = items.Count(c => (string?)c["owned"] == "场上");
            var reserve = items.Count(c => (string?)c["owned"] == "仓库");
            Label(new Rect(Padding, 331, Width - Padding * 2, 21), _live
                ? "在场 " + count + " / " + items.Count + "  ·  仓库 " + reserve + "  ·  图卡顺序不代表摆位"
                : "参考组合 " + items.Count + " 件  ·  图卡顺序不代表摆位", _small, Muted);
            var contentId = _selection.RouteId + "/" + _selection.StageKey + "/" + _selection.CardId;
            if (_contentId != contentId) { _scroll = Vector2.zero; _contentId = contentId; }
            var viewport = new Rect(Padding, 358, Width - Padding * 2, Math.Max(100, _window.height - 412));
            var card = _selection.Card(stage);
            if (card != null) DrawDetail(viewport, card);
            else DrawBuild(viewport, route, stage, items);
        }
        DrawFooter();
        GUI.DragWindow(new Rect(0, 0, Width - 125, 76));
    }

    private void DrawCompact()
    {
        Diamond(new Vector2(28, 35), 15, Gold);
        Diamond(new Vector2(28, 35), 12, Hex(0x2B2429));
        Label(new Rect(48, 12, Width - 160, 25), "卡诺克 · 阵容手册", _tileTitle, Ivory);
        var route = _reference == null ? null : _selection.Route(_reference);
        Label(new Rect(48, 39, Width - 160, 19), (_live ? "D" + Value(_snapshot?["day"]) + " · " : "参考 · ") + Value(route?["name"]), _small, _live ? Green : Muted);
        if (GUI.Button(new Rect(Width - 101, 22, 61, 27), "展开", _smallButton)) _compact = false;
        if (GUI.Button(new Rect(Width - 32, 22, 20, 27), "×", _smallButton)) _visible = false;
        GUI.DragWindow(new Rect(0, 0, Width - 108, 72));
    }

    private void DrawStats()
    {
        var strip = new Rect(Padding, 82, Width - Padding * 2, 44);
        GUI.Box(strip, GUIContent.none, _surface);
        var cell = strip.width / 4;
        var labels = new[] { "日程", "金币", "等级", "可用格" };
        var values = new[] { _live ? "D" + Value(_snapshot?["day"]) : "—", _live ? Value(_snapshot?["player_attributes"]?["Gold"]) : "—",
            _live ? Value(_snapshot?["player_attributes"]?["Level"]) : "—", _live ? Value(_reference?["unlocked_slots"]) : "—" };
        for (var n = 0; n < labels.Length; n++)
        {
            if (n > 0) Fill(new Rect(strip.x + cell * n, strip.y + 9, 1, 26), Line);
            Label(new Rect(strip.x + cell * n, strip.y + 2, cell, 17), labels[n], _small, Muted, TextAnchor.MiddleCenter);
            Label(new Rect(strip.x + cell * n, strip.y + 19, cell, 23), values[n], _number, n == 1 ? Gold : Ivory, TextAnchor.MiddleCenter);
        }
    }

    private void DrawRoutes(JArray routes)
    {
        const float gap = 6;
        var width = (Width - Padding * 2 - gap * 3) / 4;
        for (var n = 0; n < routes.Count; n++)
        {
            var route = routes[n];
            var id = (string?)route["id"] ?? "";
            var rect = new Rect(Padding + n % 4 * (width + gap), 168 + n / 4 * 38, width, 32);
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
            if (GUI.Button(new Rect(Padding + n * (width + 6), 252, width, 32),
                (_live && (string?)_reference?["stage"] == keys[n] ? "◆ " : "") + labels[n],
                _selection.StageKey == keys[n] ? _selectedButton : _button)) _selection.SelectStage(keys[n]);
        }
    }

    private void DrawBuild(Rect viewport, JObject route, JObject stage, JArray items)
    {
        var width = viewport.width - 14;
        var gridHeight = (float)Math.Ceiling(items.Count / 3f) * (CardHeight + 10);
        var lines = new List<(string Text, Color Color, string Url)>();
        void Add(string text, Color? color = null, string url = "") => lines.Add((text, color ?? Ivory, url));
        Add("阶段思路", Gold);
        Add(Value(stage["plan"]));
        var sections = new List<(string Key, string Title, bool Expanded, List<(string Text, Color Color, string Url)> Lines)>();
        foreach (var key in new[] { "ownership", "strategy", "sources" })
        {
            var content = new List<(string Text, Color Color, string Url)>();
            void Detail(string text, Color? color = null, string url = "") => content.Add((text, color ?? Ivory, url));
            if (key == "ownership")
            {
                Detail(_live ? "场上：" + JoinOr(route["owned_board"], "暂无此路线组件") : "持有状态未核对；进入卡诺克对局后自动更新。", _live ? Green : Amber);
                if (_live && Strings(route["owned_stash"]).Count > 0) Detail("仓库：" + JoinOr(route["owned_stash"], "无") + "（尚未在场）", Amber);
                if (_live && Strings(route["missing_roles"]).Count > 0) Detail("尚缺功能：" + JoinOr(route["missing_roles"], "无"), Amber);
                Detail("转型：" + Value(route["pivot"]));
            }
            else if (key == "strategy")
            {
                Detail(Value(route["summary"]));
                Detail("技能方向：" + Value(route["skills"]));
                Detail("注意条件：" + Value(route["caution"]), Amber);
            }
            else
            {
                Detail(Value(_reference?["stage_note"]), Muted);
                Detail(Value(_reference?["tier_note"]), Muted);
                Detail("复核 " + Value(_reference?["reviewed_at"]) + " · 补丁 " + Value(_reference?["reviewed_patch"]), Gold);
                Detail(Value(_reference?["version_note"]), (bool?)_reference?["verified"] == true ? Muted : Amber);
                foreach (var source in (route["sources"] as JArray ?? new JArray()).OfType<JObject>())
                    Detail(Value(source["title"]) + " · " + Value(source["date"]), Gold, (string?)source["url"] ?? "");
            }
            sections.Add((key, key == "ownership" ? "当前持有与转型" : key == "strategy" ? "技能与注意条件" : "资料与版本", _open.Contains(key), content));
        }
        var textWidth = width - 24;
        float Height(string text) => TextHeight(_muted, text, textWidth) + 10;
        var contentHeight = gridHeight + lines.Sum(l => Height(l.Text)) + sections.Sum(s => 34 + (s.Expanded ? s.Lines.Sum(l => Height(l.Text)) : 0)) + 18;
        BeginScroll(viewport, width, contentHeight);
        var tileWidth = (width - 20) / 3;
        for (var n = 0; n < items.Count; n++)
        {
            var tile = new Rect(n % 3 * (tileWidth + 10), n / 3 * (CardHeight + 10), tileWidth, CardHeight);
            if (tile.yMax < _scroll.y || tile.y > _scroll.y + viewport.height) continue;
            DrawCard(tile, items[n]);
        }
        var y = gridHeight + 3;
        foreach (var line in lines) { var h = Height(line.Text); DrawLine(line, new Rect(12, y, textWidth, h)); y += h; }
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
        var art = new Rect(tile.x + 7, tile.y + 7, tile.width - 14, 106);
        DrawArt(art, item);
        if (core) Badge(new Rect(art.x + 3, art.y + 3, 33, 18), "核心", Gold, true);
        var owned = _live ? (string?)item["owned"] : null;
        if (owned == "场上" || owned == "仓库") Badge(new Rect(art.xMax - 36, art.yMax - 21, 33, 18), owned, owned == "场上" ? Green : Amber, true);
        Label(new Rect(tile.x + 7, tile.y + 120, tile.width - 14, 30), Value(item["name"]), _tileTitle, Ivory, TextAnchor.UpperCenter);
        Label(new Rect(tile.x + 7, tile.y + 152, tile.width - 14, 17), Value(item["starting_tier"]) + "起 · " + Value(item["span"]) + "格", _tileMeta, Muted, TextAnchor.UpperCenter);
        Label(new Rect(tile.x + 7, tile.y + 174, tile.width - 14, 28), Value(item["role"]), _tileMeta, core ? Gold : Cyan, TextAnchor.UpperCenter);
    }

    private void DrawDetail(Rect viewport, JObject card)
    {
        var width = viewport.width - 14;
        var textWidth = width - 24;
        var effects = Strings(card["effects"]);
        if (effects.Count == 0) effects.Add("本机未提供可用说明，请以游戏卡面为准。");
        var heights = effects.Select(e => TextHeight(_body, e, textWidth) + 12).ToArray();
        var total = 313 + heights.Sum();
        BeginScroll(viewport, width, total);
        if (GUI.Button(new Rect(0, 0, width, 28), "‹  返回当前阶段组合", _smallButton)) _selection.CloseCard();
        DrawArt(new Rect(7, 42, 142, 142), card);
        Label(new Rect(165, 44, width - 172, 55), Value(card["name"]), _heading, Gold);
        Label(new Rect(165, 106, width - 172, 34), Value(card["role"]), _muted, Cyan);
        Label(new Rect(165, 146, width - 172, 30), Value(card["starting_tier"]) + "起步 · " + Value(card["span"]) + "格", _muted, Muted);
        var owned = _live ? Value(card["owned"]) : "持有状态未核对";
        if (_live && (string?)card["owned_tier"] is string tier && tier.Length > 0) owned += " · 当前" + tier;
        Badge(new Rect(0, 196, width, 28), owned, owned.StartsWith("场上", StringComparison.Ordinal) ? Green : Muted);
        Label(new Rect(12, 232, textWidth, 22), "本机卡面 · " + Value(card["starting_tier"]) + "品质参考", _muted, Gold);
        var y = 262f;
        for (var n = 0; n < effects.Count; n++)
        {
            Label(new Rect(12, y, textWidth, heights[n]), effects[n], _body, Ivory);
            y += heights[n];
        }
        Label(new Rect(12, y + 8, textWidth, 35), "数值占位和附魔请以游戏内当前卡面为准。", _tileMeta, Muted);
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

    private void DrawFooter()
    {
        var y = _window.height - 43;
        Rule(Padding, y, Width - Padding * 2, Line);
        var status = !_live ? "本地参考 · 待同步持有" : _selection.CardId.Length > 0 ? "Esc 返回组合" : "点击卡牌看详情 · 下滑看思路";
        Label(new Rect(Padding, y + 13, Width - 221, 21), status, _small, _live ? Muted : Amber);
        if (GUI.Button(new Rect(Width - 193, y + 8, 78, 27), "F9 隐藏", _smallButton)) _visible = false;
        if (GUI.Button(new Rect(Width - 106, y + 8, 86, 27), "F10 同步", _smallButton)) _refresh?.Invoke();
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
    private static string JoinOr(JToken? token, string fallback) { var names = Strings(token); return names.Count > 0 ? string.Join("、", names) : fallback; }
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
