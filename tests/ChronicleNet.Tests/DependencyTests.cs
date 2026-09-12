using System.Reflection;

namespace ChronicleNet.Tests;

public class DependencyTests
{
    [Fact]
    public void Core_library_references_only_the_base_class_library()
    {
        Assembly core = typeof(ChronicleQueue).Assembly;

        string[] thirdParty = core
            .GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => !IsPlatformAssembly(name))
            .ToArray();

        Assert.Empty(thirdParty);
    }

    private static bool IsPlatformAssembly(string name)
        => name is "netstandard" or "mscorlib"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);
}
