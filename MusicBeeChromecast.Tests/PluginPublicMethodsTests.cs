using MusicBeePlugin;
using System.Reflection;

namespace MusicBeeChromecast.Tests;

[TestClass]
public class PluginPublicMethodsTests
{
    [TestMethod]
    public void AttatchChromecastHandlers_WhenNoClient_DoesNotThrow()
    {
        var plugin = new Plugin();
        var result = plugin.AttatchChromecastHandlers();
        Assert.IsTrue(result);
    }

    [TestMethod]
    public void GetLocalIP_DoesNotThrow_ReturnsString()
    {
        var plugin = new Plugin();
        var ip = plugin.GetLocalIP();
        Assert.IsNotNull(ip);
    }

    [TestMethod]
    public void DisconnectFromChromecast_WhenNotConnected_DoesNotThrow()
    {
        var plugin = new Plugin();
        plugin.DisconnectFromChromecast(null, EventArgs.Empty, userCalled: true);
    }

    [TestMethod]
    public void ChromecastDisconnect_WhenNotConnected_DoesNotThrow()
    {
        var plugin = new Plugin();
        plugin.ChromecastDisconnect(null, EventArgs.Empty);
    }

    [TestMethod]
    public void StopChromecast_WhenNotConnected_DoesNotThrow()
    {
        var plugin = new Plugin();
        plugin.StopChromecast();
    }

    [TestMethod]
    public void ReceiveNotification_WhenNoClient_DoesNotThrow()
    {
        var plugin = new Plugin();
        plugin.ReceiveNotification("", Plugin.NotificationType.PlayStateChanged);
    }

    [TestMethod]
    public void Close_WhenNotInitialised_DoesNotThrow()
    {
        var plugin = new Plugin();
        plugin.Close(Plugin.PluginCloseReason.UserDisabled);
    }

    [TestMethod]
    public void Uninstall_DoesNotThrow()
    {
        var plugin = new Plugin();
        plugin.Uninstall();
    }

    [TestMethod]
    public void Configure_WhenNoPanelHandle_Throws()
    {
        var plugin = new Plugin();
        AssertThrows<NullReferenceException>(() => plugin.Configure(IntPtr.Zero));
    }

    [TestMethod]
    public void PauseIfPlaying_WhenNotInitialised_DoesNotThrow()
    {
        var plugin = new Plugin();
        plugin.PauseIfPlaying();
    }

    [TestMethod]
    public void StopIfPlaying_WhenNotInitialised_DoesNotThrow()
    {
        var plugin = new Plugin();
        plugin.StopIfPlaying();
    }

    [TestMethod]
    public void NextFileURL_WhenNotInitialised_Throws()
    {
        var plugin = new Plugin();
        AssertThrows<NullReferenceException>(() => plugin.NextFileURL());
    }

    [TestMethod]
    public void CalculateHash_SetsExpectedFields_WhenInitializedViaReflection()
    {
        var plugin = new Plugin();

        var songHashType = plugin.GetType().Assembly.GetType("MusicBeePlugin.SongHash", throwOnError: true);
        var songHash = Activator.CreateInstance(songHashType);
        SetPrivateField(plugin, "songHash", songHash);

        plugin.CalculateHash("abc", 1).GetAwaiter().GetResult();

        var current = (string)songHashType.GetProperty("Current").GetValue(songHash);
        Assert.IsFalse(string.IsNullOrWhiteSpace(current));
    }

    [TestMethod]
    public void EmptyDirectory_DoesNotThrow_WhenDirectoryMissing()
    {
        var plugin = new Plugin();
        plugin.EmptyDirectory().GetAwaiter().GetResult();
    }

    [TestMethod]
    public void DeleteFiles_DoesNotThrow_WhenDirectoryMissing()
    {
        var plugin = new Plugin();
        plugin.DeleteFiles("does-not-exist");
    }

    [TestMethod]
    public void DeleteOld_DoesNotThrow_WhenInitializedViaReflection()
    {
        var plugin = new Plugin();

        var iterableStackType = plugin.GetType().Assembly.GetType("MusicBeePlugin.IterableStack`1", throwOnError: true)!
            .MakeGenericType(typeof(string));
        var stack = Activator.CreateInstance(iterableStackType);

        var songHashType = plugin.GetType().Assembly.GetType("MusicBeePlugin.SongHash", throwOnError: true);
        var songHash = Activator.CreateInstance(songHashType);
        songHashType.GetProperty("Current")!.SetValue(songHash, "keep");

        SetPrivateField(plugin, "filenameStack", stack);
        SetPrivateField(plugin, "songHash", songHash);

        iterableStackType.GetMethod("Push")!.Invoke(stack, new object[] { "keep" });
        iterableStackType.GetMethod("Push")!.Invoke(stack, new object[] { "delete" });

        plugin.DeleteOld().GetAwaiter().GetResult();
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            Assert.Fail("Expected exception was not thrown: " + typeof(TException).FullName);
        }
        catch (TException)
        {
        }
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var f = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(f, $"Field '{fieldName}' not found");
        f.SetValue(instance, value);
    }
}
