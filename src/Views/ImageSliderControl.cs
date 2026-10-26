using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace R18MediaLibrary.Views;

/// <summary>详情页图片区：大图 + 缩略图切换条（对应 Python 版 ImageSlider，仿 DLsite 作品页）。</summary>
public class ImageSliderControl : UserControl
{
    private const double ThumbW = 72, ThumbH = 54;   // Web .thumbs img { height: 54px }

    private readonly List<string> _paths;
    private readonly Image _mainImage = new() { Stretch = Stretch.Uniform };
    private readonly List<Border> _thumbBorders = [];
    private int _index;

    public ImageSliderControl(List<string> paths)
    {
        _paths = paths;
        var layout = new StackPanel();

        var mainBorder = new Border
        {
            // Web .gallery img.main：圆角 8；双列布局下不垫底色（.dtop > .gallery img.main { background: none }）
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            MinHeight = 240,
            Child = _mainImage,
        };
        layout.Children.Add(mainBorder);

        if (paths.Count > 1)
        {
            var bar = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var prev = new Button { Content = "‹", Width = 28, Height = ThumbH + 4, Padding = new Thickness(0) };
            prev.Click += (_, _) => SetIndex(_index - 1);
            bar.Children.Add(prev);

            var thumbs = new StackPanel { Orientation = Orientation.Horizontal };
            for (var i = 0; i < paths.Count; i++)
            {
                var border = new Border
                {
                    Width = ThumbW, Height = ThumbH,
                    Margin = new Thickness(3, 0, 3, 0),   // 间距 6
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(5),
                    BorderBrush = Brushes.Transparent,
                    Cursor = Cursors.Hand,
                };
                var image = new Image { Stretch = Stretch.Uniform };
                if (TryLoad(paths[i], (int)ThumbW * 2) is { } bmp)
                    image.Source = bmp;
                border.Child = image;
                var captured = i;
                border.MouseLeftButtonUp += (_, _) => SetIndex(captured);
                _thumbBorders.Add(border);
                thumbs.Children.Add(border);
            }
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Height = ThumbH + 12,
                Content = thumbs,
            };
            Grid.SetColumn(scroll, 1);
            bar.Children.Add(scroll);

            var next = new Button { Content = "›", Width = 28, Height = ThumbH + 4, Padding = new Thickness(0) };
            next.Click += (_, _) => SetIndex(_index + 1);
            Grid.SetColumn(next, 2);
            bar.Children.Add(next);
            layout.Children.Add(bar);
        }

        Content = layout;
        SetIndex(0);
    }

    private static BitmapImage? TryLoad(string path, int decodeWidth = 0)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            if (decodeWidth > 0)
                bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void SetIndex(int index)
    {
        if (_paths.Count == 0)
            return;
        _index = ((index % _paths.Count) + _paths.Count) % _paths.Count;
        _mainImage.Source = TryLoad(_paths[_index]);
        var accent = (Brush)Application.Current.Resources["AccentLightBrush"];
        // Web .thumbs img：未选 .65 透明度、无描边；选中全亮 + 2px accent 描边
        for (var i = 0; i < _thumbBorders.Count; i++)
        {
            _thumbBorders[i].BorderBrush = i == _index ? accent : Brushes.Transparent;
            _thumbBorders[i].Opacity = i == _index ? 1.0 : 0.65;
        }
    }
}
