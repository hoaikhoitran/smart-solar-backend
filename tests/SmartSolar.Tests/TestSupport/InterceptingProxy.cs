using System.Reflection;
using System.Runtime.ExceptionServices;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Forwards every call of an interface to <c>inner</c>, running <c>before</c> first (synchronously). Used to interleave a
/// "concurrent" change at an exact point of a handler (e.g. right before its transaction starts) without a second process.
/// Returning a non-null value from <c>replace</c> short-circuits the call with that value.
/// </summary>
public class InterceptingProxy<T> : DispatchProxy where T : class
{
    private T _inner = null!;
    private Action<MethodInfo, object?[]>? _before;
    private Func<MethodInfo, object?[], object?>? _replace;

    public static T Create(T inner, Action<MethodInfo, object?[]>? before = null, Func<MethodInfo, object?[], object?>? replace = null)
    {
        var proxy = Create<T, InterceptingProxy<T>>();
        var self = (InterceptingProxy<T>)(object)proxy;
        self._inner = inner;
        self._before = before;
        self._replace = replace;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        var arguments = args ?? [];
        _before?.Invoke(method, arguments);
        if (_replace?.Invoke(method, arguments) is { } replaced) return replaced;
        try
        {
            return method.Invoke(_inner, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
