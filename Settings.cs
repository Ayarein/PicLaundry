using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace PicLaundry;

internal sealed record Settings(string Folder, bool Images, bool Paused, ModifierKeys Modifiers, Key Key, bool AlwaysShow = false)
{
    internal static Settings Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? Default()
        : Default();

    private static Settings Default() => new(Native.ScreenshotsFolder(), false, false,
        ModifierKeys.Control | ModifierKeys.Alt, Key.Space);

    internal void Save(string path)
    {
        string pending = path + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(this));
        File.Move(pending, path, true);
    }
}

internal sealed class SettingsWindow : Window
{
    internal SettingsWindow(Settings current, Func<Settings, string?> save)
    {
        Title = "PicLaundry 设置";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var fields = new StackPanel { Margin = new Thickness(20) };
        Content = fields;
        var folder = new TextBox { Text = current.Folder, Margin = new Thickness(0, 5, 0, 6) };
        var choose = new Button { Content = "选择目录…", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 3, 8, 3) };
        choose.Click += (_, _) =>
        {
            using var dialog = new Forms.FolderBrowserDialog { SelectedPath = folder.Text };
            if (dialog.ShowDialog(new DialogOwner(new System.Windows.Interop.WindowInteropHelper(this).Handle)) == Forms.DialogResult.OK)
                folder.Text = dialog.SelectedPath;
        };
        fields.Children.Add(new TextBlock { Text = "截图保存目录", FontWeight = FontWeights.SemiBold });
        fields.Children.Add(folder);
        fields.Children.Add(choose);
        fields.Children.Add(new TextBlock
        {
            Text = "请将截图工具的自动保存目录设为此目录。关闭自动保存和图片收集后，只进剪贴板的截图不会自动上绳。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 15)
        });
        var images = new CheckBox { Content = "收集剪贴板图片", IsChecked = current.Images };
        fields.Children.Add(images);
        var alwaysShow = new CheckBox
        {
            Content = "一直显示晾衣绳",
            IsChecked = current.AlwaysShow,
            Margin = new Thickness(0, 6, 0, 13)
        };
        fields.Children.Add(alwaysShow);
        fields.Children.Add(new TextBlock { Text = "展开或收起的快捷键", FontWeight = FontWeights.SemiBold });
        var shortcut = new TextBox { IsReadOnly = true, Margin = new Thickness(0, 5, 0, 7) };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick };
        ModifierKeys modifiers = current.Modifiers;
        Key key = current.Key;
        void ShowShortcut() => shortcut.Text = modifiers.ToString().Replace("Control", "Ctrl").Replace(", ", " + ") + " + " + key;
        ShowShortcut();
        shortcut.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var pressed = e.Key == Key.System ? e.SystemKey : e.Key;
            if (pressed is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0
                || KeyInterop.VirtualKeyFromKey(pressed) == 0)
            {
                error.Text = "请搭配 Ctrl、Alt 或 Win 键。";
                return;
            }
            modifiers = Keyboard.Modifiers;
            key = pressed;
            error.Text = "";
            ShowShortcut();
        };
        fields.Children.Add(shortcut);
        fields.Children.Add(error);
        var apply = new Button { Content = "保存", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 9, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        apply.Click += (_, _) =>
        {
            try
            {
                string path = Path.GetFullPath(folder.Text.Trim());
                var problem = save(new Settings(path, images.IsChecked == true,
                    current.Paused, modifiers, key, alwaysShow.IsChecked == true));
                if (problem == null) Close();
                else error.Text = problem;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                error.Text = "无法保存设置。请检查目录和写入权限。";
            }
        };
        fields.Children.Add(apply);
    }

    private sealed record DialogOwner(nint Handle) : Forms.IWin32Window;
}
