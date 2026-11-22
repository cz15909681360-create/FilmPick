using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FilmPick;

sealed class LutEditDialog : DetailDialog
{
    private readonly TextBox name;
    private readonly LutTagChoices choices;
    public string Label => name.Text;
    public string[] Tags => choices.Selected;

    public LutEditDialog(LutEntry entry, string[] tags)
        : base("编辑 LUT", "修改显示名称或添加多个标签。不会改动 LUT 的颜色数据。")
    {
        Width = 550;
        Height = 460;
        var body = new DockPanel();
        name = new TextBox { Text = entry.Name, MaxLength = 100, Margin = new Thickness(0, 0, 0, 10) };
        System.Windows.Automation.AutomationProperties.SetName(name, "LUT 名称");
        DockPanel.SetDock(name, Dock.Top);
        body.Children.Add(name);
        choices = new(tags, entry.Tags);
        body.Children.Add(choices);
        Panel.Children.Add(body);
        AddButton("取消", () => DialogResult = false).IsCancel = true;
        AddButton("保存", () => DialogResult = true, true).IsDefault = true;
    }
}

sealed class LutTagFilterDialog : DetailDialog
{
    private readonly LutTagChoices choices;
    public string[] Tags => choices.Selected;

    public LutTagFilterDialog(string[] tags, string[] selected)
        : base("筛选标签", "仅显示同时包含所有勾选标签的 LUT。未勾选时不限制标签。")
    {
        Width = 550;
        Height = 460;
        choices = new(tags, selected);
        Panel.Children.Add(choices);
        AddButton("取消", () => DialogResult = false).IsCancel = true;
        AddButton("清除筛选", () => { choices.Clear(); DialogResult = true; });
        AddButton("应用筛选", () => DialogResult = true, true).IsDefault = true;
    }
}

sealed class LutTagChoices : ScrollViewer
{
    private readonly CheckBox[] boxes;
    public string[] Selected => boxes.Where(item => item.IsChecked == true).Select(item => (string)item.Content).ToArray();

    public LutTagChoices(string[] tags, string[] selected)
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var stack = new StackPanel();
        boxes = tags.Order(StringComparer.OrdinalIgnoreCase).Select(tag => new CheckBox
        {
            Content = tag, IsChecked = selected.Contains(tag, StringComparer.OrdinalIgnoreCase), Margin = new Thickness(5, 5, 0, 5)
        }).ToArray();
        foreach (var box in boxes) stack.Children.Add(box);
        if (boxes.Length == 0) stack.Children.Add(new TextBlock
        {
            Text = "尚无标签。可在「标签」里先新建。", Margin = new Thickness(5),
            Foreground = (Brush)Application.Current.FindResource("Quiet")
        });
        Content = stack;
    }

    public void Clear() { foreach (var box in boxes) box.IsChecked = false; }
}

sealed class LutTagsDialog : DetailDialog
{
    public LutTagsDialog(LutLibrary library, Action<string?, string?> changed)
        : base("管理标签", "新建或重命名标签。删除标签只解除关联，不移除 LUT。")
    {
        Width = 590;
        Height = 470;
        var body = new DockPanel();
        var name = new TextBox { MaxLength = 100, Margin = new Thickness(0, 0, 0, 9) };
        System.Windows.Automation.AutomationProperties.SetName(name, "标签名称");
        var list = new ListBox();
        DockPanel.SetDock(name, Dock.Top);
        body.Children.Add(name);
        body.Children.Add(list);
        Panel.Children.Add(body);
        Button? rename = null, remove = null;
        AddButton("新建", () => Mutate(null, name.Text));
        rename = AddButton("重命名", () => { if (list.SelectedItem is string tag) Mutate(tag, name.Text); });
        remove = AddButton("删除", () =>
        {
            if (list.SelectedItem is string tag && MessageBox.Show(this, $"删除「{tag}」标签？LUT 不会移除。",
                "删除标签", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                Mutate(tag, null);
        });
        AddButton("完成", Close, true).IsCancel = true;
        list.SelectionChanged += (_, _) =>
        {
            rename!.IsEnabled = remove!.IsEnabled = list.SelectedItem is string;
            if (list.SelectedItem is string tag) name.Text = tag;
        };
        Refresh(null);

        void Refresh(string? selection)
        {
            list.ItemsSource = library.Index.Tags.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            list.SelectedItem = selection;
            rename!.IsEnabled = remove!.IsEnabled = list.SelectedItem is string;
            if (list.SelectedItem == null) name.Clear();
        }

        void Mutate(string? previous, string? next)
        {
            try
            {
                if (previous == null) library.AddTag(next!);
                else library.ChangeTag(previous, next);
                changed(previous, next?.Trim());
                Refresh(next?.Trim());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show(this, ex.Message, "无法修改标签");
            }
        }
    }
}
