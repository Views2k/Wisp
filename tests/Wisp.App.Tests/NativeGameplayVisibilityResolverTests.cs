using System.Text.Json.Nodes;
using Wisp.Core;
using Xunit;

namespace Wisp.App.Tests;

public sealed class NativeGameplayVisibilityResolverTests
{
    private const ulong Module = 0x140000000;
    private const ulong Service = 0x200000000;
    private const ulong Dependency = 0x300000000;
    private const ulong Page = 0x400000000;
    private const ulong PageManager = 0x500000000;
    private const ulong NestedPage = 0x600000000;
    private static NativeGameplayVisibilityLayout Layout => NativeHudBuildContract.BuiltIn.GameplayVisibility!;
    private static ulong Manager => Dependency + Layout.RootTransitionManagerOffset;

    [Fact]
    public void DrivingHudDoesNotRequireTachAssistOrStockSpeedometerFields()
    {
        // No vehicle provider or per-speedometer settings exist in this memory.
        Assert.Equal(NativeGameplayVisibility.Visible, Resolve(DrivingMemory()));
    }

    [Fact]
    public void ActiveDestinationMenuHidesEvenIfItsOwnPageIsVisible()
    {
        var memory = DrivingMemory();
        memory.SetUInt64(Page, Module + 0x06E5E980);
        Assert.Equal(NativeGameplayVisibility.Hidden, Resolve(memory));
    }

    [Fact]
    public void InheritedPageOpacityGateHidesTheDrivingPage()
    {
        var memory = DrivingMemory();
        memory.SetByte(Page + Layout.PageUiVisibleOffset, 0);
        Assert.Equal(NativeGameplayVisibility.Hidden, Resolve(memory));
    }

    [Fact]
    public void NestedMenuHidesWhileTheDrivingRootRemainsVisible()
    {
        var memory = DrivingMemory();
        memory.SetUInt32(PageManager + Layout.ManagerStateOffset, 6);
        memory.SetUInt64(PageManager + Layout.ManagerCurrentPageOffset, NestedPage);
        memory.SetUInt64(NestedPage, Module + 0x06E5E980);

        Assert.Equal(NativeGameplayVisibility.Hidden, Resolve(memory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void UnsettledNestedNavigationDoesNotExposeTheDrivingHud(uint state)
    {
        var memory = DrivingMemory();
        memory.SetUInt32(PageManager + Layout.ManagerStateOffset, state);

        Assert.Equal(NativeGameplayVisibility.Hidden, Resolve(memory));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(uint.MaxValue)]
    public void InvalidNestedManagerStateIsUnknown(uint state)
    {
        var memory = DrivingMemory();
        memory.SetUInt32(PageManager + Layout.ManagerStateOffset, state);

        Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
    }

    [Fact]
    public void NestedPageRequiresAnImageBackedType()
    {
        var memory = DrivingMemory();
        memory.SetUInt32(PageManager + Layout.ManagerStateOffset, 6);
        memory.SetUInt64(PageManager + Layout.ManagerCurrentPageOffset, NestedPage);
        memory.SetUInt64(NestedPage, NestedPage);

        Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
    }

    [Fact]
    public void EmptyPageManagerHidesWithoutReadingAPage()
    {
        var memory = DrivingMemory();
        memory.SetUInt64(Manager + Layout.ManagerCurrentPageOffset, 0);
        memory.Remove(Page);
        Assert.Equal(NativeGameplayVisibility.Hidden, Resolve(memory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void UnsettledNavigationDoesNotExposeThePreviousHud(uint state)
    {
        var memory = DrivingMemory();
        memory.SetUInt32(Manager + Layout.ManagerStateOffset, state);
        Assert.Equal(NativeGameplayVisibility.Hidden, Resolve(memory));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(uint.MaxValue)]
    public void InvalidManagerStateIsUnknown(uint state)
    {
        var memory = DrivingMemory();
        memory.SetUInt32(Manager + Layout.ManagerStateOffset, state);
        Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(255)]
    public void InvalidVisibilityByteFailsClosed(byte value)
    {
        var memory = DrivingMemory();
        memory.SetByte(Page + Layout.PageUiVisibleOffset, value);
        Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
    }

    [Theory]
    [InlineData("service")]
    [InlineData("dependency")]
    [InlineData("manager")]
    [InlineData("owner")]
    [InlineData("page")]
    [InlineData("pageManager")]
    [InlineData("pageOwner")]
    public void EveryTypeAndOwnershipGuardIsRequired(string guard)
    {
        var memory = DrivingMemory();
        var address = guard switch
        {
            "service" => Service,
            "dependency" => Dependency,
            "manager" => Manager,
            "owner" => Manager + Layout.ManagerOwnerOffset,
            "pageManager" => PageManager,
            "pageOwner" => PageManager + Layout.ManagerOwnerOffset,
            _ => Page
        };
        memory.SetUInt64(address, 0);
        Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(0xFFFFUL)]
    [InlineData(0x200000001UL)]
    [InlineData(0x00007FFFFFFFFFF8UL)]
    [InlineData(ulong.MaxValue)]
    public void UnsafeObjectAddressesAreRejected(ulong address)
    {
        var memory = DrivingMemory();
        memory.SetUInt64(Module + Layout.UiServiceRva, address);
        Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
    }

    [Theory]
    [InlineData("dependency")]
    [InlineData("page")]
    [InlineData("pageManager")]
    [InlineData("nestedPage")]
    public void UnsafeDependentObjectAddressesAreRejectedWithoutDereferencing(string pointer)
    {
        foreach (var value in new[] { 0UL, 0xFFF8UL, 0x600000001UL, 0x00007FFFFFFFFFF8UL, ulong.MaxValue })
        {
            // An empty current page is a valid hidden state, not an unsafe pointer.
            if ((pointer is "page" or "nestedPage") && value == 0)
                continue;

            var memory = DrivingMemory();
            memory.SetUInt64(PointerSlot(pointer), value);
            var dereferenced = false;
            memory.BeforeRead = (address, _) => dereferenced |= address == value;

            Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
            Assert.False(dereferenced);
        }
    }

    [Theory]
    [InlineData("dependency")]
    [InlineData("page")]
    [InlineData("pageManager")]
    [InlineData("nestedPage")]
    public void MissingDependentPointersAndUnreadableObjectsAreUnknown(string pointer)
    {
        foreach (var unreadableObject in new[] { false, true })
        {
            var memory = DrivingMemory();
            if (unreadableObject)
                memory.SetUInt64(PointerSlot(pointer), 0x600000000UL);
            else
                memory.Remove(PointerSlot(pointer));

            Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
        }
    }

    [Fact]
    public void FailedReadNeverReusesThePreviousVisibleResult()
    {
        var memory = DrivingMemory();
        var resolver = new NativeGameplayVisibilityResolver();
        Assert.Equal(NativeGameplayVisibility.Visible, resolver.Resolve(memory, Module));
        memory.Remove(Page + Layout.PageUiVisibleOffset);
        Assert.Equal(NativeGameplayVisibility.Unknown, resolver.Resolve(memory, Module));
    }

    [Theory]
    [InlineData("page")]
    [InlineData("state")]
    [InlineData("type")]
    [InlineData("visibility")]
    public void TornNavigationAndPageReadsAreRejected(string change)
    {
        var memory = DrivingMemory();
        var watched = change switch
        {
            "page" => Manager + Layout.ManagerCurrentPageOffset,
            "state" => Manager + Layout.ManagerStateOffset,
            "type" => Page,
            _ => Page + Layout.PageUiVisibleOffset
        };
        memory.BeforeRead = (address, occurrence) =>
        {
            if (address != watched || occurrence != 2)
            {
                return;
            }

            if (change == "visibility")
                memory.SetByte(address, 0);
            else if (change == "state")
                memory.SetUInt32(address, 0);
            else
                memory.SetUInt64(address, change == "type" ? Module + 0x06E5E980 : 0);
        };
        Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
    }

    [Fact]
    public void ValidPageManagerReplacementBetweenReadsIsUnknown()
    {
        var memory = DrivingMemory();
        const ulong replacement = 0x600000000;
        memory.SetUInt64(replacement, Module + Layout.TransitionManagerVtableRva);
        memory.SetUInt64(replacement + Layout.ManagerOwnerOffset, Dependency);
        memory.SetUInt32(replacement + Layout.ManagerStateOffset, 6);
        memory.SetUInt64(replacement + Layout.ManagerCurrentPageOffset, 0);
        var changed = false;
        memory.BeforeRead = (address, occurrence) =>
        {
            if (address == Page + Layout.PageTransitionManagerOffset && occurrence == 2)
            {
                memory.SetUInt64(address, replacement);
                changed = true;
            }
        };

        Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
        Assert.True(changed);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("page")]
    [InlineData("type")]
    public void TornNestedNavigationReadsAreUnknown(string change)
    {
        var memory = DrivingMemory();
        if (change == "type")
        {
            memory.SetUInt32(PageManager + Layout.ManagerStateOffset, 6);
            memory.SetUInt64(PageManager + Layout.ManagerCurrentPageOffset, NestedPage);
            memory.SetUInt64(NestedPage, Module + 0x06E5E980);
        }

        var watched = change switch
        {
            "state" => PageManager + Layout.ManagerStateOffset,
            "page" => PageManager + Layout.ManagerCurrentPageOffset,
            _ => NestedPage
        };
        memory.BeforeRead = (address, occurrence) =>
        {
            if (address != watched || occurrence != 2)
                return;

            if (change == "state")
                memory.SetUInt32(address, 5);
            else if (change == "page")
            {
                memory.SetUInt64(NestedPage, Module + 0x06E5E980);
                memory.SetUInt64(address, NestedPage);
            }
            else
                memory.SetUInt64(address, Module + 0x06E849E8);
        };

        Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
    }

    [Theory]
    [InlineData("service")]
    [InlineData("dependency")]
    [InlineData("manager")]
    [InlineData("owner")]
    [InlineData("pageManager")]
    [InlineData("pageOwner")]
    public void RootAndNestedGuardsAreRevalidatedOnTheSecondRead(string guard)
    {
        foreach (var unreadable in new[] { false, true })
        {
            var memory = DrivingMemory();
            var watched = guard switch
            {
                "service" => Service,
                "dependency" => Dependency,
                "manager" => Manager,
                "owner" => Manager + Layout.ManagerOwnerOffset,
                "pageManager" => PageManager,
                "pageOwner" => PageManager + Layout.ManagerOwnerOffset,
                _ => throw new ArgumentOutOfRangeException(nameof(guard))
            };
            var changed = false;
            memory.BeforeRead = (address, occurrence) =>
            {
                if (address != watched || occurrence != 2)
                    return;

                if (unreadable)
                    memory.Remove(address);
                else
                    memory.SetUInt64(address, 0);
                changed = true;
            };

            Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
            Assert.True(changed);
        }
    }

    [Theory]
    [InlineData("service")]
    [InlineData("dependency")]
    public void ValidRootObjectReplacementBetweenReadsIsUnknown(string pointer)
    {
        var memory = DrivingMemory();
        const ulong replacement = 0x600000000;
        ulong watched;
        if (pointer == "service")
        {
            memory.SetUInt64(replacement, Module + Layout.UiServiceVtableRva);
            memory.SetUInt64(replacement + Layout.ServiceDependencyOffset, Dependency);
            watched = Module + Layout.UiServiceRva;
        }
        else
        {
            var replacementManager = replacement + Layout.RootTransitionManagerOffset;
            memory.SetUInt64(replacement, Module + Layout.DependencyVtableRva);
            memory.SetUInt64(replacementManager, Module + Layout.TransitionManagerVtableRva);
            memory.SetUInt64(replacementManager + Layout.ManagerOwnerOffset, replacement);
            memory.SetUInt32(replacementManager + Layout.ManagerStateOffset, 6);
            memory.SetUInt64(replacementManager + Layout.ManagerCurrentPageOffset, Page);
            watched = Service + Layout.ServiceDependencyOffset;
        }

        var changed = false;
        memory.BeforeRead = (address, occurrence) =>
        {
            if (address == watched && occurrence == 2)
            {
                memory.SetUInt64(address, replacement);
                changed = true;
            }
        };

        Assert.Equal(NativeGameplayVisibility.Unknown, Resolve(memory));
        Assert.True(changed);
    }

    [Fact]
    public void LegacyPackDoesNotAttemptUnspecifiedReads()
    {
        using var stream = typeof(NativeHudBuildContract).Assembly.GetManifestResourceStream("Wisp.NativeCompatibility.BuiltIn.json")!;
        var document = JsonNode.Parse(stream)!.AsObject();
        document["schemaVersion"] = 1;
        document["readerVersion"] = 1;
        document.Remove("gameplayVisibility");
        document.Remove("nativeGauge");
        var pack = NativeHudCompatibilityPack.Parse(System.Text.Encoding.UTF8.GetBytes(document.ToJsonString()));
        var memory = new Memory();
        Assert.Equal(NativeGameplayVisibility.Unknown, new NativeGameplayVisibilityResolver(pack).Resolve(memory, Module));
        Assert.Equal(0, memory.ReadCount);
    }

    private static NativeGameplayVisibility Resolve(Memory memory) =>
        new NativeGameplayVisibilityResolver().Resolve(memory, Module);

    private static ulong PointerSlot(string pointer) => pointer switch
    {
        "dependency" => Service + Layout.ServiceDependencyOffset,
        "page" => Manager + Layout.ManagerCurrentPageOffset,
        "pageManager" => Page + Layout.PageTransitionManagerOffset,
        "nestedPage" => PageManager + Layout.ManagerCurrentPageOffset,
        _ => throw new ArgumentOutOfRangeException(nameof(pointer))
    };

    private static Memory DrivingMemory()
    {
        var memory = new Memory();
        memory.SetUInt64(Module + Layout.UiServiceRva, Service);
        memory.SetUInt64(Service, Module + Layout.UiServiceVtableRva);
        memory.SetUInt64(Service + Layout.ServiceDependencyOffset, Dependency);
        memory.SetUInt64(Dependency, Module + Layout.DependencyVtableRva);
        memory.SetUInt64(Manager, Module + Layout.TransitionManagerVtableRva);
        memory.SetUInt64(Manager + Layout.ManagerOwnerOffset, Dependency);
        memory.SetUInt32(Manager + Layout.ManagerStateOffset, 6);
        memory.SetUInt64(Manager + Layout.ManagerCurrentPageOffset, Page);
        memory.SetUInt64(Page, Module + Layout.HudPageVtableRva);
        memory.SetByte(Page + Layout.PageUiVisibleOffset, 1);
        memory.SetUInt64(Page + Layout.PageTransitionManagerOffset, PageManager);
        memory.SetUInt64(PageManager, Module + Layout.TransitionManagerVtableRva);
        memory.SetUInt64(PageManager + Layout.ManagerOwnerOffset, Dependency);
        memory.SetUInt32(PageManager + Layout.ManagerStateOffset, 6);
        memory.SetUInt64(PageManager + Layout.ManagerCurrentPageOffset, 0);
        return memory;
    }

    private sealed class Memory : IReadOnlyProcessMemory
    {
        private readonly Dictionary<ulong, byte> _bytes = [];
        private readonly Dictionary<ulong, int> _reads = [];
        public Action<ulong, int>? BeforeRead { get; set; }
        public int ReadCount { get; private set; }
        public void Remove(ulong address) => _bytes.Remove(address);
        public void SetByte(ulong address, byte value) => _bytes[address] = value;
        public void SetUInt32(ulong address, uint value) => Set(address, BitConverter.GetBytes(value));
        public void SetUInt64(ulong address, ulong value) => Set(address, BitConverter.GetBytes(value));
        public bool TryReadByte(ulong address, out byte value)
        {
            Observe(address);
            return _bytes.TryGetValue(address, out value);
        }
        public bool TryReadUInt32(ulong address, out uint value)
        {
            var success = Read(address, 4, out var bytes);
            value = success ? BitConverter.ToUInt32(bytes) : 0;
            return success;
        }
        public bool TryReadUInt64(ulong address, out ulong value)
        {
            var success = Read(address, 8, out var bytes);
            value = success ? BitConverter.ToUInt64(bytes) : 0;
            return success;
        }
        public bool TryReadSingle(ulong address, out float value)
        {
            var success = TryReadUInt32(address, out var bits);
            value = BitConverter.UInt32BitsToSingle(bits);
            return success;
        }
        private void Set(ulong address, byte[] bytes)
        {
            for (var index = 0; index < bytes.Length; index++)
                _bytes[address + (ulong)index] = bytes[index];
        }
        private bool Read(ulong address, int width, out byte[] bytes)
        {
            Observe(address);
            bytes = new byte[width];
            for (var index = 0; index < width; index++)
                if (!_bytes.TryGetValue(address + (ulong)index, out bytes[index]))
                    return false;
            return true;
        }
        private void Observe(ulong address)
        {
            ReadCount++;
            _reads.TryGetValue(address, out var count);
            _reads[address] = ++count;
            BeforeRead?.Invoke(address, count);
        }
    }
}
