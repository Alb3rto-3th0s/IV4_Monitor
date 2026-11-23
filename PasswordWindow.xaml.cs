using System.Windows;
using System.Windows.Input;

namespace VisorIV4;

public partial class PasswordWindow : Window
{
    private readonly AppConfig _cfg;

    public PasswordWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        Loaded += (_, _) => Pwd.Focus();
    }

    private void OnKey(object sender, KeyEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (_cfg.CheckPassword(Pwd.Password))
        {
            DialogResult = true;
            return;
        }
        Log.Info("Administration access attempt with wrong password");
        ErrorText.Visibility = Visibility.Visible;
        Pwd.Clear();
        Pwd.Focus();
    }
}
