using System.Windows;

namespace Wisp.App;

public partial class MainWindow
{
    private bool _applicationStyleReady;
    private AppStyleSettings? _pendingApplicationStyle;

    private void InitializeApplicationStyle()
    {
        _applicationStyleReady = false;
        var style = _dashboardController.Settings.ApplicationStyle;
        StyleGlowStrength.Value = style.GlowStrength;
        StyleSurfaceOpacity.Value = style.SurfaceOpacity;
        StyleBorderThickness.Value = style.BorderThickness;
        StyleCornerRadius.Value = style.CornerRadius;
        StyleCardPadding.Value = style.CardPadding;
        _applicationStyleReady = true;
    }

    private void ApplicationStyle_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_applicationStyleReady) return;
        var style = _dashboardController.Settings.ApplicationStyle.Clone();
        style.GlowStrength = StyleGlowStrength.Value;
        style.SurfaceOpacity = StyleSurfaceOpacity.Value;
        style.BorderThickness = StyleBorderThickness.Value;
        style.CornerRadius = StyleCornerRadius.Value;
        style.CardPadding = StyleCardPadding.Value;
        style.Normalize();
        _pendingApplicationStyle = style;
        ApplyApplicationStyle(style);
    }

    private void ApplyApplicationStyle(AppStyleSettings style) => AppThemeResources.Apply(Resources,
        AppColorThemes.Resolve(_dashboardController.Settings.ColorTheme),
        AppBackgroundThemes.Resolve(_dashboardController.Settings.BackgroundTheme),
        _dashboardController.Settings.CustomAccentColor, _dashboardController.Settings.CustomBackgroundColor, style,
        _dashboardController.Settings.CustomParticleColor);

    private void ApplicationStyle_Commit(object sender, RoutedEventArgs e) => CommitApplicationStyle();

    private void CommitApplicationStyle()
    {
        if (_pendingApplicationStyle is not { } style) return;
        _dashboardController.SetApplicationStyle(style);
        _pendingApplicationStyle = null;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CommitApplicationStyle();
        base.OnClosing(e);
    }

    private void ResetApplicationStyle_Click(object sender, RoutedEventArgs e)
    {
        var saved = _dashboardController.Settings.ApplicationStyle;
        var defaults = new AppStyleSettings
        {
            BorderColor = saved.BorderColor,
            TextColor = saved.TextColor,
            MutedTextColor = saved.MutedTextColor
        };
        _dashboardController.SetApplicationStyle(defaults);
        _pendingApplicationStyle = null;
        InitializeApplicationStyle();
        ApplyApplicationStyle(defaults);
    }
}
