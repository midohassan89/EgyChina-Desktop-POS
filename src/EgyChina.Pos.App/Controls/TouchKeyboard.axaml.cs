using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace EgyChina.Pos.App.Controls;

public enum TouchKeyboardMode
{
    Text,
    Numeric,
}

public enum TouchKeyboardLanguage
{
    Arabic,
    English,
}

public sealed partial class TouchKeyboard : UserControl
{
    private static readonly string[] EnglishRows =
    [
        "1234567890",
        "qwertyuiop",
        "asdfghjkl",
        "zxcvbnm",
    ];

    private static readonly string[] ArabicRows =
    [
        "١٢٣٤٥٦٧٨٩٠",
        "ضصثقفغعهخحجد",
        "شسيبلاتنمكط",
        "ئءؤرلاىةوزظ",
    ];

    private TouchKeyboardMode _mode = TouchKeyboardMode.Text;
    private TouchKeyboardLanguage _language = TouchKeyboardLanguage.Arabic;
    private bool _shift;
    private bool _embedded;

    public TouchKeyboard()
    {
        InitializeComponent();
        Rebuild();
    }

    /// <summary>
    /// Inline pad inside a dialog: hide language/dismiss chrome; keep Enter.
    /// </summary>
    public void SetEmbedded(bool embedded)
    {
        if (_embedded == embedded)
            return;
        _embedded = embedded;
        ApplyChrome();
    }

    public event Action<string>? TextTyped;
    public event Action? BackspacePressed;
    public event Action? EnterPressed;
    public event Action? HideRequested;

    public void SetMode(TouchKeyboardMode mode)
    {
        if (_mode == mode)
            return;
        _mode = mode;
        _shift = false;
        Rebuild();
    }

    public void PreferLanguage(TouchKeyboardLanguage language)
    {
        if (_language == language)
            return;
        _language = language;
        if (_mode == TouchKeyboardMode.Text)
            Rebuild();
    }

    private void OnToggleLanguage(object? sender, RoutedEventArgs e)
    {
        _language = _language == TouchKeyboardLanguage.Arabic
            ? TouchKeyboardLanguage.English
            : TouchKeyboardLanguage.Arabic;
        _shift = false;
        Rebuild();
    }

    private void OnHide(object? sender, RoutedEventArgs e) => HideRequested?.Invoke();

    private void OnEnter(object? sender, RoutedEventArgs e) => EnterPressed?.Invoke();

    private void Rebuild()
    {
        TitleBlock.Text = _mode == TouchKeyboardMode.Numeric
            ? (_embedded ? "أرقام" : "لوحة أرقام · Number pad")
            : _language == TouchKeyboardLanguage.Arabic
                ? "لوحة مفاتيح عربية · Arabic"
                : "English keyboard · لوحة إنجليزية";
        LanguageButton.Content = _language == TouchKeyboardLanguage.Arabic ? "EN" : "ع";
        ApplyChrome();

        RowsHost.Children.Clear();
        if (_mode == TouchKeyboardMode.Numeric)
            BuildNumeric();
        else
            BuildText();
    }

    private void ApplyChrome()
    {
        LanguageButton.IsVisible = !_embedded && _mode == TouchKeyboardMode.Text;
        HideButton.IsVisible = !_embedded;
        EnterButton.Content = _embedded ? "التالي" : "Enter ↵";
        EnterButton.MinWidth = _embedded ? 88 : 100;
    }

    private void BuildNumeric()
    {
        AddGridRow(["7", "8", "9"]);
        AddGridRow(["4", "5", "6"]);
        AddGridRow(["1", "2", "3"]);
        var bottom = CreateRowGrid(4);
        bottom.Children.Add(MakeKey("0", 0, () => Emit("0")));
        bottom.Children.Add(MakeKey(".", 1, () => Emit(".")));
        bottom.Children.Add(MakeKey("00", 2, () => Emit("00")));
        bottom.Children.Add(MakeKey("⌫", 3, () => BackspacePressed?.Invoke(), accent: true));
        RowsHost.Children.Add(bottom);
    }

    private void BuildText()
    {
        var rows = _language == TouchKeyboardLanguage.Arabic ? ArabicRows : EnglishRows;
        for (var r = 0; r < rows.Length; r++)
        {
            var chars = rows[r];
            var isLast = r == rows.Length - 1;
            var extra = 0;
            if (isLast && _language == TouchKeyboardLanguage.English)
                extra++;
            if (isLast)
                extra++;

            var grid = CreateRowGrid(chars.Length + extra);
            var col = 0;
            if (isLast && _language == TouchKeyboardLanguage.English)
            {
                grid.Children.Add(MakeKey(_shift ? "⇧" : "shift", col++, ToggleShift, accent: true));
            }

            foreach (var ch in chars)
            {
                var glyph = _language == TouchKeyboardLanguage.English && _shift
                    ? char.ToUpperInvariant(ch).ToString()
                    : ch.ToString();
                grid.Children.Add(MakeKey(glyph, col++, () => Emit(glyph)));
            }

            if (isLast)
                grid.Children.Add(MakeKey("⌫", col, () => BackspacePressed?.Invoke(), accent: true));

            RowsHost.Children.Add(grid);
        }

        var space = CreateRowGrid(6);
        space.Children.Add(MakeKey(_language == TouchKeyboardLanguage.Arabic ? "مسافة" : "space", 0, () => Emit(" "), span: 4));
        space.Children.Add(MakeKey(",", 4, () => Emit(",")));
        space.Children.Add(MakeKey("-", 5, () => Emit("-")));
        RowsHost.Children.Add(space);
    }

    private void AddGridRow(string[] keys)
    {
        var grid = CreateRowGrid(keys.Length);
        for (var i = 0; i < keys.Length; i++)
        {
            var text = keys[i];
            grid.Children.Add(MakeKey(text, i, () => Emit(text)));
        }

        RowsHost.Children.Add(grid);
    }

    private static Grid CreateRowGrid(int columns)
    {
        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ColumnDefinitions = new ColumnDefinitions(),
        };
        for (var i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        return grid;
    }

    private Button MakeKey(string label, int column, Action onClick, bool accent = false, int span = 1)
    {
        var button = new Button
        {
            Content = label,
            Classes = { "osk-key" },
            Margin = new Avalonia.Thickness(2, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (accent)
            button.Classes.Add("accent");
        if (label.Length > 2)
            button.FontSize = 15;
        button.Click += (_, _) => onClick();
        Grid.SetColumn(button, column);
        if (span > 1)
            Grid.SetColumnSpan(button, span);
        return button;
    }

    private void ToggleShift()
    {
        _shift = !_shift;
        Rebuild();
    }

    private void Emit(string text)
    {
        TextTyped?.Invoke(text);
        if (_shift && _language == TouchKeyboardLanguage.English && text.Length == 1 && char.IsLetter(text[0]))
        {
            _shift = false;
            Rebuild();
        }
    }
}
