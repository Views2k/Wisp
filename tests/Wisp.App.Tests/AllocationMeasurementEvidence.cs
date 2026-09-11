using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Wisp.App.Tests;

internal sealed class AllocationMeasurementEvidence : IDisposable
{
    private const int Capacity = 8;
    private readonly Assembly?[] _assemblies = new Assembly?[Capacity];
    private readonly Exception?[] _exceptions = new Exception?[Capacity];
    private readonly string _runtime = RuntimeInformation.FrameworkDescription;
    private int _producerThreadId;
    private int _assemblyCount;
    private int _exceptionCount;

    internal AllocationMeasurementEvidence()
    {
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
    }

    internal void Start() => Volatile.Write(ref _producerThreadId, Environment.CurrentManagedThreadId);
    internal void Stop() => Volatile.Write(ref _producerThreadId, 0);

    private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
    {
        if (Volatile.Read(ref _producerThreadId) != Environment.CurrentManagedThreadId) return;
        var index = _assemblyCount++;
        if (index < Capacity) _assemblies[index] = args.LoadedAssembly;
    }

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs args)
    {
        if (Volatile.Read(ref _producerThreadId) != Environment.CurrentManagedThreadId) return;
        var index = _exceptionCount++;
        if (index < Capacity) _exceptions[index] = args.Exception;
    }

    internal string Summary()
    {
        Stop();
        var assemblies = string.Join(", ", _assemblies.Take(Math.Min(_assemblyCount, Capacity))
            .Select(assembly => assembly!.GetName().Name));
        var exceptions = string.Join(", ", _exceptions.Take(Math.Min(_exceptionCount, Capacity))
            .Select(exception => exception!.GetType().FullName));
        return $"Runtime: {_runtime}; producer assembly loads: {_assemblyCount} [{assemblies}]; " +
            $"producer first-chance exceptions: {_exceptionCount} [{exceptions}]. At most {Capacity} names per event kind.";
    }

    public void Dispose()
    {
        Stop();
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
    }
}
