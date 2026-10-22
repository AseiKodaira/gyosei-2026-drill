using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Kiroku.App;

/// <summary>画面部品の共通設定(大きな文字・大きなボタン・装飾なし)。</summary>
internal static class Ui
{
    public static readonly FontFamily Font = new("Yu Gothic UI, Meiryo UI, Segoe UI");
    public static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF8));
    public static readonly Brush Header = new SolidColorBrush(Color.FromRgb(0x2B, 0x4C, 0x7E));
    public static readonly Brush Primary = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0xB5));
    public static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x1B, 0x7F, 0x3B));
    public static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xB3, 0x5C, 0x00));
    public static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x5F, 0x6B, 0x7A));
    public static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0xC9, 0xD1, 0xDA));
    public static readonly Brush White = Brushes.White;

    public static TextBlock Text(string t, double size = 16, bool bold = false, Brush? fg = null, bool wrap = true) => new()
    {
        Text = t, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Foreground = fg ?? Brushes.Black,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button Btn(string text, Action onClick, bool primary = false, double minWidth = 110, Brush? bg = null)
    {
        var b = new Button
        {
            Content = text, FontSize = 16, MinWidth = minWidth, MinHeight = 44, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(4),
            Foreground = primary || bg != null ? Brushes.White : Brushes.Black, Background = bg ?? (primary ? Primary : White),
            BorderBrush = Line, Cursor = System.Windows.Input.Cursors.Hand,
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static TextBox Box(bool multi = false, double minHeight = 38) => new()
    {
        FontSize = 17, MinHeight = minHeight, Padding = new Thickness(6, 4, 6, 4), VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center,
        AcceptsReturn = multi, TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap, AcceptsTab = false,
        VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, BorderBrush = Line,
    };

    public static StackPanel Field(string label, UIElement input, string? hint = null, UIElement? headerExtra = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        if (headerExtra == null) sp.Children.Add(Text(label, 14, true, Muted));
        else
        {
            var h = new StackPanel { Orientation = Orientation.Horizontal };
            h.Children.Add(Text(label, 14, true, Muted)); h.Children.Add(headerExtra);
            sp.Children.Add(h);
        }
        sp.Children.Add(input);
        if (hint != null) sp.Children.Add(Text(hint, 12, false, Muted));
        return sp;
    }

    public static Border Card(UIElement child, Thickness? margin = null) => new()
    {
        Background = White, BorderBrush = Line, BorderThickness = new Thickness(1), Padding = new Thickness(12), Margin = margin ?? new Thickness(0), Child = child,
    };

    public static void Style(Window w, string title, double width, double height)
    {
        w.Title = title; w.Width = width; w.Height = height; w.FontFamily = Font; w.FontSize = 16; w.Background = Bg;
        w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.UseLayoutRounding = true;
    }

    public static void Info(Window? owner, string msg, string title = "お知らせ") => Show(owner, msg, title, MessageBoxImage.Information);
    public static void Error(Window? owner, string msg, string title = "エラー") => Show(owner, msg, title, MessageBoxImage.Warning);
    private static void Show(Window? o, string m, string t, MessageBoxImage i)
    {
        if (o != null) MessageBox.Show(o, m, t, MessageBoxButton.OK, i); else MessageBox.Show(m, t, MessageBoxButton.OK, i);
    }
    public static bool Ask(Window? o, string msg, string title = "確認") =>
        (o != null ? MessageBox.Show(o, msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question) : MessageBox.Show(msg, title, MessageBoxButton.YesNo, MessageBoxImage.Question)) == MessageBoxResult.Yes;

    /// <summary>ボタンを選ばせる確認ダイアログ。戻り値は押したボタンの番号(閉じたら -1)。</summary>
    public static int Choose(Window? owner, string title, string message, params string[] buttons)
    {
        var w = new Window { Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, MaxWidth = 640, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        Style(w, title, double.NaN, double.NaN); w.SizeToContent = SizeToContent.WidthAndHeight;
        if (owner != null) w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(Text(message, 16));
        var row = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var result = -1;
        for (var i = 0; i < buttons.Length; i++)
        {
            var idx = i;
            row.Children.Add(Btn(buttons[i], () => { result = idx; w.Close(); }, primary: i == 0, minWidth: 300));
        }
        sp.Children.Add(row);
        w.Content = sp;
        w.ShowDialog();
        return result;
    }

    /// <summary>入力欄つきダイアログ。キャンセルは null。</summary>
    public static string[]? Prompt(Window? owner, string title, string message, params (string Label, bool Secret, string Initial)[] fields)
    {
        var w = new Window { Owner = owner, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };
        Style(w, title, 520, double.NaN); w.SizeToContent = SizeToContent.Height;
        if (owner != null) w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var sp = new StackPanel { Margin = new Thickness(20) };
        if (message != "") sp.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), FontSize = 15 });
        var inputs = new List<Control>();
        foreach (var f in fields)
        {
            Control c = f.Secret ? new PasswordBox { FontSize = 17, MinHeight = 38, Padding = new Thickness(6, 4, 6, 4) } : Box();
            if (!f.Secret) ((TextBox)c).Text = f.Initial;
            inputs.Add(c); sp.Children.Add(Field(f.Label, c));
        }
        string[]? result = null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        void Ok()
        {
            result = inputs.Select(c => c is PasswordBox p ? p.Password : ((TextBox)c).Text).ToArray();
            w.Close();
        }
        row.Children.Add(Btn("キャンセル", () => w.Close()));
        row.Children.Add(Btn("OK", Ok, primary: true));
        sp.Children.Add(row);
        w.Content = sp;
        w.Loaded += (_, _) => inputs.FirstOrDefault()?.Focus();
        w.ShowDialog();
        return result;
    }
}

/// <summary>画面共通: エラーを日本語で表示する実行ヘルパー。</summary>
internal abstract class AppWindow : Window
{
    protected async void Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Kiroku.Core.KirokuException e) { Ui.Error(this, e.Message); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Ui.Error(this, "想定外のエラーが起きました。もう一度お試しください。続く場合は管理者に連絡してください。\n(" + e.GetType().Name + ")");
        }
    }
}
