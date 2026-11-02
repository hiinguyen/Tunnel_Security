using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Station.Models;
using Station.Services;
using Windows.UI;

namespace Station.Controls;

/// <summary>
/// Bọc nội dung của một cửa sổ và phủ lên nó lớp đỏ nháy khi có cảnh báo mới.
/// Mỗi cửa sổ Station (cửa sổ chính và các cửa sổ module) gọi <see cref="Attach"/> một lần,
/// nhờ vậy mọi màn hình đang bật cùng nháy đỏ, không phải sửa từng page.
/// Cảnh báo Khẩn cấp nháy liên tục tới khi được xác nhận; các mức khác nháy vài nhịp.
/// </summary>
public static class AlertFlashHost
{
    private const int PulsesForNonCritical = 5;
    private static readonly Color FlashColor = Color.FromArgb(255, 220, 38, 38);

    public static void Attach(Window window)
    {
        if (window.Content is not UIElement original) return;

        var overlay = new Border
        {
            Background = new SolidColorBrush(FlashColor),
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        window.Content = null;
        var host = new Grid();
        host.Children.Add(original);
        host.Children.Add(overlay);
        window.Content = host;

        var storyboard = BuildStoryboard(overlay);
        var service = AlertNotificationService.Instance;

        void OnRaised(object? s, Alert alert)
        {
            var critical = alert.Severity == AlertSeverity.Critical;
            storyboard.RepeatBehavior = critical
                ? RepeatBehavior.Forever
                : new RepeatBehavior(PulsesForNonCritical);
            storyboard.Begin();
        }

        void OnPendingChanged(object? s, EventArgs e)
        {
            if (service.Pending.Count > 0) return;
            storyboard.Stop();
            overlay.Opacity = 0;
        }

        service.AlertRaised += OnRaised;
        service.PendingChanged += OnPendingChanged;

        window.Closed += (_, _) =>
        {
            service.AlertRaised -= OnRaised;
            service.PendingChanged -= OnPendingChanged;
            storyboard.Stop();
        };
    }

    private static Storyboard BuildStoryboard(Border overlay)
    {
        var frames = new DoubleAnimationUsingKeyFrames();
        Storyboard.SetTarget(frames, overlay);
        Storyboard.SetTargetProperty(frames, "Opacity");
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
        frames.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(150), Value = 0.50 });
        frames.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(550), Value = 0.35 });
        frames.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(900), Value = 0 });
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1600), Value = 0 });

        var storyboard = new Storyboard();
        storyboard.Children.Add(frames);
        return storyboard;
    }
}
