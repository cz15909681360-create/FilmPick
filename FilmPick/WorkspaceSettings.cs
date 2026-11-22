using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace FilmPick;

enum WorkAction
{
    Previous, Next, StepBack, StepForward, HoldBack, HoldForward, JumpBack, JumpForward,
    Clear, Reject, Pick, Undo, Stars0, Stars1, Stars2, Stars3, Stars4, Stars5, Capture, Lut, Proxy, Play, Mute, Speed
}

enum KeyTrigger { Press, Tap, Hold }

record WorkKey(Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    public static WorkKey From(KeyEventArgs args) => new(args.Key == Key.System ? args.SystemKey : args.Key, Keyboard.Modifiers);
    public bool Valid => Enum.IsDefined(Key) && Key is not (Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) &&
        (Modifiers & ~(ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt)) == 0 &&
        !(Modifiers.HasFlag(ModifierKeys.Alt) && Key is Key.F4 or Key.Tab);
    public string Label => (Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
        (Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + (Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
        (Key switch
        {
            Key.Up => "↑", Key.Down => "↓", Key.Left => "←", Key.Right => "→", Key.Space => "空格",
            >= Key.D0 and <= Key.D9 => ((int)(Key - Key.D0)).ToString(CultureInfo.InvariantCulture),
            >= Key.NumPad0 and <= Key.NumPad9 => "小键盘 " + (Key - Key.NumPad0),
            _ => Key.ToString()
        });
}

record WorkBinding(WorkAction Action, string Label, KeyTrigger Trigger, WorkKey[] Defaults, bool VideoOnly = false);

record UiPreferences(double QueueWidth = 230, double LibraryWidth = 294, double BottomExtra = 0, double Speed = 3,
    Dictionary<WorkAction, WorkKey[]>? Keys = null, bool PhotoThumbnails = false, bool VideoThumbnails = false)
{
    public UiPreferences() : this(230, 294, 0, 3, null) { }
}

public partial class MainWindow
{
    private static readonly double[] Speeds = [1.25, 1.5, 2, 2.5, 3, 4, 5];
    private static readonly WorkBinding[] WorkBindings =
    [
        KeyFor(WorkAction.Previous, "上一条素材", Key.Up),
        KeyFor(WorkAction.Next, "下一条素材", Key.Down),
        KeyFor(WorkAction.StepBack, "后退一帧", Key.Left, KeyTrigger.Tap, video: true),
        KeyFor(WorkAction.StepForward, "前进一帧", Key.Right, KeyTrigger.Tap, video: true),
        KeyFor(WorkAction.HoldBack, "长按快退", Key.Left, KeyTrigger.Hold, video: true),
        KeyFor(WorkAction.HoldForward, "长按快进", Key.Right, KeyTrigger.Hold, video: true),
        KeyFor(WorkAction.JumpBack, "后退 5 秒", Key.Left, modifiers: ModifierKeys.Shift, video: true),
        KeyFor(WorkAction.JumpForward, "前进 5 秒", Key.Right, modifiers: ModifierKeys.Shift, video: true),
        Digit(WorkAction.Clear, "清除旗标", 1), Digit(WorkAction.Reject, "排除", 2), Digit(WorkAction.Pick, "选中", 3),
        KeyFor(WorkAction.Undo, "撤销旗标", Key.Z, modifiers: ModifierKeys.Control),
        Digit(WorkAction.Stars0, "清除星级", 0, ModifierKeys.Control),
        Digit(WorkAction.Stars1, "1 星", 1, ModifierKeys.Control), Digit(WorkAction.Stars2, "2 星", 2, ModifierKeys.Control),
        Digit(WorkAction.Stars3, "3 星", 3, ModifierKeys.Control), Digit(WorkAction.Stars4, "4 星", 4, ModifierKeys.Control),
        Digit(WorkAction.Stars5, "5 星", 5, ModifierKeys.Control),
        KeyFor(WorkAction.Capture, "显示或隐藏拍摄参数", Key.Tab),
        KeyFor(WorkAction.Lut, "切换 LUT", Key.L),
        KeyFor(WorkAction.Proxy, "切换代理", Key.V, video: true),
        KeyFor(WorkAction.Play, "播放或暂停", Key.Space, video: true),
        KeyFor(WorkAction.Mute, "切换静音", Key.M, video: true),
        new(WorkAction.Speed, "切换持续倍速", KeyTrigger.Press, [], true)
    ];

    private UiPreferences ui = new();
    private bool useSpeed;
    private DispatcherTimer holdTimer = null!;
    private HeldKey? heldKey;
    private sealed record HeldKey(WorkKey Key, WorkAction? Tap, WorkAction? Hold, long Ticket);

    private static WorkBinding KeyFor(WorkAction action, string label, Key key, KeyTrigger trigger = KeyTrigger.Press,
        ModifierKeys modifiers = ModifierKeys.None, bool video = false) => new(action, label, trigger, [new(key, modifiers)], video);

    private static WorkBinding Digit(WorkAction action, string label, int number, ModifierKeys modifiers = ModifierKeys.None) =>
        new(action, label, KeyTrigger.Press, [new(Key.D0 + number, modifiers), new(Key.NumPad0 + number, modifiers)]);

    private static WorkKey[] KeysFor(WorkBinding binding, Dictionary<WorkAction, WorkKey[]>? map) =>
        map?.GetValueOrDefault(binding.Action) ?? binding.Defaults;

    private static bool Compatible(WorkBinding first, WorkBinding second) => first.VideoOnly && second.VideoOnly &&
        ((first.Trigger == KeyTrigger.Tap && second.Trigger == KeyTrigger.Hold) ||
         (first.Trigger == KeyTrigger.Hold && second.Trigger == KeyTrigger.Tap));

    private static string? KeyConflict(Dictionary<WorkAction, WorkKey[]> map)
    {
        var used = new Dictionary<WorkKey, List<WorkBinding>>();
        foreach (var binding in WorkBindings)
            foreach (var key in KeysFor(binding, map).Distinct())
            {
                if (!used.TryGetValue(key, out var others)) used[key] = others = [];
                var conflict = others.FirstOrDefault(other => !Compatible(other, binding));
                if (conflict != null) return $"{key.Label} 同时用于「{conflict.Label}」和「{binding.Label}」。";
                others.Add(binding);
            }
        return null;
    }

    private void InitializeWorkspace()
    {
        try
        {
            var saved = browseStore.Read<UiPreferences>("ui");
            ui = new(
                double.IsFinite(saved.QueueWidth) && saved.QueueWidth >= 180 ? saved.QueueWidth : 230,
                double.IsFinite(saved.LibraryWidth) && saved.LibraryWidth >= 230 ? saved.LibraryWidth : 294,
                double.IsFinite(saved.BottomExtra) && saved.BottomExtra >= 0 ? saved.BottomExtra : 0,
                Speeds.Contains(saved.Speed) ? saved.Speed : 3,
                saved.Keys is { } keys && keys.Values.All(list => list is { Length: <= 2 } && list.All(key => key?.Valid == true)) &&
                    KeyConflict(keys) == null ? keys : null, saved.PhotoThumbnails, saved.VideoThumbnails);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        holdTimer = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(350) };
        holdTimer.Tick += (_, _) =>
        {
            holdTimer.Stop();
            if (heldKey is not { Hold: { } action } held || held.Ticket != ticket || !videoReady) return;
            heldKey = held with { Tap = null };
            player?.SetShuttle(ticket, action == WorkAction.HoldForward ? ui.Speed : -ui.Speed);
            UpdateSpeedControl();
        };
        Viewport.SizeChanged += (_, _) =>
        {
            var narrow = Viewport.ActualWidth < 570;
            Grid.SetColumnSpan(PreviewIdentity, narrow ? 2 : 1);
            Grid.SetColumn(PreviewToolbar, narrow ? 0 : 1);
            Grid.SetColumnSpan(PreviewToolbar, narrow ? 2 : 1);
            Grid.SetRow(PreviewToolbar, narrow ? 1 : 0);
            UpdateBottomLayout();
        };
        LostKeyboardFocus += (_, args) =>
        {
            if (heldKey != null && (args.NewFocus == null || args.NewFocus is TextBoxBase or ComboBox or Slider or Thumb or System.Windows.Controls.Calendar or DatePicker or MenuItem ||
                LibraryPanel.IsKeyboardFocusWithin)) CancelTransportInput();
        };
        ApplyWorkspaceWidths();
        UpdateShortcutHints();
    }

    private void SaveUi()
    {
        try { browseStore.Write("ui", ui); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Status("无法保存设置：" + error.Message); }
    }

    private void WorkspaceResized(object sender, SizeChangedEventArgs args)
    {
        if (browseStore == null) return;
        ApplyWorkspaceWidths();
        UpdateBottomLayout();
    }

    private void ApplyWorkspaceWidths()
    {
        var libraryOpen = LibraryPanel.Visibility == Visibility.Visible;
        var available = (Workspace.ActualWidth > 0 ? Workspace.ActualWidth : Width - 28) - 422 - (libraryOpen ? 24 : 12);
        var queue = Math.Clamp(ui.QueueWidth, 180, Math.Max(180, available - (libraryOpen ? 230 : 0)));
        QueueColumn.MinWidth = 180;
        QueueColumn.Width = new(queue);
        LibraryColumn.MinWidth = libraryOpen ? 230 : 0;
        LibraryColumn.Width = new(libraryOpen ? Math.Clamp(ui.LibraryWidth, 230, Math.Max(230, available - queue)) : 0);
        LibraryGutter.Width = new(libraryOpen ? 12 : 0);
        LutSplitter.Visibility = libraryOpen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBottomLayout()
    {
        var showVideo = view == Kind.Video && Queue.SelectedItem is Row;
        VideoControls.Visibility = showVideo ? Visibility.Visible : Visibility.Collapsed;
        var baseline = showVideo ? 90 : 52;
        BottomRow.MinHeight = baseline;
        var max = Math.Max(baseline, Workspace.ActualHeight - Math.Max(55, PreviewHeader.ActualHeight) - 5 - 160 - 2);
        BottomRow.Height = new(Math.Min(baseline + ui.BottomExtra, max));
    }

    private void SaveLayoutDrag(object sender, DragCompletedEventArgs args)
    {
        if (args.Canceled || sender is not GridSplitter splitter) return;
        if (splitter.ResizeDirection == GridResizeDirection.Rows)
            ui = ui with { BottomExtra = Math.Max(0, BottomRow.Height.Value - (VideoControls.IsVisible ? 90 : 52)) };
        else if (splitter == LutSplitter) ui = ui with { LibraryWidth = LibraryColumn.Width.Value };
        else ui = ui with { QueueWidth = QueueColumn.Width.Value };
        SaveUi();
    }

    private void OpenUiSettings(object sender, RoutedEventArgs args)
    {
        CancelTransportInput();
        var settings = new ContextMenu { PlacementTarget = sender as Button, Placement = PlacementMode.Bottom };
        var keys = new MenuItem { Header = "快捷键…" };
        keys.Click += (_, _) => EditShortcuts();
        var reset = new MenuItem { Header = "恢复初始布局" };
        reset.Click += (_, _) =>
        {
            ui = ui with { QueueWidth = 230, LibraryWidth = 294, BottomExtra = 0 };
            ApplyWorkspaceWidths();
            UpdateBottomLayout();
            SaveUi();
        };
        settings.Items.Add(keys);
        settings.Items.Add(reset);
        settings.IsOpen = true;
    }

    private void EditShortcuts()
    {
        var dialog = new Window
        {
            Owner = this, Title = "FilmPick · 快捷键", Width = 580, Height = 654, MinWidth = 468, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Background = Background, Foreground = Foreground, FontFamily = FontFamily
        };
        var map = ui.Keys == null ? new Dictionary<WorkAction, WorkKey[]>() : new(ui.Keys);
        var draft = WorkBindings.ToDictionary(binding => binding.Action, binding => new WorkKey?[2]);
        var fields = new Dictionary<WorkAction, TextBox[]>();
        var content = new DockPanel { Margin = new Thickness(16) };
        dialog.Content = content;
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);
        var warning = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        footer.Children.Add(warning);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(buttons);
        Button AddButton(string label)
        {
            var button = new Button { Content = label, Margin = new Thickness(6, 0, 0, 0) };
            buttons.Children.Add(button);
            return button;
        }
        var reset = AddButton("恢复默认快捷键");
        var cancel = AddButton("取消");
        cancel.IsCancel = true;
        var save = AddButton("保存");
        var rows = new StackPanel();
        content.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var help = new TextBlock { Text = "点击按键框并按下组合键，用旁边的 × 清除。", Margin = new Thickness(0, 0, 0, 8) };
        rows.Children.Add(help);

        void Validate()
        {
            foreach (var (action, slots) in draft) map[action] = slots.OfType<WorkKey>().Distinct().ToArray();
            var conflict = KeyConflict(map);
            warning.Text = conflict ?? "";
            save.IsEnabled = conflict == null;
        }

        void LoadDefaults(bool resetAll)
        {
            if (resetAll) map.Clear();
            foreach (var binding in WorkBindings)
            {
                var value = KeysFor(binding, map);
                for (var slot = 0; slot < 2; slot++)
                {
                    var key = value.ElementAtOrDefault(slot);
                    draft[binding.Action][slot] = key;
                    fields[binding.Action][slot].Text = key?.Label ?? "";
                }
            }
            Validate();
        }

        foreach (var binding in WorkBindings)
        {
            var line = new Grid { Margin = new Thickness(0, 0, 6, 4) };
            for (var col = 0; col < 3; col++) line.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            line.Children.Add(new TextBlock { Text = binding.Label, VerticalAlignment = VerticalAlignment.Center });
            var inputs = new TextBox[2];
            fields[binding.Action] = inputs;
            for (var slot = 0; slot < inputs.Length; slot++)
            {
                var index = slot;
                var pair = new DockPanel { Margin = new Thickness(2, 0, 2, 0) };
                Grid.SetColumn(pair, slot + 1);
                line.Children.Add(pair);
                var clear = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), ToolTip = "清除此按键" };
                DockPanel.SetDock(clear, Dock.Right);
                pair.Children.Add(clear);
                var input = new TextBox { IsReadOnly = true, Padding = new Thickness(4) };
                inputs[index] = input;
                System.Windows.Automation.AutomationProperties.SetName(input, binding.Label + (index == 0 ? " 主按键" : " 备用按键"));
                pair.Children.Add(input);
                clear.Click += (_, _) => { draft[binding.Action][index] = null; input.Clear(); Validate(); };
                input.PreviewKeyDown += (_, args) =>
                {
                    var key = WorkKey.From(args);
                    args.Handled = true;
                    if (!key.Valid || args.IsRepeat) return;
                    draft[binding.Action][index] = key;
                    input.Text = key.Label;
                    Validate();
                };
            }
            rows.Children.Add(line);
        }
        LoadDefaults(false);
        reset.Click += (_, _) => LoadDefaults(true);
        save.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        ui = ui with { Keys = WorkBindings.Where(binding => !map[binding.Action].SequenceEqual(binding.Defaults))
            .ToDictionary(binding => binding.Action, binding => map[binding.Action]) };
        SaveUi();
        RefreshBrowseWidgets();
        UpdateControls();
    }

    private string KeyHint(string label, WorkAction action)
    {
        var keys = KeysFor(WorkBindings.First(binding => binding.Action == action), ui.Keys);
        return keys.Length > 0 ? label + " · " + string.Join(" / ", keys.Select(key => key.Label)) : label;
    }

    private void UpdateShortcutHints()
    {
        PreviousButton.ToolTip = KeyHint("上一条", WorkAction.Previous);
        NextButton.ToolTip = KeyHint("下一条", WorkAction.Next);
        UnmarkButton.Content = KeyHint("清除", WorkAction.Clear).Split(" / ")[0];
        RejectButton.Content = KeyHint("排除", WorkAction.Reject).Split(" / ")[0];
        PickButton.Content = KeyHint("选中", WorkAction.Pick).Split(" / ")[0];
        PlayButton.ToolTip = KeyHint("播放或暂停", WorkAction.Play);
        SoundButton.ToolTip = KeyHint("切换静音", WorkAction.Mute);
        UnmarkButton.ToolTip = KeyHint("清除旗标", WorkAction.Clear);
        RejectButton.ToolTip = KeyHint("排除", WorkAction.Reject);
        PickButton.ToolTip = KeyHint("选中", WorkAction.Pick);
        foreach (var star in RatingStrip.Children.OfType<Button>())
            if (int.TryParse((string?)star.Tag, out var n))
                star.ToolTip = KeyHint(n == 0 ? "清除星级" : $"{n} 星", WorkAction.Stars0 + n);
    }

    private double NormalSpeed => useSpeed ? ui.Speed : 1;
    private static string RateText(double speed) => speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
    private void ToggleSpeedClick(object sender, RoutedEventArgs args) => ToggleSpeed();
    private void ToggleSpeed()
    {
        CancelTransportInput();
        useSpeed = !useSpeed;
        player?.SetSpeed(NormalSpeed);
        UpdateSpeedControl();
    }

    private void OpenSpeedMenu(object sender, RoutedEventArgs args)
    {
        CancelTransportInput();
        var menu = new ContextMenu { PlacementTarget = SpeedMenuButton, Placement = PlacementMode.Top };
        foreach (var speed in Speeds)
        {
            var item = new MenuItem { Header = RateText(speed), IsCheckable = true, IsChecked = ui.Speed == speed };
            item.Click += (_, _) =>
            {
                ui = ui with { Speed = speed };
                player?.SetSpeed(NormalSpeed);
                SaveUi();
                UpdateSpeedControl();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void UpdateSpeedControl()
    {
        SpeedButton.Content = heldKey is { Tap: null, Hold: { } action } && !holdTimer.IsEnabled
            ? (action == WorkAction.HoldBack ? "◀ " : "▶ ") + RateText(ui.Speed) : RateText(NormalSpeed);
        SpeedButton.ToolTip = KeyHint("切换 1× / " + RateText(ui.Speed), WorkAction.Speed);
    }

    private void CancelTransportInput(object? sender = null, EventArgs? args = null)
    {
        if (heldKey == null) return;
        heldKey = null;
        holdTimer.Stop();
        player?.SetShuttle(ticket, 0);
        UpdateSpeedControl();
    }

    private void OnKey(object sender, KeyEventArgs args)
    {
        if (busy || BrowseTextFocus(args) || args.OriginalSource is Thumb or GridSplitter) return;
        var key = WorkKey.From(args);
        if (heldKey is { } current)
        {
            if (current.Key == key) { args.Handled = true; return; }
            CancelTransportInput();
        }
        var matches = WorkBindings.Where(binding => (!binding.VideoOnly || view == Kind.Video) &&
            KeysFor(binding, ui.Keys).Contains(key)).ToArray();
        if (matches.Length == 0) return;
        args.Handled = true;
        if (matches[0].Trigger == KeyTrigger.Press)
        {
            var action = matches[0].Action;
            if (!args.IsRepeat || action is WorkAction.Previous or WorkAction.Next) RunShortcut(action);
        }
        else if (!args.IsRepeat && videoReady)
        {
            var tap = matches.FirstOrDefault(binding => binding.Trigger == KeyTrigger.Tap)?.Action;
            var hold = matches.FirstOrDefault(binding => binding.Trigger == KeyTrigger.Hold)?.Action;
            heldKey = new(key, tap, hold, ticket);
            if (hold != null) holdTimer.Start();
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs args)
    {
        if (heldKey is not { } held) return;
        var key = WorkKey.From(args);
        if (held.Key.Key != key.Key)
        {
            if (held.Key.Modifiers != key.Modifiers) CancelTransportInput();
            return;
        }
        args.Handled = true;
        CancelTransportInput();
        if (!busy && held.Ticket == ticket && held.Tap is { } tap && !BrowseTextFocus(args)) RunShortcut(tap);
    }

    private void RunShortcut(WorkAction action)
    {
        switch (action)
        {
            case WorkAction.Previous: MoveCursor(-1); break;
            case WorkAction.Next: MoveCursor(1); break;
            case WorkAction.Clear: SetMark(Mark.None); break;
            case WorkAction.Reject: SetMark(Mark.Reject); break;
            case WorkAction.Pick: SetMark(Mark.Pick); break;
            case WorkAction.Undo: UndoMark(); break;
            case >= WorkAction.Stars0 and <= WorkAction.Stars5: SetRating(action - WorkAction.Stars0); break;
            case WorkAction.Capture when root != null: ToggleCapture(); break;
            case WorkAction.Lut: ToggleLut(); break;
            case WorkAction.Proxy: ToggleProxy(); break;
            case WorkAction.Play when videoReady: player?.TogglePlayback(); break;
            case WorkAction.Mute when videoReady: player?.ToggleMute(); break;
            case WorkAction.Speed when videoReady: ToggleSpeed(); break;
            case WorkAction.StepBack or WorkAction.StepForward when videoReady:
                seekTimer.Stop();
                player?.Step(ticket, action == WorkAction.StepForward);
                break;
            case WorkAction.JumpBack or WorkAction.JumpForward when videoReady:
                seekTimer.Stop();
                player?.SeekBy(ticket, action == WorkAction.JumpForward ? 5 : -5);
                break;
        }
    }
}
