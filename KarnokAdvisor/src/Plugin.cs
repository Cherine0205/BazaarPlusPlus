using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KarnokAdvisor;

[BepInPlugin("local.karnok.advisor", "Karnok Handbook", "0.5.2")]
public sealed class Plugin : BaseUnityPlugin
{
    private readonly GameReader _reader = new GameReader();
    private string _runtime = "";
    private string _snapshotId = "";
    private JObject? _snapshot;
    private JObject? _document;
    private float _nextCapture;
    private float _lastError;
    private bool _visible = true;
    private int _refreshNonce;
    private AdvisorPanel? _panel;
    private Process? _worker;
    private string _message = "等待游戏状态";
    private bool _loggedPanel;

    private void Awake()
    {
        try
        {
            var settingsPath = Path.ChangeExtension(Assembly.GetExecutingAssembly().Location, ".settings.json");
            if (!File.Exists(settingsPath)) throw new FileNotFoundException("缺少 KarnokAdvisor.settings.json", settingsPath);
            var settings = JObject.Parse(File.ReadAllText(settingsPath, Encoding.UTF8));
            _runtime = Path.GetFullPath((string?)settings["runtime_dir"] ?? throw new InvalidDataException("runtime_dir missing"));
            Directory.CreateDirectory(_runtime);
            var heartbeat = Path.Combine(_runtime, "guide-heartbeat.json");
            var sameWorker = false;
            if (File.Exists(heartbeat) && DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeat) < TimeSpan.FromSeconds(5))
            {
                try
                {
                    var ready = JObject.Parse(File.ReadAllText(heartbeat, Encoding.UTF8));
                    sameWorker = (string?)ready["kind"] == ReferenceViewState.DocumentKind && (int?)ready["parent_pid"] == Process.GetCurrentProcess().Id;
                }
                catch (Exception ex) when (ex is IOException || ex is JsonException) { }
            }
            if (!sameWorker)
            {
                var python = (string?)settings["python_executable"] ?? "python";
                var script = (string?)settings["advisor_script"] ?? throw new InvalidDataException("advisor_script missing");
                var start = new ProcessStartInfo(python,
                    "-u \"" + script + "\" --runtime \"" + _runtime + "\" --parent-pid " +
                    Process.GetCurrentProcess().Id) {
                    UseShellExecute = false, CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(script) ?? _runtime
                };
                _worker = Process.Start(start);
            }
            Logger.LogInfo("Karnok Handbook 0.5.2 loaded. Local reference and ownership only; F9 panel, F10 sync. Runtime: " + _runtime);
        }
        catch (Exception ex)
        {
            _message = "初始化失败：" + ex.Message;
            Logger.LogError(ex);
        }
    }

    private void Update()
    {
        _panel?.PumpArt();
        if (string.IsNullOrEmpty(_runtime) || Time.unscaledTime < _nextCapture) return;
        _nextCapture = Time.unscaledTime + 0.6f;
        try
        {
            var captured = _reader.Capture();
            captured["refresh_nonce"] = _refreshNonce;
            captured["process_id"] = Process.GetCurrentProcess().Id;
            var semantic = captured.ToString(Formatting.None);
            using (var sha = SHA256.Create())
                _snapshotId = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(semantic))).Replace("-", "").ToLowerInvariant();
            captured["snapshot_id"] = _snapshotId;
            captured["captured_at_utc"] = DateTimeOffset.UtcNow.ToString("O");
            _snapshot = captured;
            AtomicWrite(Path.Combine(_runtime, "snapshot.json"), captured.ToString(Formatting.None));
            _message = (string?)captured["hero"] == "Karnok" ? "已读取当前卡诺克状态" : "等待卡诺克对局";
        }
        catch (Exception ex)
        {
            _snapshot = null;
            _snapshotId = "";
            _message = "持有状态暂不可读：" + ex.GetBaseException().Message;
            if (Time.unscaledTime - _lastError > 15) { Logger.LogWarning(_message); _lastError = Time.unscaledTime; }
            var failure = new JObject { ["status"] = "read_error", ["snapshot_id"] = "error",
                ["captured_at_utc"] = DateTimeOffset.UtcNow.ToString("O"), ["message"] = _message };
            AtomicWrite(Path.Combine(_runtime, "snapshot.json"), failure.ToString(Formatting.None));
        }
        // A worker or publication failure only pauses ownership; static references remain browsable.
        try
        {
            var responsePath = Path.Combine(_runtime, "guide.json");
            if (!File.Exists(responsePath)) return;
            using var stream = new FileStream(responsePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var response = JObject.Parse(reader.ReadToEnd());
            if ((string?)response["kind"] != ReferenceViewState.DocumentKind) return;
            if (!(response["reference"] is JObject) && _document?["reference"] is JObject cached)
            {
                response["reference"] = ReferenceViewState.CachedReference(cached, (string?)response["status"] == "catalog_error");
            }
            _document = response;
        }
        catch (Exception ex) when (ex is IOException || ex is JsonException)
        {
            if (Time.unscaledTime - _lastError > 15) { Logger.LogWarning("Handbook sync deferred: " + ex.GetType().Name); _lastError = Time.unscaledTime; }
        }
    }

    private void OnGUI()
    {
        var input = Event.current;
        if (input.type == EventType.KeyDown && input.keyCode == KeyCode.F9)
        {
            _visible = !_visible;
            input.Use();
        }
        if (input.type == EventType.KeyDown && input.keyCode == KeyCode.F10)
        {
            _refreshNonce++;
            _nextCapture = 0;
            input.Use();
        }
        _panel ??= new AdvisorPanel(transform, text => Logger.LogWarning(text));
        var snapshot = _snapshot;
        _visible = _panel.Draw(_visible, snapshot, _document, _message, () => {
            _refreshNonce++;
            _nextCapture = 0;
        });
        if (!_loggedPanel && Time.realtimeSinceStartup > 8)
        {
            Logger.LogInfo("Advisor UI ready: screen=" + Screen.width + "x" + Screen.height + " " + _panel.RenderDiagnostics);
            _loggedPanel = true;
        }
    }

    internal static void AtomicWrite(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }

    private void OnDestroy()
    {
        if (_worker != null)
        {
            try { if (!_worker.HasExited) _worker.Kill(); }
            catch (InvalidOperationException) { }
            _worker.Dispose();
        }
        _panel?.Dispose();
    }
}
