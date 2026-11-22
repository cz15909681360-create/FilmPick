using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FilmPick;

class DetailDialog : Window
{
    protected readonly DockPanel Panel = new() { Margin = new Thickness(18), LastChildFill = true };
    protected readonly StackPanel Actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };

    protected DetailDialog(string title, string summary)
    {
        Title = title;
        Width = 860;
        Height = 590;
        MinWidth = 530;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Surface");
        Foreground = (Brush)Application.Current.FindResource("Ink");
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        FontSize = 13;
        Content = Panel;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        DockPanel.SetDock(heading, Dock.Top);
        Panel.Children.Add(heading);
        DockPanel.SetDock(Actions, Dock.Bottom);
        Actions.Margin = new Thickness(0, 12, 0, 0);
        Panel.Children.Add(Actions);
    }

    protected Button AddButton(string label, Action click, bool primary = false)
    {
        var button = new Button { Content = label, Margin = new Thickness(7, 0, 0, 0) };
        if (primary) button.Style = (Style)Application.Current.FindResource("Primary");
        button.Click += (_, _) => click();
        Actions.Children.Add(button);
        return button;
    }

    protected static TextBox ReadOnlyText(IEnumerable<string> rows) => new()
    {
        Text = string.Join(Environment.NewLine, rows),
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        Background = (Brush)Application.Current.FindResource("Panel"),
        Foreground = (Brush)Application.Current.FindResource("Ink"),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(10),
        BorderBrush = (Brush)Application.Current.FindResource("Edge")
    };

    public static void Show(Window owner, string title, string summary, IEnumerable<string> lines)
    {
        var dialog = new DetailDialog(title, summary) { Owner = owner };
        dialog.AddButton("关闭", dialog.Close, true).IsCancel = true;
        dialog.Panel.Children.Add(ReadOnlyText(lines));
        dialog.ShowDialog();
    }
}

sealed class ReviewDialog : DetailDialog
{
    public ReviewDialog(string root, CopyGroup[] groups, string[] unassigned, int hidden, string[] skipped)
        : base("预览复制归档",
            $"扫描范围内已选 {groups.Count(group => group.Mark == Mark.Pick)} 组，已排除 {groups.Count(group => group.Mark == Mark.Reject)} 组。" +
            $"其中 {hidden} 组未显示在当前列表中。归档将在旁边的文件夹内创建副本。")
    {
        AddButton("取消", () => DialogResult = false).IsCancel = true;
        AddButton("确认复制", () => DialogResult = true, true);
        var lines = groups.SelectMany(group => new[] { $"【{(group.Mark == Mark.Pick ? "已选" : "已排除")}】{group.Main}" }
            .Concat(group.Members.Select(member =>
                $"  {Paths.Below(root, member.Relative)}\n  → {Paths.Below(Paths.TargetRoot(root, group.Mark), member.Relative)}"))
            .Append(""));
        if (unassigned.Length > 0)
            lines = lines.Append($"【未自动归组：{unassigned.Length} 项，保持原处】")
                .Append("如需归组，请关闭清单并用「附属文件」手动关联。").Concat(unassigned);
        if (skipped.Length > 0) lines = lines.Append("【分级待写，本次暂缓复制】").Concat(skipped);
        Panel.Children.Add(ReadOnlyText(lines));
    }
}

enum GradeWriteAction { Close, Retry, Discard }

sealed class GradeWriteDialog : DetailDialog
{
    public GradeWriteAction Action { get; private set; }

    public GradeWriteDialog(string[] entries, bool writing) : base("分级待写",
        "解除占用后可以重试。放弃未开始的失败项后，会重新显示文件中保存的分级。")
    {
        Panel.Children.Add(ReadOnlyText(entries));
        AddButton("关闭", Close).IsCancel = true;
        AddButton("放弃未开始的失败项", () => { Action = GradeWriteAction.Discard; Close(); }).IsEnabled = !writing;
        AddButton("重试", () => { Action = GradeWriteAction.Retry; Close(); }, true).IsEnabled = !writing;
    }
}

sealed class AttachmentDialog : DetailDialog
{
    public AttachmentDialog(string owner, Catalog catalog, Session session, Action changed)
        : base("附属文件", owner + "\n自动匹配的文件会随主片复制；手动关联可跨当前扫描范围内的子文件夹。")
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var linked = new ListBox();
        var candidates = new ListBox();
        var search = new TextBox { ToolTip = "按相对路径筛选", Margin = new Thickness(0, 4, 0, 6), Padding = new Thickness(5) };
        var available = Array.Empty<string>();
        var add = new Button { Content = "关联选中文件", Margin = new Thickness(0, 6, 0, 0) };
        var remove = new Button { Content = "取消手动关联", Margin = new Thickness(0, 6, 0, 0) };
        var left = MakePane("随主片复制", linked, remove, null);
        var right = MakePane("未归组文件 · 可筛选", candidates, add, search);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        Panel.Children.Add(grid);
        AddButton("完成", Close, true).IsCancel = true;
        linked.DisplayMemberPath = nameof(LinkItem.Display);
        linked.SelectionChanged += (_, _) => remove.IsEnabled = linked.SelectedItem is LinkItem { Automatic: false };
        candidates.SelectionChanged += (_, _) => add.IsEnabled = candidates.SelectedItem is string;
        search.TextChanged += (_, _) => Filter();
        add.Click += (_, _) =>
        {
            if (candidates.SelectedItem is not string name) return;
            session.Links[name] = new SavedLink(owner, catalog.Files[owner].Stamp, catalog.Files[name].Stamp);
            changed();
            Refresh();
        };
        remove.Click += (_, _) =>
        {
            if (linked.SelectedItem is not LinkItem { Automatic: false } item) return;
            session.Links.Remove(item.File);
            changed();
            Refresh();
        };
        Refresh();

        void Filter()
        {
            candidates.ItemsSource = available.Where(file => file.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            add.IsEnabled = false;
        }

        void Refresh()
        {
            var owners = catalog.Owners(session);
            linked.ItemsSource = owners.Where(pair => Paths.Comparer.Equals(pair.Value, owner))
                .OrderBy(pair => pair.Key).Select(pair => new LinkItem(pair.Key, catalog.Automatic.ContainsKey(pair.Key) || catalog.Pairs.ContainsKey(pair.Key))).ToArray();
            available = catalog.Files.Keys.Where(file => !catalog.Main.ContainsKey(file) && !owners.ContainsKey(file)
                && !session.Links.ContainsKey(file)).Order().ToArray();
            remove.IsEnabled = false;
            Filter();
        }
    }

    private static DockPanel MakePane(string label, ListBox list, Button action, TextBox? search)
    {
        var pane = new DockPanel();
        var title = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(title, Dock.Top);
        pane.Children.Add(title);
        if (search != null) { DockPanel.SetDock(search, Dock.Top); pane.Children.Add(search); }
        DockPanel.SetDock(action, Dock.Bottom);
        pane.Children.Add(action);
        pane.Children.Add(list);
        return pane;
    }

    private sealed record LinkItem(string File, bool Automatic)
    {
        public string Display => (Automatic ? "自动 · " : "手动 · ") + File;
    }
}
