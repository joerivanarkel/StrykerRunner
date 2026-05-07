namespace StrykerRunner.Tests;

public class ProjectDiscoveryTests
{
    [Fact]
    public void TestProjectNameExtraction_WithTestsExtension()
    {
        var testPath = "MyLibrary.Tests.csproj";
        var name = Path.GetFileNameWithoutExtension(testPath);
        Assert.EndsWith(".Tests", name);
    }

    [Fact]
    public void TestProjectNameExtraction_WithTestExtension()
    {
        var testPath = "MyLibrary.Test.csproj";
        var name = Path.GetFileNameWithoutExtension(testPath);
        Assert.EndsWith(".Test", name);
    }

    [Fact]
    public void SourceProjectNameExtraction()
    {
        var testPath = "MyLibrary.csproj";
        var name = Path.GetFileNameWithoutExtension(testPath);
        Assert.NotEmpty(name);
        Assert.Equal("MyLibrary", name);
    }
}
