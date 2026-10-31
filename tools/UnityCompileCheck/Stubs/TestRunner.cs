// Compile-check stand-in for UnityEngine.TestRunner.dll (not available on NuGet). Unity provides the real one.
namespace UnityEngine.TestTools
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class UnityTestAttribute : System.Attribute { }
}
