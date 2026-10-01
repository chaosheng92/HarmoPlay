using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HarmoPlay.Core;
using HarmoPlay.Models;

namespace HarmoPlay.Views;

public partial class KeyMapWindow : Window
{
    private static readonly string[] StandardPrefixes = { "", "#", "b", "^", "#b", "#^", "'", "," };
    private static readonly string[] StandardLabels = { "本音", "半音", "降调", "升调", "半音+降调", "半音+升调", "高音", "低音" };
    private static readonly string[] StandardColors = { "#FFFFFF", "#B06CF0", "#4CD964", "#4A9DFF", "#FFD23F", "#FF5A5A", "#4A9DFF", "#4CD964" };

    private sealed class KeyRow
    {
        public TextBox Box = null!;
        public string Original = "";
    }

    private sealed class ModRow
    {
        public string Prefix = "";
        public TextBox Label = null!;
        public CheckBox Left = null!, Right = null!, Middle = null!;
        public CheckBox Ctrl = null!, Alt = null!, Shift = null!;
        public TextBox Color = null!;
    }

    private readonly List<KeyRow> _keyRows = new();
    private readonly List<ModRow> _modRows = new();
    private bool _edited;
    private bool _building = true;

    public KeyMapWindow(KeyMap source, string presetId)
    {
        InitializeComponent();

        ComboPreset.ItemsSource = KeyMap.Presets().Select(m => new { m.Id, m.Name })
            .Concat(new[] { new { Id = "custom", Name = "自定义（可编辑）" } }).ToList();
        ComboPreset.DisplayMemberPath = "Name";
        ComboPreset.SelectedValuePath = "Id";
        ComboPreset.SelectedValue = presetId == "custom" ? "delta" : presetId;
        if (ComboPreset.SelectedIndex < 0) ComboPreset.SelectedIndex = 0;

        Build(source);
        _building = false;
    }

    public KeyMap ResultMap { get; private set; } = KeyMap.CreateDelta();
    public bool ResultIsCustom { get; private set; }

    private KeyMap CurrentPreset()
    {
        var id = ComboPreset.SelectedValue as string ?? "delta";
        if (id == "custom") id = "delta";
        return KeyMap.ById(id, KeyMap.CreateDelta());
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_building) return;
        Build(CurrentPreset());
        _edited = true;
    }

    private void OnReloadPreset(object sender, RoutedEventArgs e)
    {
        Build(CurrentPreset());
        _edited = true;
        TxtNote.Text = CurrentPreset().Note;
    }

    private void Build(KeyMap map)
    {
        _keyRows.Clear();
        _modRows.Clear();
        KeyPanel.Children.Clear();
        ModPanel.Children.Clear();

        TxtNote.Text = map.Note;

        // ---- 八个琴键 ----
        for (int i = 0; i < 8; i++)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var cap = new TextBlock
            {
                Text = i == 7 ? "简谱 8（高音 1）" : $"简谱 {i + 1}",
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(cap, 0);
            grid.Children.Add(cap);

            var box = new TextBox
            {
                Text = map.Keys.Count > i ? map.Keys[i] : "Z",
                Width = 130,
                VerticalAlignment = VerticalAlignment.Center,
            };
            string original = box.Text;
            box.TextChanged += (_, _) => _edited = true;
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            var btn = new Button
            {
                Content = "捕获按键",
                VerticalAlignment = VerticalAlignment.Center,
            };
            var localBox = box;
            btn.Click += (_, _) =>
            {
                var dlg = new HotkeyCaptureWindow(new HotkeySpec { Key = localBox.Text, Modifiers = "" }) { Owner = this };
                if (dlg.ShowDialog() == true)
                {
                    localBox.Text = dlg.CapturedKey.ToString();
                    _edited = true;
                }
            };
            Grid.SetColumn(btn, 2);
            grid.Children.Add(btn);

            var hint = new TextBlock
            {
                Text = i == 7 ? "（默认 , 键）" : "",
                Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0xA0, 0xB5)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(hint, 3);
            grid.Children.Add(hint);

            KeyPanel.Children.Add(grid);
            _keyRows.Add(new KeyRow { Box = box, Original = original });
        }

        // ---- 变调修饰 ----
        for (int i = 0; i < StandardPrefixes.Length; i++)
        {
            var prefix = StandardPrefixes[i];
            var mod = map.Mods.FirstOrDefault(m => KeyMap.Normalize(m.Prefix) == prefix)
                      ?? new AccidentalMod { Prefix = prefix, Label = StandardLabels[i], Color = StandardColors[i] };

            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            for (int c = 0; c < 9; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = c == 1 ? new GridLength(120) : c == 8 ? new GridLength(60) : GridLength.Auto,
                });

            var p = new TextBlock
            {
                Text = string.IsNullOrEmpty(prefix) ? "本音" : prefix,
                FontFamily = new FontFamily("Consolas"),
                Width = 50,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(p, 0);
            grid.Children.Add(p);

            var label = new TextBox { Text = mod.Label, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            label.TextChanged += (_, _) => _edited = true;
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            CheckBox Mk(string text, bool value, int col)
            {
                var cb = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                cb.Click += (_, _) => _edited = true;
                Grid.SetColumn(cb, col);
                grid.Children.Add(cb);
                return cb;
            }

            var left = Mk("左键", mod.MouseLeft, 2);
            var middle = Mk("中键", mod.MouseMiddle, 3);
            var right = Mk("右键", mod.MouseRight, 4);
            var ctrl = Mk("Ctrl", mod.Ctrl, 5);
            var alt = Mk("Alt", mod.Alt, 6);
            var shift = Mk("Shift", mod.Shift, 7);

            var color = new TextBox { Text = mod.Color, Width = 60, VerticalAlignment = VerticalAlignment.Center };
            color.TextChanged += (_, _) => _edited = true;
            Grid.SetColumn(color, 8);
            grid.Children.Add(color);

            ModPanel.Children.Add(grid);
            _modRows.Add(new ModRow
            {
                Prefix = prefix,
                Label = label,
                Left = left,
                Middle = middle,
                Right = right,
                Ctrl = ctrl,
                Alt = alt,
                Shift = shift,
                Color = color,
            });
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var map = new KeyMap { Id = "custom", Name = "自定义映射" };
        var bad = new List<string>();

        for (int i = 0; i < _keyRows.Count; i++)
        {
            var text = _keyRows[i].Box.Text.Trim();
            if (!Enum.TryParse<Key>(text, true, out var key) || key == Key.None)
            {
                bad.Add($"第 {i + 1} 个琴键「{text}」不是有效的键名（例如 Z / OemComma / D1）");
                map.Keys.Add("Z");
                continue;
            }
            map.Keys.Add(key.ToString());
        }

        foreach (var row in _modRows)
        {
            var hex = row.Color.Text.Trim();
            if (!hex.StartsWith('#')) hex = "#" + hex;
            try
            {
                ColorConverter.ConvertFromString(hex);
            }
            catch
            {
                hex = "#FFFFFF";
            }
            map.Mods.Add(new AccidentalMod
            {
                Prefix = row.Prefix,
                Label = string.IsNullOrWhiteSpace(row.Label.Text) ? row.Prefix : row.Label.Text.Trim(),
                MouseLeft = row.Left.IsChecked == true,
                MouseMiddle = row.Middle.IsChecked == true,
                MouseRight = row.Right.IsChecked == true,
                Ctrl = row.Ctrl.IsChecked == true,
                Alt = row.Alt.IsChecked == true,
                Shift = row.Shift.IsChecked == true,
                Color = hex,
            });
        }

        if (bad.Count > 0)
        {
            MessageBox.Show(string.Join("\n", bad), "键位映射有问题", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ResultMap = map;
        ResultIsCustom = _edited || (ComboPreset.SelectedValue as string) == "custom";
        if (!ResultIsCustom)
        {
            ResultMap = CurrentPreset().Clone();
        }
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
