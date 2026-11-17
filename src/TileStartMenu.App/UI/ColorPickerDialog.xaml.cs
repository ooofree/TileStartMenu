using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TileStartMenu.App.UI;

/// <summary>颜色选择对话框：Metro 预设色板 + RGB 滑块自定义取色。</summary>
public partial class ColorPickerDialog : Window
{
    private bool _syncing;

    public ColorPickerDialog()
    {
        InitializeComponent();
        BuildPresets();
        RedSlider.ValueChanged += (_, _) => OnSliderChanged();
        GreenSlider.ValueChanged += (_, _) => OnSliderChanged();
        BlueSlider.ValueChanged += (_, _) => OnSliderChanged();
        HexBox.LostFocus += (_, _) => ApplyHex();
        HexBox.KeyDown += (s, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter) ApplyHex();
        };
        OkButton.Click += (_, _) => { DialogResult = true; Close(); };
    }

    /// <summary>用户确认后的颜色（#RRGGBB）。</summary>
    public string SelectedColor { get; private set; } = "#0078D4";

    public static string? Show(Window owner, string currentColor)
    {
        var dlg = new ColorPickerDialog { Owner = owner, SelectedColor = currentColor };
        dlg.LoadColor(currentColor);
        return dlg.ShowDialog() == true ? dlg.SelectedColor : null;
    }

    private void BuildPresets()
    {
        foreach (var hex in MetroColors.Presets)
        {
            var br = MetroColors.ToBrush(hex);
            var btn = new Button
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(2),
                Background = br,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                BorderThickness = new Thickness(1),
                Tag = hex
            };
            btn.Click += (_, _) => LoadColor((string)btn.Tag);
            PresetPanel.Children.Add(btn);
        }
    }

    private void LoadColor(string hex)
    {
        var c = MetroColors.Parse(hex);
        _syncing = true;
        RedSlider.Value = c.R;
        GreenSlider.Value = c.G;
        BlueSlider.Value = c.B;
        _syncing = false;
        UpdatePreview();
    }

    private void OnSliderChanged()
    {
        if (_syncing) return;
        UpdatePreview();
    }

    private void ApplyHex()
    {
        var text = HexBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(text);
            _syncing = true;
            RedSlider.Value = c.R;
            GreenSlider.Value = c.G;
            BlueSlider.Value = c.B;
            _syncing = false;
            UpdatePreview();
        }
        catch
        {
            UpdatePreview(); // 非法输入则回显当前颜色
        }
    }

    private void UpdatePreview()
    {
        var c = Color.FromRgb((byte)RedSlider.Value, (byte)GreenSlider.Value, (byte)BlueSlider.Value);
        Preview.Background = new SolidColorBrush(c);
        SelectedColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        HexBox.Text = SelectedColor;
    }
}
