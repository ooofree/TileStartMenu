using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TileStartMenu.Core.Models;

namespace TileStartMenu.App.UI;

/// <summary>添加磁贴对话框：选类型 / 目标 / 名称 / 颜色 / 尺寸 / 分组。</summary>
public partial class AddTileDialog : Window
{
    private readonly List<TileGroup> _groups;
    private string _color;

    private static readonly (TileActionKind Kind, string Label)[] KindOptions =
    {
        (TileActionKind.Launch,  "程序（.exe 等可执行文件）"),
        (TileActionKind.File,    "文件（用默认程序打开）"),
        (TileActionKind.Folder,  "文件夹"),
        (TileActionKind.Url,     "网址（http/https）"),
        (TileActionKind.Shell,   "Shell 目标（如 ms-settings:）")
    };

    private static readonly (TileSize Size, string Label)[] SizeOptions =
    {
        (TileSize.Small,  "小（1×1）"),
        (TileSize.Medium, "中（2×2）"),
        (TileSize.Wide,   "宽（4×2）"),
        (TileSize.Large,  "大（4×4）")
    };

    public AddTileDialog(IReadOnlyList<TileGroup> groups, string defaultGroupId)
    {
        InitializeComponent();
        _groups = new List<TileGroup>(groups);
        _color = MetroColors.Presets[0];

        foreach (var k in KindOptions) KindBox.Items.Add(new ComboBoxItem { Content = k.Label, Tag = k.Kind });
        KindBox.SelectedIndex = 0;
        KindBox.SelectionChanged += (_, _) => UpdateHint();

        foreach (var s in SizeOptions) SizeBox.Items.Add(new ComboBoxItem { Content = s.Label, Tag = s.Size });
        SizeBox.SelectedIndex = 1;

        foreach (var g in _groups)
        {
            var item = new ComboBoxItem { Content = g.Name, Tag = g.Id };
            GroupBox.Items.Add(item);
            if (g.Id == defaultGroupId) GroupBox.SelectedItem = item;
        }
        if (GroupBox.SelectedIndex < 0 && GroupBox.Items.Count > 0) GroupBox.SelectedIndex = 0;

        BrowseButton.Click += OnBrowse;
        ColorButton.Click += (_, _) =>
        {
            var picked = ColorPickerDialog.Show(this, _color);
            if (picked != null) { _color = picked; ApplyColorButton(); }
        };
        OkButton.Click += OnOk;
        ApplyColorButton();
        UpdateHint();
    }

    /// <summary>用户确认后生成的磁贴；取消为 null。</summary>
    public TileItem? Result { get; private set; }

    private TileActionKind SelectedKind =>
        (TileActionKind)((ComboBoxItem)KindBox.SelectedItem).Tag;

    private void UpdateHint()
    {
        HintText.Text = SelectedKind switch
        {
            TileActionKind.Launch => "浏览选择 exe；也可填系统命令名（如 notepad.exe）。",
            TileActionKind.File => "浏览选择任意文件，双击时用默认程序打开。",
            TileActionKind.Folder => "浏览选择一个文件夹。",
            TileActionKind.Url => "填写完整网址，如 https://www.bing.com。",
            TileActionKind.Shell => "如 ms-settings: 或 shell:AppsFolder\\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App。",
            _ => ""
        };
        BrowseButton.IsEnabled = SelectedKind is TileActionKind.Launch or TileActionKind.File or TileActionKind.Folder;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        switch (SelectedKind)
        {
            case TileActionKind.Folder:
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "选择文件夹"
                };
                if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.FolderName))
                {
                    TargetBox.Text = dlg.FolderName;
                    if (string.IsNullOrWhiteSpace(NameBox.Text))
                        NameBox.Text = Path.GetFileName(dlg.FolderName.TrimEnd('\\'));
                }
                break;
            }
            case TileActionKind.Launch:
            case TileActionKind.File:
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "选择目标",
                    Filter = SelectedKind == TileActionKind.Launch
                        ? "程序 (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|所有文件 (*.*)|*.*"
                        : "所有文件 (*.*)|*.*"
                };
                if (dlg.ShowDialog() == true)
                {
                    TargetBox.Text = dlg.FileName;
                    if (string.IsNullOrWhiteSpace(NameBox.Text))
                        NameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
                }
                break;
            }
        }
    }

    private void ApplyColorButton()
    {
        ColorButton.Background = MetroColors.ToBrush(_color);
        ColorButton.Content = _color;
        ColorButton.Foreground = System.Windows.Media.Brushes.White;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var target = TargetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show(this, "请填写磁贴目标。", "添加磁贴", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? Path.GetFileName(target)
            : NameBox.Text.Trim();

        var groupItem = (ComboBoxItem)GroupBox.SelectedItem;
        var size = (TileSize)((ComboBoxItem)SizeBox.SelectedItem).Tag;

        Result = new TileItem
        {
            Name = name,
            Kind = SelectedKind,
            Target = target,
            Color = _color,
            Size = size,
            GroupId = (string)groupItem.Tag,
            ShowLabel = true
        };
        DialogResult = true;
        Close();
    }
}
