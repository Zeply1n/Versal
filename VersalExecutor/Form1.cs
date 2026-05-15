using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace VersalExecutor
{
    public partial class Form1 : Form
    {
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HT_CAPTION       = 0x2;

        // ═══════════════════════════════════════════════════════════════
        //  IApi — common surface
        // ═══════════════════════════════════════════════════════════════
        private interface IApi : IDisposable
        {
            string Name { get; }
            Task   AttachAsync();
            void   Execute(string script);
            bool   IsAttached();
            void   Kill();
            void   SetAutoAttach(bool v);
            void   SetAttachNotify(string title, string text);
        }

        // ── Velocity wrapper ──────────────────────────────────────────
        // Confirmed signatures from DLL metadata:
        //   AttachAPI(bool)    -> Task      (instance)
        //   ExecuteScript(string) -> ValueType (instance)
        //   IsAttached()       -> bool      (instance)
        //   KillRoblox()       -> void      (static)
        //   SetAutoAttach(bool)-> void      (instance)
        //   SetAttachNotify(string,string) -> void (static)
        //   StartCommunication() -> void    (instance)
        //   StopCommunication()  -> void    (instance)
        //   UseOutput(bool)    -> void      (static)
        // ─────────────────────────────────────────────────────────────
        private sealed class VelocityWrapper : IApi
        {
            public string Name => "Velocity";

            private readonly AssemblyLoadContext _ctx;
            private readonly object      _inst;
            private readonly Type        _type;
            private readonly MethodInfo  _attachApi;
            private readonly MethodInfo  _execScript;
            private readonly MethodInfo  _isAttached;
            private readonly MethodInfo  _killRoblox;
            private readonly MethodInfo  _setAutoAttach;
            private readonly MethodInfo? _startComm;
            private readonly MethodInfo? _stopComm;
            private readonly MethodInfo? _setAttachNotify;
            private readonly MethodInfo? _useOutput;
            private readonly MethodInfo? _autoUpdate;
            private readonly object?     _logger;
            private readonly EventInfo?  _logOnLog;
            private Delegate?            _logDelegate;
            private Action<string,Color>? _logSink;

            public VelocityWrapper(string dllPath, Action<string, Color> logSink)
            {
                _logSink = logSink;

                // Isolated load context — safe even if DLL is loaded again later
                _ctx = new AssemblyLoadContext(dllPath, isCollectible: true);
                var asm = _ctx.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

                _type = asm.GetType("QuorumAPI.QuorumModule")
                        ?? throw new Exception("QuorumModule class not found in Velocity DLL.");

                _type.GetField("_AutoUpdateLogs", BindingFlags.Public | BindingFlags.Static)
                     ?.SetValue(null, true);

                _inst = Activator.CreateInstance(_type)!;

                // AttachAPI(bool) — 1 explicit bool param, returns Task
                _attachApi = _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "AttachAPI"
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(bool))
                    // fallback: any AttachAPI on instance
                    ?? _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(m => m.Name == "AttachAPI")
                    ?? throw new Exception("AttachAPI not found in Velocity DLL.");

                // ExecuteScript(string)
                _execScript = _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ExecuteScript"
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(string))
                    ?? throw new Exception("ExecuteScript(string) not found.");

                // IsAttached() — 0-param instance, returns bool
                _isAttached = _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "IsAttached" && m.GetParameters().Length == 0)
                    ?? throw new Exception("IsAttached() not found.");

                // KillRoblox() — static
                _killRoblox = _type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "KillRoblox")
                    ?? throw new Exception("KillRoblox() not found.");

                // SetAutoAttach(bool) — instance
                _setAutoAttach = _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "SetAutoAttach" && m.GetParameters().Length == 1)
                    ?? throw new Exception("SetAutoAttach(bool) not found.");

                // Optional methods
                _startComm       = _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                        .FirstOrDefault(m => m.Name == "StartCommunication");
                _stopComm        = _type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                        .FirstOrDefault(m => m.Name == "StopCommunication");
                _setAttachNotify = _type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                        .FirstOrDefault(m => m.Name == "SetAttachNotify");
                _useOutput       = _type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                        .FirstOrDefault(m => m.Name == "UseOutput");
                _autoUpdate      = _type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                        .FirstOrDefault(m => m.Name == "AutoUpdate");

                // Logger
                var lp = _type.GetProperty("Logger", BindingFlags.Public | BindingFlags.Static);
                if (lp != null)
                {
                    _logger = lp.GetValue(null);
                    if (_logger != null)
                    {
                        var lt = _logger.GetType();
                        _logOnLog = lt.GetEvent("OnLog");
                        ApplyLoggerSettings(lt);
                    }
                }

                Boot();
            }

            private void Boot()
            {
                _autoUpdate?.Invoke(null, null);
                _startComm?.Invoke(_inst, null);
                if (_useOutput != null)
                {
                    var ps = _useOutput.GetParameters();
                    _useOutput.Invoke(null, ps.Length == 0 ? null : new object[] { true });
                }
                if (_logOnLog != null && _logger != null && _logSink != null)
                {
                    _logDelegate = Delegate.CreateDelegate(
                        _logOnLog.EventHandlerType!,
                        this, GetType().GetMethod(nameof(OnLog),
                            BindingFlags.NonPublic | BindingFlags.Instance)!);
                    _logOnLog.AddEventHandler(_logger, _logDelegate);
                }
            }

            private void ApplyLoggerSettings(Type lt)
            {
                var setTheme  = lt.GetMethod("SetTheme");
                var setFormat = lt.GetMethod("SetFormat");
                var setSource = lt.GetMethod("SetLogSource");

                if (setTheme != null && _logger != null)
                {
                    var t = _type.Assembly.GetType("COP.LogTheme");
                    if (t != null)
                    {
                        var o = Activator.CreateInstance(t)!;
                        SP(t, o, "Info",    Color.FromArgb(196, 190, 221));
                        SP(t, o, "Success", Color.FromArgb( 82, 217, 138));
                        SP(t, o, "Warning", Color.FromArgb(240, 160,  80));
                        SP(t, o, "Error",   Color.FromArgb(238, 107, 119));
                        SP(t, o, "System",  Color.FromArgb(209, 184, 255));
                        setTheme.Invoke(_logger, new[] { o });
                    }
                }
                if (setFormat != null && _logger != null)
                {
                    var t = _type.Assembly.GetType("COP.LogFormat");
                    if (t != null)
                    {
                        var o = Activator.CreateInstance(t)!;
                        SP(t, o, "InfoTag",    "[INFO]");
                        SP(t, o, "SuccessTag", "[OK]");
                        SP(t, o, "WarningTag", "[WARN]");
                        SP(t, o, "ErrorTag",   "[ERR]");
                        SP(t, o, "SystemTag",  "[SYS]");
                        setFormat.Invoke(_logger, new[] { o });
                    }
                }
                if (setSource != null && _logger != null)
                {
                    var t = _type.Assembly.GetType("COP.LogSource");
                    if (t != null)
                        try { setSource.Invoke(_logger, new[] { Enum.Parse(t, "All") }); } catch { }
                }
            }

            private void OnLog(string msg, Color c) => _logSink?.Invoke(msg, c);

            private static void SP(Type t, object o, string n, object v)
            {
                t.GetProperty(n)?.SetValue(o, v);
                t.GetField(n)?.SetValue(o, v);
            }

            public Task AttachAsync()
            {
                // AttachAPI takes a bool — pass false (no forced UI / silent attach)
                var r = _attachApi.Invoke(_inst, new object[] { false });
                return r as Task ?? Task.CompletedTask;
            }

            public void Execute(string script) =>
                _execScript.Invoke(_inst, new object[] { script });

            public bool IsAttached() =>
                (bool)(_isAttached.Invoke(_inst, null) ?? false);

            public void Kill() =>
                _killRoblox.Invoke(null, null);

            public void SetAutoAttach(bool v) =>
                _setAutoAttach.Invoke(_inst, new object[] { v });

            public void SetAttachNotify(string title, string text) =>
                _setAttachNotify?.Invoke(null, new object[] { title, text });

            public void Dispose()
            {
                if (_logOnLog != null && _logger != null && _logDelegate != null)
                    try { _logOnLog.RemoveEventHandler(_logger, _logDelegate); } catch { }
                try { _stopComm?.Invoke(_inst, null); } catch { }
                try { _ctx.Unload(); } catch { }
            }
        }

        // ── Form fields ───────────────────────────────────────────────────
        private IApi?   _api;
        private bool    _attached   = false;
        private bool    _autoAttach = false;
        private bool    _apiBooted  = false;
        private WebView2? _webView;

        private readonly string _appDir;
        private readonly string _scriptsDir;
        private readonly string _velocityDll;

        public Form1()
        {
            InitializeComponent();

            _appDir      = AppDomain.CurrentDomain.BaseDirectory;
            _scriptsDir  = Path.Combine(_appDir, "Scripts");
            _velocityDll = Path.Combine(_appDir, "Bin", "velocity", "QuorumAPI.dll");

            Text            = "Versal";
            Size            = new Size(1060, 700);
            MinimumSize     = new Size(860, 560);
            FormBorderStyle = FormBorderStyle.None;
            BackColor       = Color.FromArgb(10, 9, 16);
            StartPosition   = FormStartPosition.CenterScreen;
            DoubleBuffered  = true;
            SetStyle(ControlStyles.ResizeRedraw, true);

            var ico = Path.Combine(_appDir, "versal.ico");
            if (File.Exists(ico)) Icon = new Icon(ico);

            Directory.CreateDirectory(_scriptsDir);
            InitWebView();
        }

        private void LoadVelocity()
        {
            if (!File.Exists(_velocityDll))
            {
                JSLog("[ERR]", "Velocity DLL not found: Bin\\velocity\\QuorumAPI.dll", "c-err");
                return;
            }
            try
            {
                var prev = _api;
                var next = new VelocityWrapper(_velocityDll, OnApiLog);
                prev?.Dispose();
                _api = next;
                if (_attached) { _attached = false; JSRun("setAttached(false)"); }
                JSLog("[SYS]", "Velocity loaded.", "c-sys");
            }
            catch (Exception ex)
            {
                JSLog("[ERR]", "Velocity: " + ex.Message, "c-err");
            }
        }

        private async void InitWebView()
        {
            _webView = new WebView2
            {
                Dock                  = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(10, 9, 16),
            };
            Controls.Add(_webView);
            var env = await CoreWebView2Environment.CreateAsync(
                null, Path.Combine(Path.GetTempPath(), "VersalExecutor_WV2"));
            await _webView.EnsureCoreWebView2Async(env);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled            = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled            = false;
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "versal.app", _appDir, CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.WebMessageReceived  += OnWebMessage;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.Navigate("https://versal.app/index.html");
        }

        private void OnNavigationCompleted(object? sender,
            CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!_apiBooted)
            {
                _apiBooted = true;
                LoadVelocity();
            }
            RefreshFileTree();
        }

        private async void OnWebMessage(object sender,
            CoreWebView2WebMessageReceivedEventArgs e)
        {
            var raw = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(raw)) return;
            int c1 = raw.IndexOf(':'); if (c1 < 0) return;
            int c2 = raw.IndexOf(':', c1 + 1);
            var verb = c2 < 0 ? raw[(c1+1)..] : raw[(c1+1)..c2];
            var data = c2 < 0 ? ""            : raw[(c2+1)..];

            switch (verb)
            {
                case "INJECT":             await DoInjectAsync();           break;
                case "INJECT_PID":         await DoInjectAsync();           break;
                case "EXECUTE":            DoExecute(data);                 break;
                case "KILL":               DoKill();                        break;
                case "SAVE":               DoSave(data);                    break;
                case "OPEN_FILE":          Invoke((Action)DoOpenFile);      break;
                case "LOAD_SCRIPT":        DoLoadScript(data);              break;
                case "DEL_SCRIPT":         DoDeleteScript(data);            break;
                case "AUTO_ATTACH_TOGGLE": ToggleAutoAttach();              break;
                case "SET_ALWAYS_ON_TOP":  SetAlwaysOnTop(data=="true");    break;
                case "GET_PROCESSES":      SendRobloxProcesses();           break;
                case "DRAG_START":
                    Invoke((Action)(() =>
                    {
                        ReleaseCapture();
                        SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)HT_CAPTION, IntPtr.Zero);
                    }));
                    break;
                case "MINIMIZE":
                    Invoke((Action)(() => WindowState = FormWindowState.Minimized)); break;
                case "MAXIMIZE":
                    Invoke((Action)(() => WindowState =
                        WindowState == FormWindowState.Maximized
                            ? FormWindowState.Normal : FormWindowState.Maximized));
                    break;
                case "CLOSE": Invoke((Action)Close); break;
            }
        }

        private async Task DoInjectAsync()
        {
            if (_api == null) { JSLog("[ERR]", "No API loaded.", "c-err"); return; }
            if (_attached)    { JSLog("[SYS]", "Already attached.", "c-sys"); return; }

            JSRun("setInjecting(true)");
            try   { await _api.AttachAsync(); }
            catch (Exception ex)
            {
                JSRun("setInjecting(false)");
                JSLog("[ERR]", "Inject failed: " + ex.Message, "c-err");
                return;
            }
            JSRun("setInjecting(false)");

            if (_api.IsAttached())
            {
                _attached = true;
                JSRun("setAttached(true)");
                try { _api.SetAttachNotify("Versal", "Attached!"); } catch { }
            }
            else
            {
                var rbx = Process.GetProcessesByName("RobloxPlayerBeta");
                JSLog("[ERR]", rbx.Length == 0
                    ? "No Roblox process. Open Roblox first."
                    : "Attach failed — run as Administrator.", "c-err");
            }
        }

        private void DoExecute(string script)
        {
            if (_api == null)                      { JSLog("[ERR]",  "No API loaded.",    "c-err");  return; }
            if (!_attached)                        { JSLog("[ERR]",  "Not attached.",     "c-err");  return; }
            if (string.IsNullOrWhiteSpace(script)) { JSLog("[WARN]", "Editor is empty.", "c-warn"); return; }
            try   { _api.Execute(script); }
            catch (Exception ex) { JSLog("[ERR]", "Execute: " + ex.Message, "c-err"); }
        }

        private void DoKill()
        {
            try { _api?.Kill(); }
            catch { foreach (var p in Process.GetProcessesByName("RobloxPlayerBeta"))
                        try { p.Kill(); } catch { } }
            _attached = false;
            JSRun("setAttached(false)");
            JSLog("[WARN]", "Roblox terminated.", "c-warn");
        }


        private void SendRobloxProcesses()
        {
            var procs = Process.GetProcessesByName("RobloxPlayerBeta")
                .Concat(Process.GetProcessesByName("RobloxPlayer"))
                .Select(p =>
                {
                    string title = "";
                    try { title = p.MainWindowTitle; } catch { }
                    if (string.IsNullOrWhiteSpace(title)) title = "Roblox";
                    return "{\"pid\":" + p.Id + ",\"title\":" + Esc(title) + "}";
                })
                .ToList();
            var json = "[" + string.Join(",", procs) + "]";
            JSRun("receiveProcesses(" + json + ")");
        }

        private void ToggleAutoAttach()
        {
            _autoAttach = !_autoAttach;
            try { _api?.SetAutoAttach(_autoAttach); } catch { }
            JSLog("[SYS]", "Auto-attach " + (_autoAttach ? "on." : "off."), "c-sys");
        }

        private void SetAlwaysOnTop(bool v)
        {
            Invoke((Action)(() => TopMost = v));
        }

        private void DoSave(string script)
        {
            Invoke((Action)(() =>
            {
                using var dlg = new SaveFileDialog
                {
                    Filter = "Lua Script (*.lua)|*.lua|Text (*.txt)|*.txt",
                    InitialDirectory = _scriptsDir, FileName = "script.lua",
                };
                if (dlg.ShowDialog() != DialogResult.OK) return;
                File.WriteAllText(dlg.FileName, script);
                JSLog("[OK]", "Saved: " + Path.GetFileName(dlg.FileName), "c-ok");
                RefreshFileTree();
            }));
        }

        private void DoOpenFile()
        {
            using var dlg = new OpenFileDialog
            {
                Filter = "Lua / Text (*.lua;*.txt)|*.lua;*.txt|All Files (*.*)|*.*",
                InitialDirectory = _scriptsDir,
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            JSRun("loadContent(" + Esc(File.ReadAllText(dlg.FileName))
                         + "," + Esc(Path.GetFileName(dlg.FileName)) + ")");
        }

        private void DoLoadScript(string f)
        {
            var p = Path.Combine(_scriptsDir, f);
            if (!File.Exists(p)) return;
            JSRun("loadContent(" + Esc(File.ReadAllText(p)) + "," + Esc(f) + ")");
        }

        private void DoDeleteScript(string f)
        {
            var p = Path.Combine(_scriptsDir, f);
            if (File.Exists(p)) File.Delete(p);
            RefreshFileTree();
            JSLog("[SYS]", "Deleted: " + f, "c-sys");
        }

        private void RefreshFileTree()
        {
            var files = Directory.GetFiles(_scriptsDir, "*.lua")
                .Concat(Directory.GetFiles(_scriptsDir, "*.txt"))
                .Select(Path.GetFileName).OrderBy(x => x).ToList();
            JSRun("if(typeof updateTree==='function')updateTree(["
                  + string.Join(",", files.Select(Esc)) + "])");
        }

        private void OnApiLog(string msg, Color c)
        {
            var l = msg.ToLowerInvariant();
            if (l.Contains("script") && (l.Contains("execut") || l.Contains("sent"))) return;
            string cls = "c-sys";
            if      (c.G > c.R && c.G > c.B)              cls = "c-ok";
            else if (c.R > 200 && c.G > 150 && c.B < 120) cls = "c-warn";
            else if (c.R > 200 && c.G < 130)               cls = "c-err";
            JSLog("[SYS]", msg.Trim(), cls);
        }

        private void JSLog(string tag, string msg, string cls) =>
            JSRun("appendLog(" + Esc(tag) + "," + Esc(msg) + ",'" + cls + "')");

        private void JSRun(string js)
        {
            if (_webView?.CoreWebView2 == null) return;
            void Exec() => _webView.CoreWebView2.ExecuteScriptAsync(js);
            if (InvokeRequired) Invoke((Action)Exec); else Exec();
        }

        private static string Esc(string s) =>
            "\"" + (s ?? "")
                .Replace("\\",   "\\\\").Replace("\"",  "\\\"")
                .Replace("\r\n", "\\n") .Replace("\n",  "\\n")
                .Replace("\r",   "\\n") .Replace("</",  "<\\/") + "\"";

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84)
            {
                const int B = 6;
                var pt = PointToClient(new Point(m.LParam.ToInt32()));
                int x = pt.X, y = pt.Y, w = Width, h = Height;
                if (x < B && y < B)     { m.Result=(IntPtr)13; return; }
                if (x > w-B && y < B)   { m.Result=(IntPtr)14; return; }
                if (x < B && y > h-B)   { m.Result=(IntPtr)16; return; }
                if (x > w-B && y > h-B) { m.Result=(IntPtr)17; return; }
                if (x < B)              { m.Result=(IntPtr)10; return; }
                if (x > w-B)            { m.Result=(IntPtr)11; return; }
                if (y < B)              { m.Result=(IntPtr)12; return; }
                if (y > h-B)            { m.Result=(IntPtr)15; return; }
                m.Result=(IntPtr)1; return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _api?.Dispose();
            base.OnFormClosed(e);
        }
    }
}
