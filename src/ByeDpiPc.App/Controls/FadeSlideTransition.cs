using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace ByeDpiPc.App.Controls;

/// <summary>Смена страниц: старая быстро гаснет, новая проявляется с небольшим подъёмом.</summary>
public sealed class FadeSlideTransition : IPageTransition
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(260);
    public double Offset { get; set; } = 14;

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        var tasks = new List<Task>();

        if (from != null)
        {
            var fadeOut = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(Duration.TotalMilliseconds * 0.45),
                FillMode = FillMode.Forward,
                Easing = new CubicEaseIn(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 0d) } },
                },
            };
            tasks.Add(fadeOut.RunAsync(from, cancellationToken));
        }

        if (to != null)
        {
            to.IsVisible = true;
            to.Opacity = 0;
            var fadeIn = new Animation
            {
                Duration = Duration,
                Delay = TimeSpan.FromMilliseconds(Duration.TotalMilliseconds * 0.25),
                FillMode = FillMode.Forward,
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0),
                        Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, Offset) },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1),
                        Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) },
                    },
                },
            };
            tasks.Add(fadeIn.RunAsync(to, cancellationToken));
        }

        await Task.WhenAll(tasks);

        if (from != null && !cancellationToken.IsCancellationRequested) from.IsVisible = false;
        if (to != null) to.Opacity = 1;
    }
}
