using System.Windows;

namespace TileStartMenu.App.UI;

/// <summary>单行文本输入对话框（重命名 / 新分组名）。取消返回 null。</summary>
public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        OkButton.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>用户输入的文本；点取消为 null。</summary>
    public string? Value => InputBox.Text?.Trim();

    public static string? Show(Window owner, string title, string prompt, string defaultValue = "")
    {
        var dlg = new InputDialog
        {
            Title = title,
            PromptText = { Text = prompt },
            InputBox = { Text = defaultValue },
            Owner = owner
        };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
