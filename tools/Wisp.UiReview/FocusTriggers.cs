using System.Windows;
using System.Windows.Controls;
using Wisp.App;

namespace Wisp.UiReview;

// Focus outlines are keyboard-only: each focus trigger pairs the focus
// property with FocusCues.ShowKeyboardFocus so a click does not outline.
internal static class FocusTriggers
{
    internal static bool HasKeyboardOnly(ControlTemplate template, DependencyProperty focus) =>
        template.Triggers.OfType<MultiTrigger>().Any(trigger =>
            trigger.Conditions.Any(condition => condition.Property == focus && Equals(condition.Value, true)) &&
            trigger.Conditions.Any(condition => condition.Property == FocusCues.ShowKeyboardFocusProperty && Equals(condition.Value, true)));
}
