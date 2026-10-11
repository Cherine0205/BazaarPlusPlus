using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace KarnokAdvisor;

// The view owns its styles and local interactions; recommendation facts stay untouched.
internal sealed class AdvisorPanel : IDisposable
{
    private const float Width = 428;
    private const float Padding = 20;
    private static readonly Color Gold = Hex(0xD8B16B);
    private static readonly Color Ivory = Hex(0xF0E1C6);
    private static readonly Color Muted = Hex(0xAC9B8D);
    private static readonly Color Line = Hex(0x5D4B48);
    private static readonly Color Green = Hex(0x93BD91);
    private static readonly Color Cyan = Hex(0x77C7D5);
    private static readonly Color Amber = Hex(0xD8A775);
    private readonly List<Texture2D> _textures = new List<Texture2D>();
    private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _warnings = new HashSet<string>(StringComparer.Ordinal);
    private readonly Font _bodyFont;
    private readonly Font _titleFont;
    private readonly GameObject _blocker;
    private readonly RectTransform _blockerRect;
    private readonly GUIStyle _frame;
    private readonly GUIStyle _primary;
    private readonly GUIStyle _secondary;
    private readonly GUIStyle _button;
    private readonly GUIStyle _smallButton;
    private readonly GUIStyle _body;
    private readonly GUIStyle _muted;
    private readonly GUIStyle _small;
    private readonly GUIStyle _title;
    private readonly GUIStyle _cardTitle;
    private readonly GUIStyle _number;
    private readonly GUIStyle _windowStyle;
    private readonly GUIStyle _scrollTrack;
    private readonly GUIStyle _scrollThumb;
    private readonly Texture2D _coin;
    private readonly Texture2D _diamond;
    private Rect _window;
    private Vector2 _scroll;
    private Vector2 _guideScroll;
    private bool _showGuide;
    private bool _manualRoute;
    private string _selectedRoute = "";
    private float _scale = 1;
    private string _lastFirstAction = "";
    private bool _initialized;
    internal string RenderDiagnostics => "visible=" + _visible + " window=" + _window + " scale=" + _scale;
    private bool _visible;
    private JObject? _snapshot;
    private JObject? _recommendation;
    private string _message = "";
    private Action? _refresh;

    internal AdvisorPanel(Transform parent)
    {
        _bodyFont = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "SimHei", "Arial" }, 18);
        _titleFont = Font.CreateDynamicFontFromOSFont(new[] { "SimSun", "Microsoft YaHei", "Georgia" }, 26);
        _body = TextStyle(_bodyFont, 14, Ivory, true);
        _muted = TextStyle(_bodyFont, 13, Muted, true);
        _small = TextStyle(_bodyFont, 11, Muted, false);
        _title = TextStyle(_titleFont, 24, Ivory, false, FontStyle.Bold);
        _cardTitle = TextStyle(_bodyFont, 19, Ivory, true, FontStyle.Bold);
        _number = TextStyle(_bodyFont, 20, Ivory, false, FontStyle.Bold);

        _frame = Surface(Hex(0x211C29), Hex(0x8C6942), 7, 2, 0.018f);
        _primary = Surface(Hex(0x30282D), Hex(0xBD9455), 5, 1, 0.012f);
        _secondary = Surface(Hex(0x27232D), Hex(0x594947), 5, 1, 0.01f);
        _button = Surface(Hex(0x3A3032), Hex(0x987344), 4, 1, 0.005f);
        _button.font = _bodyFont;
        _button.fontSize = 12;
        _button.alignment = TextAnchor.MiddleCenter;
        _button.normal.textColor = Gold;
        _button.hover.background = Texture(Hex(0x514037), Hex(0xD0A464), 4, 1, 0.005f);
        _button.hover.textColor = Ivory;
        _button.active.background = Texture(Hex(0x29232A), Hex(0xD0A464), 4, 1, 0);
        _button.active.textColor = Gold;
        _smallButton = new GUIStyle(_button) { fontSize = 11 };
        _windowStyle = new GUIStyle { padding = new RectOffset(), border = new RectOffset() };
        _scrollTrack = Surface(Hex(0x211D26), Hex(0x211D26), 2, 0, 0);
        _scrollThumb = Surface(Hex(0x796044), Hex(0xA78451), 2, 0, 0);
        _scrollTrack.padding = new RectOffset();
        _scrollThumb.padding = new RectOffset();
        _scrollThumb.fixedWidth = 6;
        _coin = Texture(Gold, Hex(0x9A6D32), 12, 2, 0, 24);
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

    internal bool Draw(bool visible, JObject? snapshot, JObject? recommendation, string message, Action refresh)
    {
        _visible = visible;
        _snapshot = snapshot;
        _recommendation = recommendation;
        _message = message;
        _refresh = refresh;
        _blocker.SetActive(visible);
        if (!visible) return false;

        _scale = Mathf.Clamp(Screen.height / 1080f, 0.78f, 1.5f);
        var screenWidth = Screen.width / _scale;
        var screenHeight = Screen.height / _scale;
        var height = Mathf.Min(688, screenHeight - 52);
        if (!_initialized)
        {
            _window = new Rect(Math.Max(12, screenWidth - Width - 24), 82, Width, height);
            _initialized = true;
        }
        _window.height = height;
        _window.x = Mathf.Clamp(_window.x, 10, Math.Max(10, screenWidth - Width - 10));
        _window.y = Mathf.Clamp(_window.y, 10, Math.Max(10, screenHeight - height - 10));
        var firstAction = (string?)_recommendation?["recommendations"]?.First?["action_id"] ?? "";
        if (_lastFirstAction != firstAction)
        {
            _expanded.Clear();
            _warnings.Clear();
            _scroll = Vector2.zero;
            _lastFirstAction = firstAction;
            if (firstAction.Length > 0) _expanded.Add(firstAction);
        }

        var previousMatrix = GUI.matrix;
        var previousDepth = GUI.depth;
        var previousColor = GUI.color;
        var previousBackground = GUI.backgroundColor;
        var previousContent = GUI.contentColor;
        var previousEnabled = GUI.enabled;
        GUI.depth = 0;
        GUI.color = Color.white;
        GUI.backgroundColor = Color.white;
        GUI.contentColor = Color.white;
        GUI.enabled = true;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scale, _scale, 1));
        try
        {
            Fill(new Rect(_window.x + 4, _window.y + 8, Width, height), new Color(0, 0, 0, 0.25f));
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
        FrameOrnaments();
        DrawHeader();
        DrawStats();
        var rows = _recommendation?["recommendations"] as JArray;
        if (GUI.Button(new Rect(Padding, 173, 187, 28), (_showGuide ? "" : "◆ ") + PhaseTitle(), _smallButton)) _showGuide = false;
        if (GUI.Button(new Rect(220, 173, 187, 28), (_showGuide ? "◆ " : "") + "阵容参考", _smallButton)) _showGuide = true;
        var viewport = new Rect(14, 213, Width - 28, Math.Max(80, _window.height - 273));
        if (_showGuide) DrawStrategy(viewport);
        else if (rows == null || rows.Count == 0) DrawEmpty(viewport);
        else DrawRecommendations(viewport, rows);
        DrawFooter();
        GUI.DragWindow(new Rect(0, 0, Width - 50, 94));
    }

    private void DrawHeader()
    {
        var crest = new Rect(22, 22, 42, 48);
        Diamond(new Vector2(crest.center.x, crest.center.y), 23, Hex(0x8A643C));
        Diamond(new Vector2(crest.center.x, crest.center.y), 20, Hex(0x30252B));
        Label(new Rect(crest.x, crest.y + 4, crest.width, 36), "K", _title, Gold, TextAnchor.MiddleCenter);
        Label(new Rect(77, 22, 265, 33), "卡诺克 · 旅途参谋", _title, Ivory);
        Label(new Rect(79, 57, 230, 21), "看清取舍，再作选择", _small, Muted);
        if (GUI.Button(new Rect(Width - 47, 23, 24, 24), "×", _smallButton)) _visible = false;
        var isLive = (string?)_snapshot?["status"] == "live" && (string?)_snapshot?["hero"] == "Karnok";
        var statusColor = isLive ? Green : Muted;
        Fill(new Rect(Width - 93, 66, 4, 4), statusColor);
        Label(new Rect(Width - 82, 57, 60, 21), isLive ? "已同步" : "待同步", _small, statusColor);
        Rule(20, 94, Width - 40, Hex(0x8B6944));
        Diamond(new Vector2(Width / 2, 94), 3, Gold);
    }

    private void DrawStats()
    {
        var strip = new Rect(Padding, 108, Width - Padding * 2, 49);
        Fill(strip, new Color(0.06f, 0.045f, 0.075f, 0.36f));
        var cell = strip.width / 4;
        var attrs = _snapshot?["player_attributes"];
        var live = (string?)_snapshot?["status"] == "live";
        for (var i = 1; i < 4; i++) Fill(new Rect(strip.x + cell * i, strip.y + 9, 1, 31), Line);
        Stat(new Rect(strip.x, strip.y, cell, strip.height), "日程", live ? Value(_snapshot?["day"]) + " 天" : "—", Ivory);
        Stat(new Rect(strip.x + cell, strip.y, cell, strip.height), "金币", live ? Value(attrs?["Gold"]) : "—", Gold);
        Stat(new Rect(strip.x + cell * 2, strip.y, cell, strip.height), "声望", live ? Value(attrs?["Prestige"]) : "—", Green);
        Stat(new Rect(strip.x + cell * 3, strip.y, cell, strip.height), "等级", live ? Value(attrs?["Level"]) : "—", Cyan);
    }

    private void DrawRecommendations(Rect viewport, JArray rows)
    {
        var width = viewport.width - 15;
        var heights = rows.OfType<JObject>().Select((row, index) => CardHeight(row, index, width)).ToArray();
        var contentHeight = heights.Sum() + Math.Max(0, heights.Length - 1) * 10 + 4;
        var scrollLimit = Math.Max(0, contentHeight - viewport.height);
        _scroll.y = Mathf.Clamp(_scroll.y, 0, scrollLimit);
        _scroll = GUI.BeginScrollView(viewport, _scroll, new Rect(0, 0, width, contentHeight), false, false, GUIStyle.none, GUIStyle.none);
        var y = 0f;
        var index = 0;
        foreach (var row in rows.OfType<JObject>())
        {
            DrawCard(new Rect(3, y + 2, width - 3, heights[index]), row, index);
            y += heights[index] + 10;
            index++;
        }
        GUI.EndScrollView();
        if (scrollLimit > 0)
        {
            _scrollThumb.fixedHeight = Math.Max(30, viewport.height * viewport.height / contentHeight);
            _scroll.y = GUI.VerticalSlider(new Rect(viewport.xMax - 7, viewport.y, 6, viewport.height), _scroll.y, 0, scrollLimit, _scrollTrack, _scrollThumb);
        }
    }

    private float CardHeight(JObject row, int index, float width)
    {
        var contentWidth = width - 31;
        var id = (string?)row["action_id"] ?? index.ToString();
        var expanded = _expanded.Contains(id);
        var titleHeight = TextHeight(_cardTitle, Title(row), contentWidth - (index == 0 ? 0 : 61));
        var height = (index == 0 ? 43 : 17) + titleHeight + 13;
        if (!expanded)
        {
            var first = row["reasons"]?.First?.ToString();
            return height + (string.IsNullOrEmpty(first) ? 0 : Math.Min(40, TextHeight(_muted, first!, contentWidth))) + 13;
        }
        foreach (var reason in Strings(row["reasons"])) height += TextHeight(_body, reason, contentWidth - 13) + 9;
        var steps = Strings(row["steps"]);
        if (steps.Count > 0)
        {
            height += 28;
            foreach (var step in steps) height += TextHeight(_muted, step, contentWidth - 26) + 8;
        }
        var warnings = Strings(row["uncertainties"]);
        if (warnings.Count > 0)
        {
            height += 30;
            if (_warnings.Contains(id))
                foreach (var warning in warnings) height += TextHeight(_muted, Friendly(warning), contentWidth) + 8;
        }
        return height + 8;
    }

    private void DrawCard(Rect rect, JObject row, int index)
    {
        var id = (string?)row["action_id"] ?? index.ToString();
        var eligible = (bool?)row["eligible"] != false;
        var expanded = _expanded.Contains(id);
        GUI.Box(rect, GUIContent.none, index == 0 ? _primary : _secondary);
        var x = rect.x + 14;
        var width = rect.width - 28;
        var y = rect.y + 14;
        if (index == 0)
        {
            Badge(new Rect(x, y, 48, 20), eligible ? "首选" : "待判断", eligible ? Gold : Amber);
            Label(new Rect(x + 58, y, width - 94, 20), eligible ? "优先考虑这个方向" : "先核对选项效果", _small, Muted);
            y += 29;
        }
        else
        {
            Label(new Rect(x, y - 2, 29, 25), (index + 1).ToString("00"), _number, Muted);
        }
        var titleX = x + (index == 0 ? 0 : 34);
        var titleWidth = width - (index == 0 ? 0 : 61);
        var titleHeight = TextHeight(_cardTitle, Title(row), titleWidth);
        Label(new Rect(titleX, y, titleWidth, titleHeight), Title(row), _cardTitle, index == 0 ? Ivory : Hex(0xD4C5B4));
        var toggle = new Rect(rect.xMax - 36, rect.y + 12, 23, 22);
        if (GUI.Button(toggle, expanded ? "−" : "+", _smallButton))
        {
            if (expanded) _expanded.Remove(id); else _expanded.Add(id);
        }
        y += titleHeight + 13;
        if (!expanded)
        {
            var first = row["reasons"]?.First?.ToString() ?? "";
            Label(new Rect(x, y, width, Math.Min(40, TextHeight(_muted, first, width))), first, _muted, Muted);
            return;
        }
        foreach (var reason in Strings(row["reasons"]))
        {
            var h = TextHeight(_body, reason, width - 13);
            Diamond(new Vector2(x + 2, y + 9), 2, index == 0 ? Gold : Muted);
            Label(new Rect(x + 13, y, width - 13, h), reason, _body, Ivory);
            y += h + 9;
        }
        var steps = Strings(row["steps"]);
        if (steps.Count > 0)
        {
            Rule(x, y + 3, width, Line);
            Label(new Rect(x, y + 10, width, 20), "操作顺序", _small, Gold);
            y += 28;
            for (var n = 0; n < steps.Count; n++)
            {
                var h = TextHeight(_muted, steps[n], width - 26);
                Label(new Rect(x, y, 20, h), (n + 1) + ".", _small, Gold);
                Label(new Rect(x + 24, y, width - 24, h), steps[n], _muted, Ivory);
                y += h + 8;
            }
        }
        var warnings = Strings(row["uncertainties"]);
        if (warnings.Count > 0)
        {
            var open = _warnings.Contains(id);
            if (GUI.Button(new Rect(x, y + 1, width, 24), (open ? "收起" : "查看") + " " + warnings.Count + " 项待确认因素", _smallButton))
            {
                if (open) _warnings.Remove(id); else _warnings.Add(id);
            }
            y += 30;
            if (open)
                foreach (var warning in warnings)
                {
                    var text = Friendly(warning);
                    var h = TextHeight(_muted, text, width);
                    Label(new Rect(x, y, width, h), text, _muted, Amber);
                    y += h + 8;
                }
        }
    }

    private void DrawEmpty(Rect rect)
    {
        var live = (string?)_snapshot?["status"] == "live";
        var combat = (bool?)_snapshot?["in_combat"] == true;
        var hero = (string?)_snapshot?["hero"];
        var readError = (string?)_snapshot?["status"] == "read_error" || _message.IndexOf("失败", StringComparison.Ordinal) >= 0
            || (_recommendation?["summary"]?.ToString() ?? "").IndexOf("读取失败", StringComparison.Ordinal) >= 0;
        var title = readError ? "暂时无法同步" : !live ? "旅程尚未开始" : hero != "Karnok" ? "等待卡诺克的旅程" : combat ? "专注眼前的战斗" : _recommendation == null ? "正在整理新的选择" : "等待下一次抉择";
        var text = readError ? "状态恢复后会自动继续更新建议。" : !live || hero != "Karnok" ? "进入卡诺克对局后，事件、升级和购买建议会出现在这里。" : combat ? "战斗结束、出现新选项后，参谋会继续为你整理建议。" : _recommendation == null ? "正在核对当前状态与可选方案，请稍候。" : "出现新选项时，自动比较收益与取舍。";
        var centerY = rect.y + Math.Min(110, rect.height * 0.24f);
        Diamond(new Vector2(Width / 2, centerY), 24, Hex(0x6D523D));
        Diamond(new Vector2(Width / 2, centerY), 21, Hex(0x29232D));
        Label(new Rect(Width / 2 - 25, centerY - 23, 50, 42), "K", _title, Gold, TextAnchor.MiddleCenter);
        Label(new Rect(38, centerY + 46, Width - 76, 34), title, _cardTitle, Ivory, TextAnchor.MiddleCenter);
        Label(new Rect(54, centerY + 91, Width - 108, 80), text, _muted, Muted, TextAnchor.UpperCenter);
    }

    private void DrawStrategy(Rect viewport)
    {
        var guide = _recommendation?["strategy"] as JObject;
        var routes = guide?["routes"] as JArray;
        if (guide == null || routes == null || routes.Count == 0)
        {
            Label(new Rect(viewport.x + 14, viewport.y + 20, viewport.width - 28, 72),
                "阵容资料加载中。进入游戏后会结合当前状态显示；状态变化时暂停旧阵容判断。", _body, Muted);
            return;
        }
        var index = _manualRoute ? routes.ToList().FindIndex(r => (string?)r["id"] == _selectedRoute) : 0;
        if (index < 0) index = 0;
        if (GUI.Button(new Rect(viewport.x + 4, viewport.y, 38, 27), "‹", _button))
        {
            index = (index + routes.Count - 1) % routes.Count;
            _manualRoute = true;
            _guideScroll = Vector2.zero;
        }
        var hasSignal = routes.Any(r => (bool?)r["signal"] == true);
        var caption = _manualRoute ? "浏览 " : hasSignal ? "按持有核心 " : "流派参考 ";
        if (GUI.Button(new Rect(viewport.x + 49, viewport.y, viewport.width - 98, 27),
            caption + (index + 1) + " / " + routes.Count + (_manualRoute ? "  ·  点击恢复匹配" : "  ·  左右浏览"), _smallButton))
        {
            _manualRoute = false;
            index = 0;
            _guideScroll = Vector2.zero;
        }
        if (GUI.Button(new Rect(viewport.xMax - 42, viewport.y, 38, 27), "›", _button))
        {
            index = (index + 1) % routes.Count;
            _manualRoute = true;
            _guideScroll = Vector2.zero;
        }
        var route = (JObject)routes[index];
        if (!_manualRoute && _selectedRoute != (string?)route["id"]) _guideScroll = Vector2.zero;
        _selectedRoute = (string?)route["id"] ?? "";
        var lines = new List<(string Text, GUIStyle Style, Color Color, string Url)>();
        void Add(string text, GUIStyle? style = null, Color? color = null, string url = "") =>
            lines.Add((text, style ?? _body, color ?? Ivory, url));
        Add(Value(route["name"]), _cardTitle, Gold);
        Add(Value(guide["stage_label"]) + "  ·  " + Value(route["status"]), _muted, Cyan);
        Add(Value(route["summary"]));
        if ((bool?)guide["live"] == true)
        {
            var owned = Strings(route["owned_board"]);
            var stash = Strings(route["owned_stash"]);
            Add("当前在场：" + (owned.Count > 0 ? string.Join("、", owned) : "无此路线组件"), _muted, Green);
            if (stash.Count > 0) Add("仓库备选：" + string.Join("、", stash) + "（未计为生效联动）", _muted, Muted);
            var missing = Strings(route["missing_roles"]);
            if (missing.Count > 0) Add("尚缺功能：" + string.Join("、", missing), _muted, Amber);
            if ((bool?)route["signal"] != true) Add("还没有这条路线的辨识核心，先保证当日战力。", _muted, Amber);
        }
        else Add("当前状态不足以判断持有和缺牌，下面仅供查阅。", _muted, Amber);
        Add("转型条件", _body, Gold);
        Add(Value(route["pivot"]), _muted);
        Add(Value(guide["stage_note"]), _muted, Muted);
        Add(Value(guide["tier_note"]), _muted, Muted);
        foreach (var stage in (route["stages"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var current = (string?)stage["key"] == (string?)guide["stage"];
            Add((current ? "◆ 当前阶段  " : "◇ ") + Value(stage["label"]), _body, current ? Gold : Cyan);
            var slots = Value(stage["slots"]);
            Add("参考组合 · " + slots + " 格" + ((bool?)guide["live"] == true ? " / 当前解锁 " + Value(guide["unlocked_slots"]) + " 格" : ""), _muted, Muted);
            var pieces = (stage["items"] as JArray ?? new JArray()).OfType<JObject>().Select(item =>
                Value(item["name"]) + "［" + Value(item["starting_tier"]) + "·" + Value(item["span"]) + "格"
                + ((bool?)guide["live"] == true ? "·" + Value(item["owned"]) : "") + "］");
            Add(string.Join(" / ", pieces));
            Add(Value(stage["plan"]), _muted);
        }
        Add("升级与技能取舍", _body, Gold);
        Add(Value(route["skills"]), _muted);
        Add("注意条件", _body, Amber);
        Add(Value(route["caution"]), _muted, Amber);
        Add("资料与版本", _body, Gold);
        Add("复核 " + Value(guide["reviewed_at"]) + " · 对照补丁 " + Value(guide["reviewed_patch"]), _muted, Muted);
        Add(Value(guide["version_note"]), _muted, (bool?)guide["verified"] == true ? Muted : Amber);
        Add("攻略包含历史版本；点击来源查看原文。浏览路线不改变实际选项或自动操作。", _muted, Muted);
        foreach (var source in (route["sources"] as JArray ?? new JArray()).OfType<JObject>())
            Add(Value(source["title"]) + " · " + Value(source["date"]), _muted, Gold, (string?)source["url"] ?? "");

        var scrollView = new Rect(viewport.x, viewport.y + 37, viewport.width, viewport.height - 37);
        var contentWidth = scrollView.width - 18;
        var textWidth = contentWidth - 28;
        var heights = lines.Select(line => TextHeight(line.Style, line.Text, textWidth) + 13).ToList();
        var contentHeight = heights.Sum() + 24;
        var limit = Math.Max(0, contentHeight - scrollView.height);
        _guideScroll.y = Mathf.Clamp(_guideScroll.y, 0, limit);
        _guideScroll = GUI.BeginScrollView(scrollView, _guideScroll, new Rect(0, 0, contentWidth, contentHeight), false, false, GUIStyle.none, GUIStyle.none);
        GUI.Box(new Rect(3, 0, contentWidth - 3, contentHeight), GUIContent.none, _secondary);
        var y = 14f;
        for (var n = 0; n < lines.Count; n++)
        {
            var line = lines[n];
            var rect = new Rect(16, y, textWidth, heights[n] - 9);
            if (line.Url.Length > 0)
            {
                if (GUI.Button(rect, line.Text, _smallButton)) Application.OpenURL(line.Url);
            }
            else Label(rect, line.Text, line.Style, line.Color);
            y += heights[n];
        }
        GUI.EndScrollView();
        if (limit > 0)
        {
            _scrollThumb.fixedHeight = Math.Max(30, scrollView.height * scrollView.height / contentHeight);
            _guideScroll.y = GUI.VerticalSlider(new Rect(scrollView.xMax - 7, scrollView.y, 6, scrollView.height),
                _guideScroll.y, 0, limit, _scrollTrack, _scrollThumb);
        }
    }

    private void DrawFooter()
    {
        var y = _window.height - 52;
        Rule(Padding, y, Width - Padding * 2, Line);
        var status = (string?)_recommendation?["model_status"] ?? "";
        var mode = status.IndexOf("模型策略排序", StringComparison.Ordinal) >= 0 ? "模型参谋" : status.IndexOf("分析中", StringComparison.Ordinal) >= 0 ? "正在推演" : "规则推演";
        Label(new Rect(Padding + 12, y + 15, 90, 22), mode, _small, Muted);
        Diamond(new Vector2(Padding + 3, y + 26), 3, Gold);
        if (GUI.Button(new Rect(Width - 201, y + 11, 80, 28), "F9  隐藏", _smallButton)) _visible = false;
        if (GUI.Button(new Rect(Width - 112, y + 11, 90, 28), "F10  刷新", _smallButton)) _refresh?.Invoke();
    }

    private void FrameOrnaments()
    {
        foreach (var p in new[] { new Vector2(10, 10), new Vector2(Width - 10, 10), new Vector2(10, _window.height - 10), new Vector2(Width - 10, _window.height - 10) })
            Diamond(p, 3, Gold);
        Rule(25, 5, Width - 50, Hex(0xBC935C));
        Rule(25, _window.height - 6, Width - 50, Hex(0x71563F));
    }

    private string PhaseTitle()
    {
        var type = (string?)_snapshot?["state_type"];
        if (type == "LevelUpState") return "升级抉择";
        if (type == "ChoiceState") return "事件抉择";
        if (_snapshot?["options"] is JArray options && options.Any(c => (string?)c["type"] == "Item")) return "买卡与换卡";
        return "当前选择";
    }

    private void Stat(Rect rect, string label, string value, Color color)
    {
        Label(new Rect(rect.x, rect.y + 4, rect.width, 18), label, _small, Muted, TextAnchor.MiddleCenter);
        Label(new Rect(rect.x, rect.y + 21, rect.width, 25), value, _number, color, TextAnchor.MiddleCenter);
        if (label == "金币")
        {
            var textWidth = _number.CalcSize(new GUIContent(value)).x;
            GUI.DrawTexture(new Rect(rect.center.x - textWidth / 2 - 17, rect.y + 29, 11, 11), _coin);
        }
    }

    private void Badge(Rect rect, string text, Color color)
    {
        Fill(rect, new Color(color.r, color.g, color.b, 0.15f));
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

    private static float TextHeight(GUIStyle style, string text, float width) =>
        Math.Max(style.lineHeight + 2, style.CalcHeight(new GUIContent(text), Math.Max(20, width)) + 2);
    private static List<string> Strings(JToken? token) => token is JArray array ? array.Select(t => t.ToString()).Where(s => s.Length > 0).ToList() : new List<string>();
    private static string Value(JToken? value) => value == null || value.Type == JTokenType.Null ? "—" : value.ToString();
    private static string Title(JObject row) => ((string?)row["title"] ?? "查看当前选项").Replace("选择 ", "");
    private static string Friendly(string text) => Regex.Replace(text, @"(?:暂未量化效果|未解析[^：]*|未支持[^：]*)：T\w+", "部分效果尚未纳入估算，请结合游戏说明判断");
    private static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);

    private static void Fill(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }
    private static void Rule(float x, float y, float width, Color color) => Fill(new Rect(x, y, width, 1), color);
    private void Diamond(Vector2 center, float radius, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2, radius * 2), _diamond);
        GUI.color = previous;
    }

    private Texture2D CreateDiamond()
    {
        const int size = 48;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) {
            name = "KarnokAdvisor.Emblem", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
        };
        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var distance = Math.Abs(x + 0.5f - size / 2f) + Math.Abs(y + 0.5f - size / 2f);
            pixels[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(size / 2f - distance));
        }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        _textures.Add(texture);
        return texture;
    }

    private GUIStyle Surface(Color fill, Color border, int radius, int thickness, float grain)
    {
        return new GUIStyle { normal = { background = Texture(fill, border, radius, thickness, grain) },
            border = new RectOffset(9, 9, 9, 9), padding = new RectOffset(12, 12, 12, 12) };
    }

    private Texture2D Texture(Color fill, Color border, int radius, int thickness, float grain, int size = 32)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) {
            name = "KarnokAdvisor.Surface", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp
        };
        var pixels = new Color[size * size];
        var noise = new System.Random(317);
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var dx = Math.Max(radius - x - 0.5f, x - (size - radius) + 0.5f);
            var dy = Math.Max(radius - y - 0.5f, y - (size - radius) + 0.5f);
            var distance = Math.Sqrt(Math.Max(0, dx) * Math.Max(0, dx) + Math.Max(0, dy) * Math.Max(0, dy));
            var alpha = Mathf.Clamp01(radius - (float)distance + 0.5f);
            var edge = Math.Min(Math.Min(x, size - x - 1), Math.Min(y, size - y - 1));
            var isBorder = edge < thickness || distance > radius - thickness;
            var color = isBorder ? border : fill;
            var shade = (float)(noise.NextDouble() - 0.5) * grain;
            if (isBorder) shade += (y >= size / 2 ? 0.035f : -0.045f);
            pixels[y * size + x] = new Color(color.r + shade, color.g + shade, color.b + shade, alpha * color.a);
        }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        _textures.Add(texture);
        return texture;
    }

    public void Dispose()
    {
        UnityEngine.Object.Destroy(_blocker);
        UnityEngine.Object.Destroy(_bodyFont);
        UnityEngine.Object.Destroy(_titleFont);
        foreach (var texture in _textures) UnityEngine.Object.Destroy(texture);
    }
}
