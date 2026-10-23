using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Station.Controls;

/// <summary>
/// Camera slot control.  Two display modes (priority order):
///   1. StreamUrl set     → MJPEG stream via &lt;img&gt; tag (backend /api/cameras/{id}/stream)
///   2. Offline / no URL → dark "Mất tín hiệu" placeholder
/// </summary>
public sealed partial class CameraVideoControl : UserControl
{
    private bool _webViewReady;
    private string? _currentContent; // track last-rendered state to avoid redundant navigations

    public CameraVideoControl()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── Dependency Properties ─────────────────────────────────────────────────

    public static readonly DependencyProperty StreamUrlProperty =
        DependencyProperty.Register(nameof(StreamUrl), typeof(string), typeof(CameraVideoControl),
            new PropertyMetadata(null, (d, _) => ((CameraVideoControl)d).UpdateContent()));


    public static readonly DependencyProperty IsOnlineProperty =
        DependencyProperty.Register(nameof(IsOnline), typeof(bool), typeof(CameraVideoControl),
            new PropertyMetadata(false, (d, _) => ((CameraVideoControl)d).UpdateContent()));

    public static readonly DependencyProperty IsStreamEnabledProperty =
        DependencyProperty.Register(nameof(IsStreamEnabled), typeof(bool), typeof(CameraVideoControl),
            new PropertyMetadata(true, (d, _) => ((CameraVideoControl)d).UpdateContent()));

    public static readonly DependencyProperty SelectedResolutionProperty =
        DependencyProperty.Register(nameof(SelectedResolution), typeof(string), typeof(CameraVideoControl),
            new PropertyMetadata("320×240", (d, _) => ((CameraVideoControl)d).UpdateContent()));

    public string? StreamUrl
    {
        get => (string?)GetValue(StreamUrlProperty);
        set => SetValue(StreamUrlProperty, value);
    }

    public bool IsOnline
    {
        get => (bool)GetValue(IsOnlineProperty);
        set => SetValue(IsOnlineProperty, value);
    }

    public bool IsStreamEnabled
    {
        get => (bool)GetValue(IsStreamEnabledProperty);
        set => SetValue(IsStreamEnabledProperty, value);
    }

    public string SelectedResolution
    {
        get => (string)GetValue(SelectedResolutionProperty);
        set => SetValue(SelectedResolutionProperty, value);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await VideoView.EnsureCoreWebView2Async();

            VideoView.CoreWebView2.Settings.IsStatusBarEnabled          = false;
            VideoView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            VideoView.CoreWebView2.Settings.IsZoomControlEnabled         = false;
            VideoView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            _webViewReady = true;
            UpdateContent();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CameraVideoControl] WebView2 init failed: {ex.Message}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _webViewReady = false;
        _currentContent = null;
    }

    // ── Content routing ───────────────────────────────────────────────────────

    private void UpdateContent()
    {
        if (!_webViewReady) return;

        if (!IsStreamEnabled) { Navigate("paused", BuildPausedPage()); return; }
        if (!IsOnline) { Navigate("offline", BuildOfflinePage()); return; }

        if (!string.IsNullOrEmpty(StreamUrl))
        {
            var res = SelectedResolution ?? "320×240";
            var isWebSocket = StreamUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)
                           || StreamUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);
            var html = isWebSocket ? BuildWebSocketStreamPage(StreamUrl) : BuildStreamPage(StreamUrl, res);
            Navigate($"stream:{StreamUrl}:{res}", html);
            return;
        }

        Navigate("offline", BuildOfflinePage());
    }

    private void Navigate(string key, string html)
    {
        if (_currentContent == key) return;
        _currentContent = key;
        VideoView.CoreWebView2.NavigateToString(html);
    }

    // ── Page builders ─────────────────────────────────────────────────────────

    private static string BuildOfflinePage() => @"<!DOCTYPE html><html>
<body style='margin:0;background:#060606;display:flex;align-items:center;
             justify-content:center;height:100vh;flex-direction:column;gap:10px;
             font-family:Consolas,monospace'>
  <div style='color:#374151;font-size:32px;line-height:1'>◌</div>
  <div style='color:#6B7280;font-size:10px;letter-spacing:.12em'>MẤT TÍN HIỆU</div>
</body></html>";

    private static string BuildPausedPage() => @"<!DOCTYPE html><html>
<body style='margin:0;background:#0a0d14;display:flex;align-items:center;
             justify-content:center;height:100vh;flex-direction:column;gap:12px;
             font-family:Consolas,monospace'>
  <div style='width:48px;height:48px;border-radius:50%;background:#1e2a3a;
              display:flex;align-items:center;justify-content:center'>
    <div style='display:flex;gap:5px'>
      <div style='width:5px;height:20px;background:#3b82f6;border-radius:2px'></div>
      <div style='width:5px;height:20px;background:#3b82f6;border-radius:2px'></div>
    </div>
  </div>
  <div style='color:#3b82f6;font-size:10px;letter-spacing:.14em;font-weight:bold'>ĐÃ TẮT LUỒNG</div>
  <div style='color:#374151;font-size:9px;letter-spacing:.06em'>Bấm ▶ để bật lại</div>
</body></html>";

    // Use /frame (single-JPEG poll) instead of /stream (MJPEG) to bypass
    // Chromium's internal MJPEG buffer: it accumulates frames then flushes
    // in a burst, which is the root cause of the "frame jump" artifact.
    // The resolution string (e.g. "640×480") is forwarded as ?w=&h= query params
    // so the backend can resize frames before returning them.
    private static string BuildStreamPage(string streamUrl, string resolution)
    {
        var slashStream = streamUrl.LastIndexOf("/stream");
        var frameUrl    = (slashStream >= 0 ? streamUrl[..slashStream] : streamUrl) + "/frame";

        // Build resolution query segment — split on Unicode multiplication sign ×
        string resQuery = "";
        var parts = resolution.Split('×');
        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), out int rw)
            && int.TryParse(parts[1].Trim(), out int rh))
            resQuery = $"&w={rw}&h={rh}";

        return $@"<!DOCTYPE html><html>
<body style='margin:0;background:#000;overflow:hidden;width:100vw;height:100vh'>
  <canvas id='cv' style='position:absolute;inset:0;width:100%;height:100%'></canvas>
  <img id='buf' style='display:none'>
  <script>
    var cv  = document.getElementById('cv');
    var ctx = cv.getContext('2d');
    var img = document.getElementById('buf');
    var url = '{frameUrl}';
    var on  = true;

    function resize() {{ cv.width = innerWidth || 320; cv.height = innerHeight || 240; }}
    resize();
    window.onresize = resize;

    img.onload = function() {{
      ctx.drawImage(img, 0, 0, cv.width, cv.height);
      if (on) setTimeout(poll, 20);
    }};
    img.onerror = function() {{
      if (on) setTimeout(poll, 500);
    }};

    function poll() {{
      img.src = url + '?_=' + Date.now() + '{resQuery}';
    }}

    poll();
    window.addEventListener('beforeunload', function() {{ on = false; }});
  </script>
</body></html>";
    }

    // BackendV2's camera relay is a WebSocket push (/ws/camera/{id}/view) rather than a
    // pollable HTTP endpoint: the browser WebSocket delivers one binary JPEG per message,
    // which we turn into an object URL and draw to canvas on load — no server polling loop
    // needed, and no MJPEG-buffer burst artifact since each message renders immediately.
    private static string BuildWebSocketStreamPage(string wsUrl)
    {
        return $@"<!DOCTYPE html><html>
<body style='margin:0;background:#000;overflow:hidden;width:100vw;height:100vh'>
  <canvas id='cv' style='position:absolute;inset:0;width:100%;height:100%'></canvas>
  <img id='buf' style='display:none'>
  <script>
    var cv  = document.getElementById('cv');
    var ctx = cv.getContext('2d');
    var img = document.getElementById('buf');
    var url = '{wsUrl}';
    var on  = true;
    var ws  = null;
    var currentBlobUrl = null;

    function resize() {{ cv.width = innerWidth || 320; cv.height = innerHeight || 240; }}
    resize();
    window.onresize = resize;

    img.onload = function() {{
      ctx.drawImage(img, 0, 0, cv.width, cv.height);
      if (currentBlobUrl) {{ URL.revokeObjectURL(currentBlobUrl); currentBlobUrl = null; }}
    }};

    function connect() {{
      if (!on) return;
      ws = new WebSocket(url);
      ws.binaryType = 'blob';
      ws.onmessage = function(ev) {{
        if (currentBlobUrl) URL.revokeObjectURL(currentBlobUrl);
        currentBlobUrl = URL.createObjectURL(ev.data);
        img.src = currentBlobUrl;
      }};
      ws.onclose = function() {{ if (on) setTimeout(connect, 1000); }};
      ws.onerror = function() {{ ws.close(); }};
    }}

    connect();
    window.addEventListener('beforeunload', function() {{ on = false; if (ws) ws.close(); }});
  </script>
</body></html>";
    }

}
