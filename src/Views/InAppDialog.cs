using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Views;

/// <summary>
/// 程序内模态对话框：在所属窗口的 AdornerLayer 上覆盖渲染，替代系统 MessageBox（不再弹出程序外窗口）。
/// 通过嵌套消息泵（DispatcherFrame）实现与 MessageBox 一致的"阻塞到用户操作"语义。
/// </summary>
public static class InAppDialog
{
    /// <summary>提示信息（仅"确定"）。</summary>
    public static void Info(DependencyObject? owner, string message, string title) =>
        Show(owner, message, title, yesNo: false);

    /// <summary>警告信息（仅"确定"）。样式同 Info，仅语义区分。</summary>
    public static void Warn(DependencyObject? owner, string message, string title) =>
        Show(owner, message, title, yesNo: false);

    /// <summary>确认对话框（是/否），返回是否选择"是"。</summary>
    public static bool Confirm(DependencyObject? owner, string message, string title) =>
        Show(owner, message, title, yesNo: true);

    /// <summary>
    /// 多按钮选择对话框（程序内覆盖层）。返回被点击按钮的索引；关闭/Esc 返回 -1。
    /// buttons[0] 使用主按钮样式。rememberText 非空时额外显示一个复选框，其勾选状态经 out remember 返回。
    /// </summary>
    public static int Choose(DependencyObject? owner, string message, string title,
        string[] buttons, out bool remember, string? rememberText = null)
    {
        var checkBox = rememberText != null ? new CheckBox
        {
            Content = rememberText,
            Foreground = Res("TextBrush", Brushes.White),
            Margin = new Thickness(0, 18, 0, 0),
        } : null;

        var window = ResolveWindow(owner);
        var root = window?.Content as UIElement;
        var layer = root != null ? AdornerLayer.GetAdornerLayer(root) : null;
        if (layer == null || root == null)
        {
            remember = false;   // 兜底：无覆盖层时退回系统弹窗（丢弃"记住选择"能力）
            var r = MessageBox.Show(message, title, MessageBoxButton.YesNoCancel);
            return r switch
            {
                MessageBoxResult.Yes => 0,
                MessageBoxResult.No => buttons.Length > 1 ? 1 : -1,
                _ => -1,
            };
        }

        var result = -1;
        var frame = new DispatcherFrame();
        OverlayAdorner? adorner = null;

        void Close(int index)
        {
            if (adorner == null)
                return;
            result = index;
            layer.Remove(adorner);
            adorner = null;
            frame.Continue = false;
        }

        var overlay = BuildChoiceOverlay(title, message, buttons, checkBox, Close);
        adorner = new OverlayAdorner(root, overlay);
        layer.Add(adorner);
        overlay.Loaded += (_, _) => Keyboard.Focus(overlay);
        overlay.Focus();

        Dispatcher.PushFrame(frame);
        remember = checkBox?.IsChecked == true;
        return result;
    }

    private static FrameworkElement BuildChoiceOverlay(
        string title, string message, string[] buttons, CheckBox? checkBox, Action<int> close)
    {
        var dim = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0x9A, 0, 0, 0)),
            Focusable = true,
        };
        var card = new Border
        {
            Background = Res("CardBrush", Brushes.Black),
            BorderBrush = Res("BorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24, 20, 24, 18),
            MinWidth = 340,
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Foreground = Res("TextBrush", Brushes.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextBrush", Brushes.White),
            Margin = new Thickness(0, 14, 0, 0),
            LineHeight = 22,
        });
        if (checkBox != null)
            panel.Children.Add(checkBox);

        var buttonBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = MakeButton(buttons[i], primary: i == 0);
            if (i > 0)
                button.Margin = new Thickness(10, 0, 0, 0);
            button.Click += (_, _) => close(index);
            buttonBar.Children.Add(button);
        }
        panel.Children.Add(buttonBar);

        card.Child = panel;
        dim.Children.Add(card);
        dim.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                close(-1);
                e.Handled = true;
            }
        };
        return dim;
    }

    /// <summary>文本输入对话框（程序内覆盖层）：确定返回输入文本，取消返回 null。</summary>
    public static string? Prompt(DependencyObject? owner, string message, string title, string defaultValue = "")
    {
        string? value = null;
        var shown = OverlayHost.ShowModal(owner,
            close => BuildPrompt(title, message, defaultValue, v => value = v, close));
        if (shown == null)
        {
            // 兜底：无法定位窗口 AdornerLayer 时退回系统输入窗口
            var dialog = new InputDialog(title, message);
            if (owner != null && Window.GetWindow(owner) is { } w)
                dialog.Owner = w;
            return dialog.ShowDialog() == true ? dialog.Value : null;
        }
        return shown == true ? value : null;
    }

    /// <summary>
    /// 多字段表单（程序内覆盖层，对齐 Web 的 uiForm）：每个字段一行标签 + 一行输入框。
    /// 确定返回各字段的值（与 <paramref name="fields"/> 同序），取消返回 null。
    /// 回车在最后一个输入框上即确定，其余输入框上跳到下一个。
    /// </summary>
    public static string[]? Form(DependencyObject? owner, string title, string message,
        (string Label, string Value)[] fields, string? okText = null)
    {
        var inputs = fields.Select(f => new TextBox { Text = f.Value }).ToArray();
        var shown = OverlayHost.ShowModal(owner, close =>
        {
            var dim = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x9A, 0, 0, 0)), Focusable = true };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = title, FontWeight = FontWeights.SemiBold, FontSize = 16,
                Foreground = Res("TextBrush", Brushes.White), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            if (message.Length > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = message, TextWrapping = TextWrapping.Wrap,
                    Foreground = Res("TextBrush", Brushes.White), Margin = new Thickness(0, 14, 0, 0),
                });
            for (var i = 0; i < fields.Length; i++)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = fields[i].Label, Foreground = Res("CaptionBrush", Brushes.Gray),
                    Margin = new Thickness(0, 12, 0, 4),
                });
                var input = inputs[i];
                var next = i + 1 < inputs.Length ? inputs[i + 1] : null;
                input.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter)
                        return;
                    if (next != null)
                        next.Focus();
                    else
                        close(true);
                    e.Handled = true;
                };
                panel.Children.Add(input);
            }
            if (inputs.Length > 0)
                inputs[0].Loaded += (_, _) => { inputs[0].Focus(); inputs[0].SelectAll(); };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 20, 0, 0),
            };
            var cancel = MakeButton(I18n.Tr("取消"), primary: false);
            cancel.Click += (_, _) => close(false);
            buttons.Children.Add(cancel);
            var ok = MakeButton(okText ?? I18n.Tr("保存"), primary: true);
            ok.Margin = new Thickness(10, 0, 0, 0);
            ok.Click += (_, _) => close(true);
            buttons.Children.Add(ok);
            panel.Children.Add(buttons);

            dim.Children.Add(new Border
            {
                Background = Res("CardBrush", Brushes.Black),
                BorderBrush = Res("BorderBrush", Brushes.Gray),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(24, 20, 24, 18),
                Width = 480,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = panel,
            });
            dim.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape)
                    return;
                close(false);
                e.Handled = true;
            };
            return dim;
        });
        return shown == true ? inputs.Select(t => t.Text).ToArray() : null;
    }

    private static FrameworkElement BuildPrompt(
        string title, string message, string defaultValue, Action<string> onAccept, Action<bool> close)
    {
        var dim = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0x9A, 0, 0, 0)),
            Focusable = true,
        };
        var card = new Border
        {
            Background = Res("CardBrush", Brushes.Black),
            BorderBrush = Res("BorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24, 20, 24, 18),
            MinWidth = 360,
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Foreground = Res("TextBrush", Brushes.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextBrush", Brushes.White),
            Margin = new Thickness(0, 14, 0, 8),
        });
        var input = new TextBox { Text = defaultValue };
        input.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        panel.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        var ok = MakeButton(I18n.Tr("确定"), primary: true);
        ok.Click += (_, _) => { onAccept(input.Text); close(true); };
        buttons.Children.Add(ok);
        var cancel = MakeButton(I18n.Tr("取消"), primary: false);
        cancel.Margin = new Thickness(10, 0, 0, 0);
        cancel.Click += (_, _) => close(false);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        card.Child = panel;
        dim.Children.Add(card);
        dim.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                onAccept(input.Text);
                close(true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                close(false);
                e.Handled = true;
            }
        };
        return dim;
    }

    private static bool Show(DependencyObject? owner, string message, string title, bool yesNo)
    {
        var window = ResolveWindow(owner);
        var root = window?.Content as UIElement;
        var layer = root != null ? AdornerLayer.GetAdornerLayer(root) : null;
        if (layer == null || root == null)
        {
            // 兜底：无法定位窗口 AdornerLayer 时退回系统弹窗，保证功能不丢失
            var r = MessageBox.Show(message, title,
                yesNo ? MessageBoxButton.YesNo : MessageBoxButton.OK);
            return r is MessageBoxResult.Yes or MessageBoxResult.OK;
        }

        var result = false;
        var frame = new DispatcherFrame();
        OverlayAdorner? adorner = null;

        void Close(bool value)
        {
            if (adorner == null)
                return;
            result = value;
            layer.Remove(adorner);
            adorner = null;
            frame.Continue = false;
        }

        var overlay = BuildOverlay(title, message, yesNo, Close);
        adorner = new OverlayAdorner(root, overlay);
        layer.Add(adorner);
        overlay.Loaded += (_, _) => Keyboard.Focus(overlay);
        overlay.Focus();

        Dispatcher.PushFrame(frame);
        return result;
    }

    private static FrameworkElement BuildOverlay(string title, string message, bool yesNo, Action<bool> close)
    {
        var dim = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(0x9A, 0, 0, 0)),
            Focusable = true,
        };

        var card = new Border
        {
            Background = Res("CardBrush", Brushes.Black),
            BorderBrush = Res("BorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24, 20, 24, 18),
            MinWidth = 340,
            MaxWidth = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 16,
            Foreground = Res("TextBrush", Brushes.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextBrush", Brushes.White),
            Margin = new Thickness(0, 14, 0, 0),
            LineHeight = 22,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0),
        };
        if (yesNo)
        {
            var yes = MakeButton(I18n.Tr("是"), primary: true);
            yes.Click += (_, _) => close(true);
            buttons.Children.Add(yes);
            var no = MakeButton(I18n.Tr("否"), primary: false);
            no.Margin = new Thickness(10, 0, 0, 0);
            no.Click += (_, _) => close(false);
            buttons.Children.Add(no);
        }
        else
        {
            var ok = MakeButton(I18n.Tr("确定"), primary: true);
            ok.Click += (_, _) => close(true);
            buttons.Children.Add(ok);
        }
        panel.Children.Add(buttons);
        card.Child = panel;
        dim.Children.Add(card);

        dim.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                close(true);   // 回车=确定/是
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                close(!yesNo);  // Esc：确认框=否；提示框=关闭
                e.Handled = true;
            }
        };
        return dim;
    }

    private static Button MakeButton(string text, bool primary)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 76,
            Padding = new Thickness(14, 5, 14, 5),
        };
        if (primary && Application.Current?.TryFindResource("AccentButton") is Style accent)
            button.Style = accent;
        return button;
    }

    private static Brush Res(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private static Window? ResolveWindow(DependencyObject? owner)
    {
        if (owner != null && Window.GetWindow(owner) is { } w)
            return w;
        return Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive)
               ?? Application.Current?.MainWindow;
    }

    /// <summary>把对话框覆盖层铺满被装饰元素（整窗）的简易 Adorner。</summary>
    private sealed class OverlayAdorner : Adorner
    {
        private readonly VisualCollection _visuals;
        private readonly FrameworkElement _child;

        public OverlayAdorner(UIElement adornedElement, FrameworkElement child) : base(adornedElement)
        {
            _child = child;
            _visuals = new VisualCollection(this) { child };
        }

        protected override int VisualChildrenCount => _visuals.Count;

        protected override Visual GetVisualChild(int index) => _visuals[index];

        protected override Size MeasureOverride(Size constraint)
        {
            var size = AdornedElement is FrameworkElement fe
                ? new Size(fe.ActualWidth, fe.ActualHeight)
                : constraint;
            _child.Measure(size);
            return size;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _child.Arrange(new Rect(finalSize));
            return finalSize;
        }
    }
}
