using System.ComponentModel;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace VisorIV4;

public partial class MainWindow : Window
{
    private AppConfig _cfg = AppConfig.Load();
    private PlcService? _plc;
    private LiveImageService? _live;
    private int _currentProgram = -1;
    private bool _cameraReady;          // WebView2 initialized
    private bool _cameraOnline;         // last navigation / ping succeeded
    private bool _allowClose;
    private bool _suppressCombo;

    // Last displayed state, kept so texts can be re-rendered when the language changes.
    private (string Key, string Brush) _plcPill = ("S_PlcConnecting", "IdleBrush");
    private (string Key, string Brush) _camPill = ("S_CamConnecting", "IdleBrush");
    private Func<(string Title, string Detail)> _camMsg = () => (Loc.T("S_CamConnectingMsg"), "");
    private Action _posRender;

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _reload = new();
    private readonly DispatcherTimer _ping = new() { Interval = TimeSpan.FromSeconds(5) };

    private Brush B(string key) => (Brush)FindResource(key);

    public MainWindow()
    {
        InitializeComponent();
        _posRender = () => RenderNoPosition("S_NoPosHint");
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _retry.Tick += (_, _) => { _retry.Stop(); if (_cfg.Cam.IsWebMode) NavigateCamera(); };
        _reload.Tick += (_, _) => { if (_cameraOnline && _cfg.Cam.IsWebMode) NavigateCamera(); };
        _ping.Tick += async (_, _) => await PingCameraAsync();
        Closing += OnClosing;
        StateChanged += OnStateChanged;
        _titleHide.Tick += (_, _) => HideAutoTitle();
        Loc.Changed += RefreshTexts;
        RenderCamMessage();
    }

    // ================= Lifecycle =================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _clock.Start();
        ApplyConfig();
        await InitCameraAsync();
        StartViewer();
        _ping.Start();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_cfg.Kiosk && !_allowClose) e.Cancel = true; // in kiosk mode, close only from Administration
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Loc.Changed -= RefreshTexts;
        _plc?.Dispose();
        _live?.Dispose();
        CameraView.Dispose();
    }

    private void ApplyConfig()
    {
        // re-render the colors set from code (pills, position bar, ghost button) when the theme changes
        if (Theme.Apply(_cfg.Theme)) RefreshTexts();
        Loc.Apply(_cfg.Language);

        // --- Window / kiosk ---
        if (_cfg.Kiosk)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            ResizeMode = ResizeMode.CanResize;
        }
        ApplyChrome();

        ApplyLayout();

        // --- Programs / active camera ---
        FillProgramCombo();
        UpdateCameraTitle();

        ApplyPlcAndTimers();
    }

    // ================= Auto-hide title bar =================

    private bool _chromeBusy;
    private readonly DispatcherTimer _titleHide = new() { Interval = TimeSpan.FromMilliseconds(600) };

    private void OnStateChanged(object? sender, EventArgs e) => ApplyChrome();

    /// <summary>Maximized (non-kiosk): no Windows title bar; a bar drops down when the cursor touches the top edge.
    /// Normal: the standard Windows title bar.</summary>
    private void ApplyChrome()
    {
        if (_chromeBusy || WindowState == WindowState.Minimized) return;
        bool autoHide = !_cfg.Kiosk && WindowState == WindowState.Maximized;
        TitleHotZone.Visibility = autoHide ? Visibility.Visible : Visibility.Collapsed;
        if (!autoHide) HideAutoTitle();
        if (_cfg.Kiosk) return;

        var style = autoHide ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        if (WindowStyle == style) return;
        _chromeBusy = true;
        try
        {
            if (autoHide)
            {
                // re-maximize after removing the border so the window fills exactly the screen
                // (NoResize: without it the invisible resize frame pushes the window 7 px past every edge)
                WindowState = WindowState.Normal;
                WindowStyle = style;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = style;
                ResizeMode = ResizeMode.CanResize;
            }
        }
        finally { _chromeBusy = false; }
    }

    private void OnTitleHotZoneEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _titleHide.Stop();
        AutoTitleBar.Visibility = Visibility.Visible;
    }

    private void OnAutoTitleEnter(object sender, System.Windows.Input.MouseEventArgs e) => _titleHide.Stop();
    private void OnAutoTitleLeave(object sender, System.Windows.Input.MouseEventArgs e) => _titleHide.Start();

    private void HideAutoTitle()
    {
        _titleHide.Stop();
        AutoTitleBar.Visibility = Visibility.Collapsed;
    }

    private void OnAutoTitleMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) WindowState = WindowState.Normal; // double click restores, like a title bar
    }

    private void OnCaptionMinimize(object sender, RoutedEventArgs e) { HideAutoTitle(); WindowState = WindowState.Minimized; }
    private void OnCaptionRestore(object sender, RoutedEventArgs e) => WindowState = WindowState.Normal;
    private void OnCaptionClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Reference + camera side by side (or stacked). In ghost mode only the camera pane is shown.</summary>
    private void ApplyLayout()
    {
        bool ghost = GhostActive;
        RefPane.Visibility = ghost ? Visibility.Collapsed : Visibility.Visible;
        double r = ghost ? 0 : Math.Clamp(_cfg.ReferenceRatio, 0.2, 0.8);
        var refLen = ghost ? new GridLength(0) : new GridLength(r, GridUnitType.Star);
        var camLen = new GridLength(1 - r, GridUnitType.Star);
        int refIdx = _cfg.ReferenceFirst ? 0 : 1, camIdx = 1 - refIdx;
        bool vertical = _cfg.Layout.Equals("Vertical", StringComparison.OrdinalIgnoreCase);

        Grid.SetRow(RefPane, vertical ? refIdx : 0); Grid.SetColumn(RefPane, vertical ? 0 : refIdx);
        Grid.SetRow(CamPane, vertical ? camIdx : 0); Grid.SetColumn(CamPane, vertical ? 0 : camIdx);
        if (vertical)
        {
            Col0.Width = new GridLength(1, GridUnitType.Star); Col1.Width = new GridLength(0);
            Row0.Height = refIdx == 0 ? refLen : camLen; Row1.Height = refIdx == 0 ? camLen : refLen;
        }
        else
        {
            Row0.Height = new GridLength(1, GridUnitType.Star); Row1.Height = new GridLength(0);
            Col0.Width = refIdx == 0 ? refLen : camLen; Col1.Width = refIdx == 0 ? camLen : refLen;
        }
    }

    private void ApplyPlcAndTimers()
    {
        // --- PLC ---
        _plc?.Dispose();
        _plc = null;
        _currentProgram = -1;
        DirBar.Visibility = _cfg.Plc.Enabled && _cfg.Plc.ReadOffsets ? Visibility.Visible : Visibility.Collapsed;

        if (_cfg.Plc.Enabled)
        {
            ManualProgramCombo.Visibility = Visibility.Collapsed;
            SetPlcPill("S_PlcConnecting", "IdleBrush");
            ShowNoPosition("S_WaitPlc");
            ProgramNumberText.Text = "--";
            ProgramNameText.Text = Loc.T("S_NoProgram");
            RefImage.Source = null;
            RefMissingText.Visibility = Visibility.Visible;
            RefMissingText.Text = Loc.T("S_NoRef");
            _plc = new PlcService(_cfg.Plc, _cfg.Cam);
            _plc.Updated += s => Dispatcher.BeginInvoke(() => OnPlcData(s));
            _plc.Start();
        }
        else
        {
            ManualProgramCombo.Visibility = Visibility.Visible;
            SetPlcPill("S_PlcManual", "IdleBrush");
            int p = Math.Clamp(_cfg.ManualProgram, 1, _cfg.ProgramCount);
            _suppressCombo = true;
            ManualProgramCombo.SelectedIndex = p - 1;
            _suppressCombo = false;
            SetProgram(p);
        }

        // --- Periodic viewer reload ---
        _reload.Stop();
        if (_cfg.CameraReloadMinutes > 0)
        {
            _reload.Interval = TimeSpan.FromMinutes(_cfg.CameraReloadMinutes);
            _reload.Start();
        }
        if (_cameraReady) CameraView.ZoomFactor = Math.Clamp(_cfg.CameraZoom, 0.25, 5);
        UpdateGhost();
    }

    // ================= Ghost overlay =================

    private void OnToggleGhost(object sender, RoutedEventArgs e)
    {
        _cfg.GhostEnabled = !_cfg.GhostEnabled;
        try { _cfg.Save(); } catch (Exception ex) { Log.Error("Could not save configuration", ex); }
        Log.Info($"Ghost mode {(_cfg.GhostEnabled ? "on" : "off")}");
        ApplyLayout();
        UpdateGhost();
    }

    private ImageSource? _ghostFor;     // reference image the cached ghost was built from
    private string? _ghostStyle;
    private ImageSource? _ghostSrc;

    /// <summary>Ghost mode only works in direct image mode (WebView2 always draws above WPF content).</summary>
    private bool GhostActive => _cfg.GhostEnabled && !_cfg.Cam.IsWebMode;

    /// <summary>Shows the active camera's reference image of the active program over the live image,
    /// whether or not the camera is online.</summary>
    private void UpdateGhost()
    {
        bool ghost = GhostActive;
        GhostButton.SetResourceReference(BackgroundProperty, ghost ? "AccentBrush" : "ButtonBrush");
        if (ghost) GhostButton.Foreground = Brushes.White;
        else GhostButton.SetResourceReference(ForegroundProperty, "TextBrush");
        GhostButton.IsEnabled = !_cfg.Cam.IsWebMode;
        bool hasRef = RefImage.Source is not null;
        string style = _cfg.GhostStyleNorm;
        if (ghost && hasRef)
        {
            // the derived image (gray / outline) is cached until the reference or the style changes
            if (!ReferenceEquals(_ghostFor, RefImage.Source) || _ghostStyle != style)
            {
                _ghostFor = RefImage.Source;
                _ghostStyle = style;
                var src = RefImage.Source as System.Windows.Media.Imaging.BitmapSource;
                _ghostSrc = style switch
                {
                    "Outline" => GhostRenderer.Outline(src, Color.FromRgb(0x39, 0xFF, 0x14)),
                    "Watermark" => GhostRenderer.Watermark(src),
                    _ => src,
                };
            }
            GhostImage.Source = _ghostSrc;
            GhostImage.Opacity = style == "Outline" ? 1.0 : Math.Clamp(_cfg.GhostOpacity, 5, 95) / 100.0;
        }
        else GhostImage.Source = null;
        GhostImage.Visibility = ghost && hasRef ? Visibility.Visible : Visibility.Hidden;
        GhostMissingPanel.Visibility = ghost && !hasRef ? Visibility.Visible : Visibility.Collapsed;
        GhostMissingText.Text = RefMissingText.Text.Replace("\n", "  ·  ");
    }

    private void UpdateCameraTitle() => CamTitleText.Text = $"{Loc.T("S_CamTitle")}  —  {_cfg.Cam.Display}";

    private void FillProgramCombo()
    {
        _suppressCombo = true;
        int sel = ManualProgramCombo.SelectedIndex;
        ManualProgramCombo.Items.Clear();
        for (int i = 1; i <= _cfg.ProgramCount; i++)
            ManualProgramCombo.Items.Add(new ComboBoxItem { Content = $"{i:00} · {_cfg.ProgramName(i)}", Tag = i });
        if (sel >= 0 && sel < ManualProgramCombo.Items.Count) ManualProgramCombo.SelectedIndex = sel;
        _suppressCombo = false;
    }

    // ================= Language =================

    private void OnToggleLanguage(object sender, RoutedEventArgs e)
    {
        _cfg.Language = Loc.Current == "en" ? "es" : "en";
        try { _cfg.Save(); } catch (Exception ex) { Log.Error("Could not save configuration", ex); }
        Loc.Apply(_cfg.Language);
        Log.Info($"Language changed to {_cfg.Language}");
    }

    /// <summary>Re-renders every text set from code (XAML texts update by themselves via DynamicResource).</summary>
    private void RefreshTexts()
    {
        SetPlcPill(_plcPill.Key, _plcPill.Brush);
        SetCamPill(_camPill.Key, _camPill.Brush);
        RenderCamMessage();
        _posRender();
        FillProgramCombo();
        UpdateCameraTitle();
        if (_currentProgram != -1) SetProgram(_currentProgram, force: true);
        else
        {
            ProgramNameText.Text = Loc.T("S_NoProgram");
            RefMissingText.Text = Loc.T("S_NoRef");
        }
    }

    // ================= Program / reference =================

    private void SetProgram(int program, bool force = false)
    {
        if (program == _currentProgram && !force) return;
        bool changed = program != _currentProgram;
        _currentProgram = program;

        bool valid = program >= 1 && program <= _cfg.ProgramCount;
        ProgramNumberText.Text = valid ? program.ToString("00") : program.ToString();
        ProgramNameText.Text = valid ? _cfg.ProgramName(program) : Loc.T("S_ProgramOutOfRange");

        var path = valid ? ReferenceStore.Find(_cfg.Cam.ReferenceFolder, program) : null;
        var img = ReferenceStore.Load(path);
        RefImage.Source = img;
        RefMissingText.Visibility = img is null ? Visibility.Visible : Visibility.Collapsed;
        RefMissingText.Text = valid
            ? Loc.F("S_NoRefFor", program)
            : Loc.F("S_PlcOutOfRange", program, _cfg.ProgramCount);
        UpdateGhost();
        if (changed) Log.Info($"Active program: {program}");
    }

    private void OnManualProgramChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCombo || ManualProgramCombo.SelectedItem is not ComboBoxItem { Tag: int p }) return;
        _cfg.ManualProgram = p;
        try { _cfg.Save(); } catch (Exception ex) { Log.Error("Could not save configuration", ex); }
        SetProgram(p);
    }

    // ================= PLC =================

    private void OnPlcData(PlcSnapshot s)
    {
        if (_plc is null) return; // late message from a stopped service
        if (!s.Connected)
        {
            SetPlcPill("S_PlcOffline", "BadBrush");
            PlcPill.ToolTip = s.Error;
            ShowNoPosition("S_NoPlcComm");
            return;
        }

        SetPlcPill("S_PlcOk", "OkBrush");
        PlcPill.ToolTip = null;
        if (s.Program is int p) SetProgram(p);
        if (_cfg.Plc.ReadOffsets && s.X is double x && s.Y is double y)
        {
            _posRender = () => RenderPosition(x, y);
            _posRender();
        }
    }

    private void ShowNoPosition(string detailKey)
    {
        _posRender = () => RenderNoPosition(detailKey);
        _posRender();
    }

    private void RenderNoPosition(string detailKey)
    {
        DirBar.Background = B("IdleBrush");
        DirTitle.Text = Loc.T("S_NoPos");
        DirDetail.Text = Loc.T(detailKey);
        ValueX.Text = "X: --";
        ValueY.Text = "Y: --";
        ValueX.Foreground = ValueY.Foreground = Brushes.White;
        foreach (var a in new[] { ArrowUp, ArrowDown, ArrowLeft, ArrowRight, CenterDot }) a.Background = B("ArrowIdleBrush");
        CenterDotText.Text = "•";
        CenterDotText.Foreground = Brushes.White;
    }

    /// <summary>
    /// x / y = measured part offset from the correct position (computed by camera/PLC).
    /// If the part is shifted toward X+, the operator must move it toward X− (left arrow).
    /// Screen convention: X+ = right, Y+ = up (use InvertX/InvertY to adjust).
    /// </summary>
    private void RenderPosition(double x, double y)
    {
        var c = _cfg.Plc;
        double dx = c.InvertX ? -x : x;
        double dy = c.InvertY ? -y : y;
        bool okX = Math.Abs(dx) <= c.ToleranceX;
        bool okY = Math.Abs(dy) <= c.ToleranceY;

        ValueX.Text = $"X: {x,7:+0.00;-0.00;0.00} {c.Units}";
        ValueY.Text = $"Y: {y,7:+0.00;-0.00;0.00} {c.Units}";
        ValueX.Foreground = okX ? Brushes.White : B("WarnBrush");
        ValueY.Foreground = okY ? Brushes.White : B("WarnBrush");

        ArrowLeft.Background = !okX && dx > 0 ? B("WarnBrush") : B("ArrowIdleBrush");
        ArrowRight.Background = !okX && dx < 0 ? B("WarnBrush") : B("ArrowIdleBrush");
        ArrowDown.Background = !okY && dy > 0 ? B("WarnBrush") : B("ArrowIdleBrush");
        ArrowUp.Background = !okY && dy < 0 ? B("WarnBrush") : B("ArrowIdleBrush");

        if (okX && okY)
        {
            DirBar.Background = B("OkBrush");
            CenterDot.Background = Brushes.White;
            CenterDotText.Text = "✓";
            CenterDotText.Foreground = B("OkBrush");
            DirTitle.Text = Loc.T("S_PosOk");
            DirDetail.Text = Loc.T("S_PosOkDetail");
            return;
        }

        DirBar.Background = new SolidColorBrush(Color.FromRgb(0x6B, 0x2A, 0x14));
        CenterDot.Background = B("ArrowIdleBrush");
        CenterDotText.Text = "•";
        CenterDotText.Foreground = Brushes.White;

        var moves = new List<string>();
        var shifts = new List<string>();
        if (!okX) { shifts.Add(dx > 0 ? "X+" : "X−"); moves.Add(dx > 0 ? "◀ X−" : "X+ ▶"); }
        if (!okY) { shifts.Add(dy > 0 ? "Y+" : "Y−"); moves.Add(dy > 0 ? "▼ Y−" : "Y+ ▲"); }
        DirTitle.Text = Loc.F("S_MoveTo", string.Join("   ", moves));
        DirDetail.Text = Loc.F("S_Shifted", string.Join(Loc.T("S_And"), shifts));
    }

    private void SetPlcPill(string key, string brush)
    {
        _plcPill = (key, brush);
        PlcPill.Background = B(brush);
        PlcPillText.Text = Loc.T(key);
    }

    private void SetCamPill(string key, string brush)
    {
        _camPill = (key, brush);
        CamPill.Background = B(brush);
        CamPillText.Text = Loc.T(key);
    }

    // ================= Camera (IV4 web viewer) =================

    private async Task InitCameraAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewDir);
            await CameraView.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            Log.Error("Could not start WebView2", ex);
            ShowCameraOffline(() => (Loc.T("S_CamNoWebView"), Loc.T("S_CamNoWebViewDetail")));
            return;
        }

        var core = CameraView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NewWindowRequested += (_, a) => { a.Handled = true; core.Navigate(a.Uri); };
        core.NavigationCompleted += OnCameraNavigationCompleted;
        core.ProcessFailed += (_, a) =>
        {
            Log.Error($"WebView2 process failed: {a.ProcessFailedKind}");
            ShowCameraOffline(() => (Loc.T("S_CamStopped"), Loc.T("S_Retrying")));
            _retry.Start();
        };
        _cameraReady = true;
        CameraView.ZoomFactor = Math.Clamp(_cfg.CameraZoom, 0.25, 5);
    }

    /// <summary>Starts the viewer of the active camera in its mode (direct image or web page).</summary>
    private void StartViewer()
    {
        _retry.Stop();
        _live?.Dispose();
        _live = null;
        FpsText.Text = "";
        _cameraOnline = false;

        if (_cfg.Cam.IsWebMode)
        {
            LiveImage.Visibility = Visibility.Hidden;
            LiveImage.Source = null;
            UpdateGhost();
            NavigateCamera();
            return;
        }

        // Direct image mode: the web page is not needed
        if (_cameraReady) { try { CameraView.CoreWebView2.Navigate("about:blank"); } catch { } }
        CameraView.Visibility = Visibility.Hidden;

        var url = _cfg.Cam.LiveImageUrl;
        if (url is null)
        {
            ShowCameraOffline(() => (Loc.T("S_CamBadUrl"), _cfg.Cam.Url));
            return;
        }
        SetCamPill("S_CamConnecting", "IdleBrush");
        Log.Info($"Direct image mode: {url}");
        var live = new LiveImageService(url, _cfg.ImageIntervalMs);
        live.Frame += (bmp, fps) => Dispatcher.BeginInvoke(() => OnLiveFrame(live, bmp, fps));
        live.Failed += (n, msg) => Dispatcher.BeginInvoke(() => OnLiveFailed(live, n, msg));
        _live = live;
        live.Start();
    }

    private void OnLiveFrame(LiveImageService source, System.Windows.Media.Imaging.BitmapSource bmp, double fps)
    {
        if (!ReferenceEquals(source, _live)) return; // late frame from a stopped service
        LiveImage.Source = bmp;
        FpsText.Text = fps > 0 ? $"{fps:0.0} fps" : "";
        if (!_cameraOnline)
        {
            _cameraOnline = true;
            LiveImage.Visibility = Visibility.Visible;
            CamOfflinePanel.Visibility = Visibility.Collapsed;
            SetCamPill("S_CamOk", "OkBrush");
            UpdateGhost();
        }
    }

    private void OnLiveFailed(LiveImageService source, int failures, string message)
    {
        if (!ReferenceEquals(source, _live)) return;
        // keep the last image on screen for short glitches; report after 3 consecutive failures
        if (failures < 3) return;
        var url = _cfg.Cam.LiveImageUrl ?? _cfg.Cam.Url;
        ShowCameraOffline(() => (Loc.T("S_CamNoConn"), Loc.F("S_CamNoResponseDetail", url) + "\n" + message));
    }

    private void NavigateCamera()
    {
        if (!_cameraReady) return;
        _retry.Stop();
        var url = _cfg.Cam.Url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            ShowCameraOffline(() => (Loc.T("S_CamBadUrl"), url));
            return;
        }
        SetCamPill("S_CamConnecting", "IdleBrush");
        try { CameraView.CoreWebView2.Navigate(uri.ToString()); }
        catch (Exception ex)
        {
            Log.Error("Error navigating to camera", ex);
            ShowCameraOffline(() => (Loc.T("S_CamOpenFail"), ex.Message));
            _retry.Start();
        }
    }

    private void OnCameraNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!_cfg.Cam.IsWebMode) return; // about:blank while in direct image mode
        if (e.IsSuccess)
        {
            _cameraOnline = true;
            CameraView.Visibility = Visibility.Visible;
            CamOfflinePanel.Visibility = Visibility.Collapsed;
            SetCamPill("S_CamOk", "OkBrush");
            Log.Info("Camera viewer loaded");
        }
        else
        {
            var status = e.WebErrorStatus;
            var url = _cfg.Cam.Url;
            Log.Error($"Camera navigation failed: {status}");
            ShowCameraOffline(() => (Loc.T("S_CamNoConn"), Loc.F("S_CamNoConnDetail", url, status)));
            _retry.Start();
        }
    }

    private void ShowCameraOffline(Func<(string Title, string Detail)> msg)
    {
        _cameraOnline = false;
        CameraView.Visibility = Visibility.Hidden;
        LiveImage.Visibility = Visibility.Hidden;
        UpdateGhost();
        FpsText.Text = "";
        CamOfflinePanel.Visibility = Visibility.Visible;
        _camMsg = msg;
        RenderCamMessage();
        SetCamPill("S_CamOffline", "BadBrush");
    }

    private void RenderCamMessage()
    {
        var (title, detail) = _camMsg();
        CamOfflineText.Text = title;
        CamOfflineDetail.Text = detail;
    }

    /// <summary>Checks the camera still answers (TCP connect to the web port); if it drops, hides the viewer and retries.</summary>
    private async Task PingCameraAsync()
    {
        // direct image mode detects failures by itself
        if (!_cfg.Cam.IsWebMode || !_cameraReady || !Uri.TryCreate(_cfg.Cam.Url, UriKind.Absolute, out var uri)) return;
        bool alive;
        try
        {
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync(uri.Host, uri.Port, cts.Token);
            alive = true;
        }
        catch { alive = false; }

        if (!alive && _cameraOnline)
        {
            var url = _cfg.Cam.Url;
            Log.Error("Camera stopped responding");
            ShowCameraOffline(() => (Loc.T("S_CamNoResponse"), Loc.F("S_CamNoResponseDetail", url)));
            _retry.Start();
        }
    }

    private void OnReloadCamera(object sender, RoutedEventArgs e) => StartViewer();

    // ================= Administration =================

    private void OnAdmin(object sender, RoutedEventArgs e)
    {
        var pwd = new PasswordWindow(_cfg) { Owner = this };
        if (pwd.ShowDialog() != true) return;

        var admin = new AdminWindow(_cfg.Clone()) { Owner = this };
        bool saved = admin.ShowDialog() == true;

        if (admin.ExitRequested)
        {
            _allowClose = true;
            Close();
            return;
        }
        if (!saved)
        {
            Loc.Apply(_cfg.Language); // undo a language preview made inside Administration
            // reference images may have changed even if the rest was cancelled
            if (_currentProgram > 0) SetProgram(_currentProgram, force: true);
            return;
        }

        int oldCam = _cfg.ActiveCamera;
        _cfg = admin.Config;
        if (oldCam != _cfg.ActiveCamera) Log.Info($"Active camera changed: {oldCam} -> {_cfg.ActiveCamera} ({_cfg.Cam.Name}, {_cfg.Cam.Url})");
        try { _cfg.Save(); }
        catch (Exception ex)
        {
            Log.Error("Could not save configuration", ex);
            MessageBox.Show(this, Loc.F("S_SaveCfgError", ex.Message), Loc.T("S_AppName"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Log.Info("Configuration updated");
        ApplyConfig();
        if (_currentProgram > 0) SetProgram(_currentProgram, force: true);
        StartViewer(); // URL, mode or active camera may have changed
    }
}
