using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PathShortener.Core.Ai;
using PathShortener.Core.Cache;

namespace PathShortener.Gui;

/// <summary>Маленький редактор одного запису словника (оригінал + скорочення).</summary>
public sealed class DictEntryDialog : Window
{
    private readonly TextBox _orig = new();
    private readonly TextBox _short = new();
    private bool _ok;

    private DictEntryDialog(string orig, string shortForm)
    {
        Title = Loc.T("dict.entry.title");
        Width = 520; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _orig.Text = orig; _short.Text = shortForm;

        var ok = new Button { Content = Loc.T("button.ok"), IsDefault = true, Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { _ok = true; Close(); };
        var cancel = new Button { Content = Loc.T("button.cancel"), IsCancel = true, Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16), Spacing = 6,
            Children =
            {
                new TextBlock { Text = Loc.T("dict.entry.original") }, _orig,
                new TextBlock { Text = Loc.T("dict.entry.short") }, _short,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 12, 0, 0),
                    Children = { ok, cancel } }
            }
        };
    }

    public static async Task<(bool Ok, string Original, string Short)> Show(Window owner, string orig, string shortForm)
    {
        var dlg = new DictEntryDialog(orig, shortForm);
        await dlg.ShowDialog(owner);
        return (dlg._ok, dlg._orig.Text?.Trim() ?? "", dlg._short.Text?.Trim() ?? "");
    }
}

/// <summary>Діалог вибору цілі для папки: перейменувати на місці або скопіювати в іншу теку.</summary>
public sealed class TargetDialog : Window
{
    private readonly RadioButton _inPlace;
    private readonly RadioButton _copyTo;
    private readonly TextBox _path;

    public TargetKind ResultKind { get; private set; } = TargetKind.InPlace;
    public string? ResultPath { get; private set; }

    public TargetDialog(TargetKind current, string? currentPath)
    {
        Title = Loc.T("target.dialog.title");
        Width = 600; SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _inPlace = new RadioButton
        {
            Content = Loc.T("target.dialog.inPlace"),
            IsChecked = current == TargetKind.InPlace
        };
        _copyTo = new RadioButton
        {
            Content = Loc.T("target.dialog.copyTo"),
            IsChecked = current == TargetKind.CopyTo
        };
        _path = new TextBox { IsReadOnly = true, Text = currentPath ?? "", Margin = new Avalonia.Thickness(28, 4, 0, 0) };

        var browse = new Button { Content = Loc.T("button.choose"), Margin = new Avalonia.Thickness(8, 4, 0, 0) };
        browse.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            { Title = Loc.T("target.dialog.browse.title") });
            if (folders.Count > 0)
            {
                _path.Text = folders[0].TryGetLocalPath() ?? "";
                _copyTo.IsChecked = true;
            }
        };

        var ok = new Button { Content = Loc.T("button.ok"), IsDefault = true, Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += async (_, _) =>
        {
            if (_copyTo.IsChecked == true && string.IsNullOrWhiteSpace(_path.Text))
            {
                await Msg.Info(this, Loc.T("target.dialog.needPath"));
                return;
            }
            ResultKind = _copyTo.IsChecked == true ? TargetKind.CopyTo : TargetKind.InPlace;
            ResultPath = _copyTo.IsChecked == true ? _path.Text : null;
            Close(true);
        };
        var cancel = new Button { Content = Loc.T("button.cancel"), IsCancel = true, Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(false);

        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_path, 0); Grid.SetColumn(browse, 1);
        pathRow.Children.Add(_path); pathRow.Children.Add(browse);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 16, 0, 0)
        };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Spacing = 6,
            Children = { _inPlace, _copyTo, pathRow, buttons }
        };
    }
}

/// <summary>Прості модальні повідомлення (Avalonia не має вбудованого MessageBox).</summary>
public static class Msg
{
    public static Task Info(Window owner, string text, string? title = null)
        => Show(owner, text, title ?? Loc.T("msg.title.warning"), yesNo: false);

    public static async Task<bool> YesNo(Window owner, string text, string? title = null)
        => await Show(owner, text, title ?? Loc.T("msg.title.question"), yesNo: true);

    /// <summary>Три варіанти: повертає 0 (перший), 1 (другий) або 2 (третій/скасувати).</summary>
    public static async Task<int> ThreeWay(Window owner, string text, string title, string a, string b, string c)
    {
        var dlg = new Window
        {
            Title = title, Width = 520, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        int result = 2;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 16, 0, 0)
        };
        var b0 = new Button { Content = a, IsDefault = true }; b0.Click += (_, _) => { result = 0; dlg.Close(); };
        var b1 = new Button { Content = b }; b1.Click += (_, _) => { result = 1; dlg.Close(); };
        var b2 = new Button { Content = c, IsCancel = true }; b2.Click += (_, _) => { result = 2; dlg.Close(); };
        buttons.Children.Add(b0); buttons.Children.Add(b1); buttons.Children.Add(b2);
        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, buttons }
        };
        await dlg.ShowDialog(owner);
        return result;
    }

    private static async Task<bool> Show(Window owner, string text, string title, bool yesNo)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 480, SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 16, 0, 0)
        };
        if (yesNo)
        {
            var yes = new Button { Content = Loc.T("button.yes"), IsDefault = true, Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
            var no = new Button { Content = Loc.T("button.no"), IsCancel = true, Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
            yes.Click += (_, _) => dlg.Close(true);
            no.Click += (_, _) => dlg.Close(false);
            buttons.Children.Add(yes); buttons.Children.Add(no);
        }
        else
        {
            var ok = new Button { Content = Loc.T("button.ok"), IsDefault = true, IsCancel = true, Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Click += (_, _) => dlg.Close(false);
            buttons.Children.Add(ok);
        }

        dlg.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                buttons
            }
        };
        return await dlg.ShowDialog<bool>(owner);
    }
}

/// <summary>Редактор профілю ШІ-провайдера: назва, ключ, baseUrl, модель, примітка.</summary>
public sealed class ProfileDialog : Window
{
    private readonly AiProfile _profile;
    private readonly TextBox _name = new();
    private readonly TextBox _key = new() { PasswordChar = '•' };
    private readonly TextBox _baseUrl = new();
    private readonly ComboBox _model = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _notes = new();

    public ProfileDialog(AiProfile profile)
    {
        _profile = profile;
        Title = Loc.T("profile.dialog.title");
        Width = 560; SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        bool needsKey = profile.Provider is AiProvider.Claude or AiProvider.OpenAI
                        or AiProvider.Grok or AiProvider.Gemini;
        bool needsUrl = profile.Provider is AiProvider.OllamaRemote or AiProvider.Grok;

        _name.Text = profile.ProfileName;
        _key.Text = profile.ApiKey;
        _baseUrl.Text = profile.BaseUrl ?? AiChatModel.DefaultBaseUrl(profile.Provider);
        _notes.Text = profile.Notes;
        _model.ItemsSource = AiChatModel.DefaultModels(profile.Provider).ToList();
        _model.IsEditable = true;
        _model.SelectedItem = profile.SelectedModel.Length > 0 ? profile.SelectedModel : null;
        if (profile.SelectedModel.Length > 0) _model.Text = profile.SelectedModel;

        var panel = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 6 };
        panel.Children.Add(Row(Loc.T("profile.name"), _name));
        if (needsKey) panel.Children.Add(Row(Loc.T("profile.key"), _key));
        if (needsUrl) panel.Children.Add(Row(Loc.T("profile.baseUrl"), _baseUrl));
        panel.Children.Add(Row(Loc.T("profile.model"), _model));
        panel.Children.Add(Row(Loc.T("profile.notes"), _notes));

        var ok = new Button { Content = Loc.T("button.ok"), IsDefault = true, Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => { Commit(); Close(true); };
        var cancel = new Button { Content = Loc.T("button.cancel"), IsCancel = true, Width = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close(false);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Avalonia.Thickness(0, 12, 0, 0),
            Children = { ok, cancel }
        });

        Content = panel;
    }

    private void Commit()
    {
        _profile.ProfileName = _name.Text ?? "";
        _profile.ApiKey = _key.Text ?? "";
        _profile.BaseUrl = string.IsNullOrWhiteSpace(_baseUrl.Text) ? null : _baseUrl.Text;
        _profile.SelectedModel = _model.Text ?? "";
        _profile.Notes = _notes.Text ?? "";
    }

    private static Control Row(string label, Control input)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("160,*"), Margin = new Avalonia.Thickness(0, 2, 0, 2) };
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(l, 0); Grid.SetColumn(input, 1);
        g.Children.Add(l); g.Children.Add(input);
        return g;
    }
}

/// <summary>Вікно «Про програму»: опис, автор, контакти, ліцензія MIT.</summary>
public sealed class AboutDialog : Window
{
    public AboutDialog()
    {
        Title = Loc.T("about.title");
        Width = 560; SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        string version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "1.5.0";

        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        links.Children.Add(LinkButton("teraxis@gmail.com", "mailto:teraxis@gmail.com"));
        links.Children.Add(LinkButton("github.com/teraxis", "https://github.com/teraxis"));

        var ok = new Button
        {
            Content = Loc.T("button.ok"), IsDefault = true, IsCancel = true, Width = 100,
            HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Avalonia.Thickness(0, 16, 0, 0)
        };
        ok.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = Loc.T("about.name"), FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = Loc.F("about.version", version), FontSize = 12 },
                new TextBlock { Text = Loc.T("about.description"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = Loc.T("about.features"), TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                new TextBlock { Text = Loc.T("about.author"), FontWeight = FontWeight.SemiBold, Margin = new Avalonia.Thickness(0, 6, 0, 0) },
                links,
                new TextBlock { Text = Loc.T("about.license"), TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                ok
            }
        };
    }

    private static Button LinkButton(string text, string url)
    {
        var b = new Button
        {
            Content = text,
            Background = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Padding = new Avalonia.Thickness(2),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        b.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return b;
    }
}
