using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OneClickFilm
{
    internal static class UiFx
    {
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.RegisterAttached("IsActive", typeof(bool), typeof(UiFx), new PropertyMetadata(false));

        public static bool GetIsActive(DependencyObject obj) { return (bool)obj.GetValue(IsActiveProperty); }
        public static void SetIsActive(DependencyObject obj, bool value) { obj.SetValue(IsActiveProperty, value); }

        public static readonly DependencyProperty ToneProperty =
            DependencyProperty.RegisterAttached("Tone", typeof(string), typeof(UiFx), new PropertyMetadata("Default"));

        public static string GetTone(DependencyObject obj) { return (string)obj.GetValue(ToneProperty); }
        public static void SetTone(DependencyObject obj, string value) { obj.SetValue(ToneProperty, value); }

        private static readonly IEasingFunction EaseOutCubic = new CubicEase { EasingMode = EasingMode.EaseOut };

        internal static bool CanAnimate(SystemOptions sys)
        {
            return sys != null && !sys.ReduceMotion && !sys.HighContrast && !SystemParameters.HighContrast && SystemParameters.ClientAreaAnimation;
        }

        internal static void AnimatePageEntrance(UIElement target, SystemOptions sys)
        {
            if (target == null) return;
            target.BeginAnimation(UIElement.OpacityProperty, null);
            var shift = target.RenderTransform as TranslateTransform;
            if (shift != null) shift.BeginAnimation(TranslateTransform.YProperty, null);

            if (!CanAnimate(sys))
            {
                if (sys != null && !sys.HighContrast && !SystemParameters.HighContrast && SystemParameters.ClientAreaAnimation)
                {
                    var gentle = new DoubleAnimation(0.86, 1.0, TimeSpan.FromMilliseconds(110))
                    {
                        EasingFunction = EaseOutCubic,
                        FillBehavior = FillBehavior.Stop
                    };
                    target.BeginAnimation(UIElement.OpacityProperty, gentle);
                }
                else target.Opacity = 1.0;
                return;
            }

            if (shift == null || shift.IsFrozen)
            {
                shift = new TranslateTransform(0, 0);
                target.RenderTransform = shift;
            }
            var opacityAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = EaseOutCubic,
                FillBehavior = FillBehavior.Stop
            };
            var slideAnim = new DoubleAnimation(8.0, 0.0, TimeSpan.FromMilliseconds(210))
            {
                EasingFunction = EaseOutCubic,
                FillBehavior = FillBehavior.Stop
            };
            target.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
            shift.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }

        // Non-uniform stagger capped at first 5 items (< 260ms total), never blocking interaction.
        internal static void StaggerEntrance(UIElement[] items, SystemOptions sys)
        {
            if (items == null || items.Length == 0 || !CanAnimate(sys)) return;
            int[] delays = { 0, 35, 65, 90, 110 };
            int count = Math.Min(items.Length, delays.Length);
            for (int i = 0; i < count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                var delay = TimeSpan.FromMilliseconds(delays[i]);
                var transform = new TranslateTransform(0, 0);
                item.RenderTransform = transform;
                // Keep base Opacity at 1.0 so interrupted or un-ticked animations can never leave elements invisible.
                item.Opacity = 1.0;
                var opacity = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
                opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                if (delays[i] > 0)
                    opacity.KeyFrames.Add(new DiscreteDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(delay)));
                opacity.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(175)), EaseOutCubic));
                var slide = new DoubleAnimation(7.0, 0.0, TimeSpan.FromMilliseconds(195))
                {
                    BeginTime = delay,
                    EasingFunction = EaseOutCubic,
                    FillBehavior = FillBehavior.Stop
                };
                item.BeginAnimation(UIElement.OpacityProperty, opacity);
                transform.BeginAnimation(TranslateTransform.YProperty, slide);
            }
        }

        internal static void AnimateDialogOpen(Window dialog, SystemOptions sys)
        {
            if (dialog == null || !CanAnimate(sys)) return;
            dialog.Loaded += delegate
            {
                var root = dialog.Content as FrameworkElement;
                if (root == null) return;
                root.RenderTransformOrigin = new Point(0.5, 0.5);
                var scale = new ScaleTransform(0.965, 0.965);
                root.RenderTransform = scale;
                var dur = TimeSpan.FromMilliseconds(185);
                var sx = new DoubleAnimation(0.965, 1.0, dur) { EasingFunction = EaseOutCubic, FillBehavior = FillBehavior.Stop };
                var sy = new DoubleAnimation(0.965, 1.0, dur) { EasingFunction = EaseOutCubic, FillBehavior = FillBehavior.Stop };
                var op = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(155)) { EasingFunction = EaseOutCubic, FillBehavior = FillBehavior.Stop };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
                root.BeginAnimation(UIElement.OpacityProperty, op);
            };
        }

        internal static void SetPulse(UIElement dot, bool active, SystemOptions sys)
        {
            if (dot == null) return;
            if (!active || !CanAnimate(sys))
            {
                dot.BeginAnimation(UIElement.OpacityProperty, null);
                dot.Opacity = active ? 1.0 : 0.7;
                return;
            }
            var pulse = new DoubleAnimation(1.0, 0.32, TimeSpan.FromMilliseconds(850))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            dot.BeginAnimation(UIElement.OpacityProperty, pulse);
        }

        private static DoubleAnimation Transition(double from, double to, int ms)
        {
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
                { EasingFunction = EaseOutCubic, FillBehavior = FillBehavior.Stop };
        }

        internal static void AnimateProgress(ProgressBar bar, double targetValue, SystemOptions sys)
        {
            double value = Math.Max(bar.Minimum, Math.Min(bar.Maximum, targetValue));
            if ((double)bar.GetAnimationBaseValue(ProgressBar.ValueProperty) == value) return;
            double from = bar.Value;
            bar.Value = value;
            bar.BeginAnimation(ProgressBar.ValueProperty, CanAnimate(sys) ? Transition(from, value, 260) : null);
        }

        internal static Action AttachShimmer(Border clipHost, Border sheenBar, SystemOptions sys)
        {
            sheenBar.IsHitTestVisible = false;
            sheenBar.Visibility = CanAnimate(sys) ? Visibility.Visible : Visibility.Collapsed;
            if (!CanAnimate(sys)) return delegate { };
            clipHost.ClipToBounds = true;
            var shift = new TranslateTransform();
            sheenBar.RenderTransform = shift;
            Action start = delegate {
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-sheenBar.Width,
                    clipHost.ActualWidth, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever });
            };
            SizeChangedEventHandler resize = delegate { start(); };
            clipHost.SizeChanged += resize;
            start();
            return delegate {
                clipHost.SizeChanged -= resize;
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                sheenBar.Visibility = Visibility.Collapsed;
            };
        }

        internal static void AttachCardLift(FrameworkElement card, SystemOptions sys)
        {
            if (!CanAnimate(sys)) return;
            var shift = new TranslateTransform();
            card.RenderTransform = shift;
            card.Resources["FlowPressTransform"] = new ScaleTransform(.98,.98);
            Action<double,int> lift = (to,ms) => {
                double from = shift.Y;
                shift.Y = to;
                shift.BeginAnimation(TranslateTransform.YProperty, Transition(from,to,ms));
            };
            card.MouseEnter += delegate { lift(-2,140); };
            card.MouseLeave += delegate { lift(0,160); };
            card.IsEnabledChanged += delegate { if (!card.IsEnabled) lift(0,140); };
        }

        internal static void AnimateReveal(UIElement panel, SystemOptions sys)
        {
            if (!CanAnimate(sys)) return;
            var shift = new TranslateTransform();
            panel.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.YProperty, Transition(6,0,175));
            panel.BeginAnimation(UIElement.OpacityProperty, Transition(0,1,175));
        }

        internal static void SetRotatingRing(FrameworkElement target, bool active, SystemOptions sys)
        {
            var rotation = target.RenderTransform as RotateTransform;
            if (rotation == null || rotation.IsFrozen)
            {
                rotation = new RotateTransform();
                target.RenderTransform = rotation;
                target.RenderTransformOrigin = new Point(.5,.5);
            }
            rotation.BeginAnimation(RotateTransform.AngleProperty, active && CanAnimate(sys)
                ? new DoubleAnimation(0,360,TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever } : null);
        }

        internal static void SafeCancel(CancellationTokenSource cts)
        {
            if (cts == null) return;
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        internal static bool IsCancelled(CancellationTokenSource cts)
        {
            if (cts == null) return false;
            try { return cts.IsCancellationRequested; }
            catch (ObjectDisposedException) { return true; }
        }
    }
}
