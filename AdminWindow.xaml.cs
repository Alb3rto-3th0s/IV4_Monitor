using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace VisorIV4;

public sealed class ProgramRefItem : INotifyPropertyChanged
{
    private BitmapImage? _thumb;
    private string _name = "";

    public int Number { get; init; }
    public string NumberText => Number.ToString("00");
    public string Name { get => _name; set { _name = value; Raise(); } }
    public BitmapImage? Thumbnail { get => _thumb; set { _thumb = value; Raise(); Raise(nameof(MissingVisibility)); } }
    public Visibility MissingVisibility => _thumb is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public partial class AdminWindow : Window
{
    private static readonly string[] Types = { "DINT", "INT", "SINT", "REAL", "LREAL" };
    private static readonly string[] PlcTypes = { "ControlLogix", "Micro800", "MicroLogix", "Slc500", "Plc5" };

    private readonly ObservableCollection<ProgramRefItem> _items = new();
    private CameraProfile? _refCam;     // camera whose references are being edited
    private bool _loading = true;

    public AppConfig Config { get; }
    public bool ExitRequested { get; private set; }

    private string AppName => Loc.T("S_AppName");

    public AdminWindow(AppConfig cfg)
    {
        InitializeComponent();
        Config = cfg;

        foreach (var c in new[] { ProgTypeCombo, XTypeCombo, YTypeCombo }) c.ItemsSource = Types;
        PlcTypeCombo.ItemsSource = PlcTypes;

        // Cameras
        CamerasList.ItemsSource = cfg.Cameras;
        ActiveCameraCombo.ItemsSource = cfg.Cameras;
        ActiveCameraCombo.SelectedIndex = cfg.ActiveCamera - 1;
        RefCameraCombo.ItemsSource = cfg.Cameras;

        ImgIntervalBox.Text = cfg.ImageIntervalMs.ToString();
        GhostOpacityBox.Text = cfg.GhostOpacity.ToString();
        GhostStyleCombo.SelectedValue = cfg.GhostStyleNorm;
        CameraZoomBox.Text = cfg.CameraZoom.ToString(CultureInfo.InvariantCulture);
        ReloadBox.Text = cfg.CameraReloadMinutes.ToString();
        LanguageCombo.SelectedIndex = Loc.Normalize(cfg.Language) == "es" ? 1 : 0;
        LayoutCombo.SelectedIndex = cfg.Layout.Equals("Vertical", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        RatioBox.Text = ((int)Math.Round(cfg.ReferenceRatio * 100)).ToString();
        RefFirstCheck.IsChecked = cfg.ReferenceFirst;
        KioskCheck.IsChecked = cfg.Kiosk;
        ThemeCombo.SelectedValue = VisorIV4.Theme.Normalize(cfg.Theme);

        var p = cfg.Plc;
        PlcEnabledCheck.IsChecked = p.Enabled;
        PlcIpBox.Text = p.Ip;
        PlcTypeCombo.SelectedItem = PlcTypes.FirstOrDefault(t => t.Equals(p.PlcType, StringComparison.OrdinalIgnoreCase)) ?? "ControlLogix";
        PlcPathBox.Text = p.Path;
        ProgTypeCombo.SelectedItem = Norm(p.ProgramType);
        ProgOffsetBox.Text = p.ProgramOffset.ToString();
        PollBox.Text = p.PollMs.ToString();
        ReadOffsetsCheck.IsChecked = p.ReadOffsets;
        XTypeCombo.SelectedItem = Norm(p.OffsetXType);
        YTypeCombo.SelectedItem = Norm(p.OffsetYType);
        TolXBox.Text = p.ToleranceX.ToString(CultureInfo.InvariantCulture);
        TolYBox.Text = p.ToleranceY.ToString(CultureInfo.InvariantCulture);
        UnitsBox.Text = p.Units;
        InvXCheck.IsChecked = p.InvertX;
        InvYCheck.IsChecked = p.InvertY;

        RefList.ItemsSource = _items;
        _loading = false;
        RefCameraCombo.SelectedIndex = cfg.ActiveCamera - 1; // loads that camera's references
    }

    private static string Norm(string t) => Types.FirstOrDefault(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)) ?? "DINT";

    private void UpdateFolderText() =>
        RefFolderText.Text = _refCam is null ? "" : Loc.F("S_Folder", _refCam.ReferenceFolder);

    /// <summary>Applies the selected language immediately as a preview (reverted by the main window on Cancel).</summary>
    private void OnLanguagePreview(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedItem is not ComboBoxItem { Tag: string lang }) return;
        Loc.Apply(lang);
        UpdateFolderText();
    }

    // ---------- Reference images (per camera) ----------

    private void OnRefCameraChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RefCameraCombo.SelectedItem is not CameraProfile cam) return;
        FlushNames();
        _refCam = cam;
        _items.Clear();
        for (int i = 1; i <= Config.ProgramCount; i++)
        {
            _items.Add(new ProgramRefItem
            {
                Number = i,
                Name = cam.ProgramNames.TryGetValue(i, out var n) ? n : "",
                Thumbnail = ReferenceStore.Load(ReferenceStore.Find(cam.ReferenceFolder, i), 200),
            });
        }
        UpdateFolderText();
    }

    /// <summary>Copies the program names typed in the list back into the camera being edited.</summary>
    private void FlushNames()
    {
        if (_refCam is null) return;
        _refCam.ProgramNames = _items.Where(i => !string.IsNullOrWhiteSpace(i.Name)).ToDictionary(i => i.Number, i => i.Name.Trim());
    }

    private void OnLoadImage(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProgramRefItem item || _refCam is null) return;
        var dlg = new OpenFileDialog
        {
            Title = $"{_refCam.Display} – " + Loc.F("S_ImgDlgTitle", item.Number),
            Filter = Loc.T("S_ImgFilter") + "|*.png;*.jpg;*.jpeg;*.bmp",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            ReferenceStore.Import(_refCam.ReferenceFolder, item.Number, dlg.FileName);
            item.Thumbnail = ReferenceStore.Load(ReferenceStore.Find(_refCam.ReferenceFolder, item.Number), 200);
            Log.Info($"Camera {_refCam.Number}: reference for program {item.Number} updated from {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error("Error copying reference image", ex);
            MessageBox.Show(this, Loc.F("S_CopyFail", ex.Message), AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRemoveImage(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ProgramRefItem item || item.Thumbnail is null || _refCam is null) return;
        if (MessageBox.Show(this, Loc.F("S_RemoveAsk", item.Number), AppName,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            ReferenceStore.Remove(_refCam.ReferenceFolder, item.Number);
            item.Thumbnail = null;
            Log.Info($"Camera {_refCam.Number}: reference for program {item.Number} removed");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.F("S_RemoveFail", ex.Message), AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Save / exit ----------

    private static bool ValidUrl(string s) =>
        Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        double D(TextBox b, string nameKey, double min, double max)
        {
            var s = b.Text.Trim().Replace(',', '.');
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= min && v <= max) return v;
            errors.Add(Loc.F("S_V_Range", Loc.T(nameKey), min, max));
            return min;
        }
        int I(TextBox b, string nameKey, int min, int max)
        {
            if (int.TryParse(b.Text.Trim(), out var v) && v >= min && v <= max) return v;
            errors.Add(Loc.F("S_V_Int", Loc.T(nameKey), min, max));
            return min;
        }

        var c = Config;
        FlushNames();

        // Cameras
        c.ActiveCamera = ActiveCameraCombo.SelectedIndex + 1;
        foreach (var cam in c.Cameras)
        {
            cam.Name = cam.Name.Trim();
            cam.Url = cam.Url.Trim();
            cam.ProgramTag = cam.ProgramTag.Trim();
            cam.OffsetXTag = cam.OffsetXTag.Trim();
            cam.OffsetYTag = cam.OffsetYTag.Trim();
            bool active = cam.Number == c.ActiveCamera;
            if ((active || cam.Url.Length > 0) && !ValidUrl(cam.Url))
                errors.Add(Loc.F("S_V_CamUrl", cam.Display));
        }

        c.ImageIntervalMs = I(ImgIntervalBox, "S_ImgInterval", 0, 10000);
        c.GhostOpacity = I(GhostOpacityBox, "S_GhostOpacity", 5, 95);
        c.GhostStyle = GhostStyleCombo.SelectedValue as string ?? "Watermark";
        c.CameraZoom = D(CameraZoomBox, "S_Zoom", 0.25, 5);
        c.CameraReloadMinutes = I(ReloadBox, "S_ReloadEvery", 0, 1440);
        c.Language = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "en";
        c.Layout = (LayoutCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Horizontal";
        c.ReferenceRatio = I(RatioBox, "S_RefPct", 20, 80) / 100.0;
        c.ReferenceFirst = RefFirstCheck.IsChecked == true;
        c.Kiosk = KioskCheck.IsChecked == true;
        c.Theme = ThemeCombo.SelectedValue as string ?? "Dark";
        if (NewPwdBox.Password.Length > 0) c.AdminPasswordHash = AppConfig.Hash(NewPwdBox.Password);

        var p = c.Plc;
        p.Enabled = PlcEnabledCheck.IsChecked == true;
        p.Ip = PlcIpBox.Text.Trim();
        p.PlcType = PlcTypeCombo.SelectedItem as string ?? "ControlLogix";
        p.Path = PlcPathBox.Text.Trim();
        p.ProgramType = ProgTypeCombo.SelectedItem as string ?? "DINT";
        p.ProgramOffset = I(ProgOffsetBox, "S_ProgOffset", -100, 100);
        p.PollMs = I(PollBox, "S_Poll", 50, 10000);
        p.ReadOffsets = ReadOffsetsCheck.IsChecked == true;
        p.OffsetXType = XTypeCombo.SelectedItem as string ?? "REAL";
        p.OffsetYType = YTypeCombo.SelectedItem as string ?? "REAL";
        p.ToleranceX = D(TolXBox, "S_TolX", 0, 1e6);
        p.ToleranceY = D(TolYBox, "S_TolY", 0, 1e6);
        p.Units = UnitsBox.Text.Trim();
        p.InvertX = InvXCheck.IsChecked == true;
        p.InvertY = InvYCheck.IsChecked == true;

        if (p.Enabled)
        {
            var cam = c.Cam;
            if (string.IsNullOrWhiteSpace(p.Ip)) errors.Add(Loc.T("S_V_PlcIp"));
            if (cam.ProgramTag.Length == 0) errors.Add($"{cam.Display}: " + Loc.T("S_V_ProgTag"));
            if (p.ReadOffsets && (cam.OffsetXTag.Length == 0 || cam.OffsetYTag.Length == 0))
                errors.Add($"{cam.Display}: " + Loc.T("S_V_Offsets"));
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this, Loc.T("S_Check") + "\n• " + string.Join("\n• ", errors), AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void OnExitApp(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, Loc.T("S_ExitAsk"), AppName, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ExitRequested = true;
        DialogResult = false;
    }
}
