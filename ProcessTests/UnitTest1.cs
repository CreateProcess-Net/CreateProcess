using System.IO;
using System.Reactive;
using NUnit.Framework;
using Process;

namespace ProcessTests;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public void RedirectToFile()
    {
        var shell = new ProcessShell();
        shell.Run(
            CreateProcess.FromCommandLine("echo", "test single argument")
            > Redirect.ToFile("test.txt")
            > Redirect.ToFile("second.txt")
            );
        Assert.AreEqual("test single argument", File.ReadAllText("test.txt"));
        Assert.AreEqual("test single argument", File.ReadAllText("second.txt"));
    }
    
    [Test]
    public void RedirectToFileAndPipeToCat()
    {
        var shell = new ProcessShell();
        var result = shell.Run(
            CreateProcess.FromCommandLine("echo", "test single argument")
            > Redirect.ToFile("test.txt")
            | CreateProcess.FromCommandLine("cat")
        );
        Assert.AreEqual("test single argument", File.ReadAllText("test.txt"));
    }
    
    [Test]
    public void RetrieveData()
    {
        var shell = new ProcessShell();
        shell.RunAsync(
            CreateProcess.FromCommandLine("echo", "test single argument")
            > Redirect.ToFile("test.txt")
            | CreateProcess.FromCommandLine("cat")
            > Redirect.
        );
        Assert.AreEqual("test single argument", File.ReadAllText("test.txt"));
    }
}
