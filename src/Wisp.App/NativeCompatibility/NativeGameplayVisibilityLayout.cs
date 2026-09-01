namespace Wisp.App;

public sealed class NativeGameplayVisibilityLayout
{
    internal NativeGameplayVisibilityLayout(IReadOnlyDictionary<string, ulong> values)
    {
        UiServiceRva = values["uiServiceRva"];
        UiServiceVtableRva = values["uiServiceVtableRva"];
        DependencyVtableRva = values["dependencyVtableRva"];
        TransitionManagerVtableRva = values["transitionManagerVtableRva"];
        HudPageVtableRva = values["hudPageVtableRva"];
        ServiceDependencyOffset = values["serviceDependencyOffset"];
        RootTransitionManagerOffset = values["rootTransitionManagerOffset"];
        ManagerOwnerOffset = values["managerOwnerOffset"];
        ManagerCurrentPageOffset = values["managerCurrentPageOffset"];
        ManagerStateOffset = values["managerStateOffset"];
        PageTransitionManagerOffset = values["pageTransitionManagerOffset"];
        PageUiVisibleOffset = values["pageUiVisibleOffset"];
    }

    public ulong UiServiceRva { get; }
    public ulong UiServiceVtableRva { get; }
    public ulong DependencyVtableRva { get; }
    public ulong TransitionManagerVtableRva { get; }
    public ulong HudPageVtableRva { get; }
    public ulong ServiceDependencyOffset { get; }
    public ulong RootTransitionManagerOffset { get; }
    public ulong ManagerOwnerOffset { get; }
    public ulong ManagerCurrentPageOffset { get; }
    public ulong ManagerStateOffset { get; }
    public ulong PageTransitionManagerOffset { get; }
    public ulong PageUiVisibleOffset { get; }
}
