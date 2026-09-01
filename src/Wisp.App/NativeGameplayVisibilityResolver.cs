using Wisp.Core;

namespace Wisp.App;

public sealed class NativeGameplayVisibilityResolver : INativeGameplayVisibilityResolver
{
    private const uint SettledTransitionState = 6;
    private readonly NativeHudCompatibilityPack _pack;

    public NativeGameplayVisibilityResolver(NativeHudCompatibilityPack? pack = null)
    {
        _pack = pack ?? NativeHudBuildContract.BuiltIn;
    }

    public NativeGameplayVisibility Resolve(IReadOnlyProcessMemory memory, ulong moduleBase)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (_pack.GameplayVisibility is not { } layout ||
            !IsObjectPointer(moduleBase) || !IsAddressRange(moduleBase, _pack.ImageSize) ||
            !TryReadRoot(memory, moduleBase, layout, out var before))
        {
            return NativeGameplayVisibility.Unknown;
        }

        var visibility = NativeGameplayVisibility.Hidden;
        byte uiVisible = 0;
        PageManagerSnapshot nestedManager = default;
        if (before.Page != 0)
        {
            if (!memory.TryReadUInt64(before.Page, out var pageVtable) ||
                !IsImagePointer(pageVtable, moduleBase))
            {
                return NativeGameplayVisibility.Unknown;
            }

            if (pageVtable == moduleBase + layout.HudPageVtableRva)
            {
                if (!memory.TryReadByte(before.Page + layout.PageUiVisibleOffset, out uiVisible) || uiVisible > 1 ||
                    !TryReadPageManager(memory, moduleBase, layout, before, out nestedManager))
                {
                    return NativeGameplayVisibility.Unknown;
                }

                // FH6 free roam keeps both transition managers settled at state
                // six while the nested manager has no current page. Menus and
                // cinematics either own a nested page or leave that manager in
                // an unsettled state during navigation.
                visibility = before.State == SettledTransitionState && uiVisible == 1 &&
                             nestedManager.State == SettledTransitionState && nestedManager.Page == 0
                    ? NativeGameplayVisibility.Visible
                    : NativeGameplayVisibility.Hidden;
            }

            if (!memory.TryReadUInt64(before.Page, out var currentVtable) || currentVtable != pageVtable ||
                pageVtable == moduleBase + layout.HudPageVtableRva &&
                (!memory.TryReadByte(before.Page + layout.PageUiVisibleOffset, out var currentUiVisible) ||
                 currentUiVisible != uiVisible ||
                 !TryReadPageManager(memory, moduleBase, layout, before, out var currentNestedManager) ||
                 currentNestedManager != nestedManager))
            {
                return NativeGameplayVisibility.Unknown;
            }
        }

        // Navigation can replace or destroy a page between individual reads.
        // Publish only a stable ownership/state observation; never retain a
        // previous visible result after a failed or inconsistent read.
        return TryReadRoot(memory, moduleBase, layout, out var after) && before == after
            ? visibility
            : NativeGameplayVisibility.Unknown;
    }

    private bool TryReadPageManager(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        NativeGameplayVisibilityLayout layout,
        RootSnapshot root,
        out PageManagerSnapshot snapshot)
    {
        snapshot = default;
        if (!memory.TryReadUInt64(root.Page + layout.PageTransitionManagerOffset, out var manager) ||
            !IsObjectPointer(manager) ||
            !memory.TryReadUInt64(manager, out var vtable) ||
            vtable != moduleBase + layout.TransitionManagerVtableRva ||
            !memory.TryReadUInt64(manager + layout.ManagerOwnerOffset, out var owner) || owner != root.Dependency ||
            !memory.TryReadUInt32(manager + layout.ManagerStateOffset, out var state) || state > SettledTransitionState ||
            !memory.TryReadUInt64(manager + layout.ManagerCurrentPageOffset, out var page) ||
            page != 0 && !IsObjectPointer(page))
        {
            return false;
        }

        ulong pageVtable = 0;
        if (page != 0 &&
            (!memory.TryReadUInt64(page, out pageVtable) || !IsImagePointer(pageVtable, moduleBase)))
        {
            return false;
        }

        snapshot = new PageManagerSnapshot(manager, state, page, pageVtable);
        return true;
    }

    private bool TryReadRoot(
        IReadOnlyProcessMemory memory,
        ulong moduleBase,
        NativeGameplayVisibilityLayout layout,
        out RootSnapshot snapshot)
    {
        snapshot = default;
        if (!memory.TryReadUInt64(moduleBase + layout.UiServiceRva, out var service) || !IsObjectPointer(service) ||
            !memory.TryReadUInt64(service, out var serviceVtable) || serviceVtable != moduleBase + layout.UiServiceVtableRva ||
            !memory.TryReadUInt64(service + layout.ServiceDependencyOffset, out var dependency) || !IsObjectPointer(dependency) ||
            !memory.TryReadUInt64(dependency, out var dependencyVtable) || dependencyVtable != moduleBase + layout.DependencyVtableRva)
        {
            return false;
        }

        var manager = dependency + layout.RootTransitionManagerOffset;
        if (!memory.TryReadUInt64(manager, out var managerVtable) || managerVtable != moduleBase + layout.TransitionManagerVtableRva ||
            !memory.TryReadUInt64(manager + layout.ManagerOwnerOffset, out var owner) || owner != dependency ||
            !memory.TryReadUInt32(manager + layout.ManagerStateOffset, out var state) || state > SettledTransitionState ||
            !memory.TryReadUInt64(manager + layout.ManagerCurrentPageOffset, out var page) ||
            page != 0 && !IsObjectPointer(page))
        {
            return false;
        }

        snapshot = new RootSnapshot(service, dependency, state, page);
        return true;
    }

    private bool IsImagePointer(ulong address, ulong moduleBase) =>
        (address & 7) == 0 && address >= moduleBase && address - moduleBase < _pack.ImageSize;

    private static bool IsObjectPointer(ulong address) =>
        (address & 7) == 0 && IsAddressRange(address, NativeHudCompatibilityPack.MaximumFieldBytes);

    private static bool IsAddressRange(ulong address, ulong bytes) =>
        address is >= 0x10000 and <= 0x00007FFFFFFFFFFF && bytes > 0 &&
        bytes - 1 <= 0x00007FFFFFFFFFFF - address;

    private readonly record struct RootSnapshot(ulong Service, ulong Dependency, uint State, ulong Page);
    private readonly record struct PageManagerSnapshot(ulong Manager, uint State, ulong Page, ulong PageVtable);
}
