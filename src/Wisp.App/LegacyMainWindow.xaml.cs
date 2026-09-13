using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Wisp.App;

public partial class LegacyMainWindow : ControlPanelWindow
{
    private const double SidebarWidth = 168;
    private int _sidebarAnimationVersion;

    public LegacyMainWindow(AppController controller) : base(controller)
    {
        InitializeComponent();
        PageScrollRouting.SetIsEnabled((UIElement)Content, true);
        InitializeControlPanel();
    }

    protected override void ApplySidebarLayout(bool open, bool animate)
    {
        var targetWidth = open ? SidebarWidth : 0;
        var contentOffset = SidebarColumn.Width.Value + ContentTranslation.X - targetWidth;
        var sidebarOffset = SidebarTranslation.X;
        var chevronAngle = SidebarChevronRotation.Angle;
        StopSidebarAnimation();
        var animationVersion = _sidebarAnimationVersion;

        if (!open && SidebarHost.IsKeyboardFocusWithin)
        {
            SidebarToggleButton.Focus();
        }

        // Layout changes once; the brief transition only animates render transforms.
        SidebarColumn.Width = new GridLength(targetWidth);
        SidebarHost.IsEnabled = open;
        SidebarHost.IsHitTestVisible = open;
        ContentTranslation.X = 0;
        SidebarTranslation.X = open ? 0 : -SidebarWidth;
        SidebarChevronRotation.Angle = open ? 0 : 180;
        var label = open ? "Hide sidebar" : "Show sidebar";
        SidebarToggleButton.ToolTip = label;
        AutomationProperties.SetName(SidebarToggleButton, label);

        if (!animate || !IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            SidebarHost.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        SidebarHost.Visibility = Visibility.Visible;
        // A new WPF clock starts on the next render tick. Keep its unanimated
        // value at the current position so rapid reversals cannot flash the end state.
        ContentTranslation.X = contentOffset;
        SidebarTranslation.X = sidebarOffset;
        SidebarChevronRotation.Angle = chevronAngle;
        var contentAnimation = SidebarAnimation(contentOffset, 0);
        contentAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _sidebarAnimationVersion)
            {
                return;
            }

            StopSidebarAnimation();
            ContentTranslation.X = 0;
            SidebarTranslation.X = IsSidebarOpen ? 0 : -SidebarWidth;
            SidebarChevronRotation.Angle = IsSidebarOpen ? 0 : 180;
            SidebarHost.Visibility = IsSidebarOpen ? Visibility.Visible : Visibility.Collapsed;
        };
        ContentTranslation.BeginAnimation(TranslateTransform.XProperty, contentAnimation);
        SidebarTranslation.BeginAnimation(TranslateTransform.XProperty,
            SidebarAnimation(sidebarOffset, open ? 0 : -SidebarWidth));
        SidebarChevronRotation.BeginAnimation(RotateTransform.AngleProperty,
            SidebarAnimation(chevronAngle, open ? 0 : 180));
    }

    private static DoubleAnimation SidebarAnimation(double from, double to) => new(from, to,
        new Duration(TimeSpan.FromMilliseconds(200)))
    {
        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        FillBehavior = FillBehavior.Stop
    };

    protected override void StopSidebarAnimation()
    {
        _sidebarAnimationVersion++;
        ContentTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        SidebarTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        SidebarChevronRotation.BeginAnimation(RotateTransform.AngleProperty, null);
    }
}
