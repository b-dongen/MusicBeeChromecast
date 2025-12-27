using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MusicBeePlugin;
using Sharpcaster.Models;

namespace MusicBeeChromecast.Tests;

[TestClass]
public class ChromecastPanelLoadTests
{
    [TestMethod]
    public void ChromecastPanel_Ctor_SetsDefaults()
    {
        RunSta(() =>
        {
            using (var panel = new ChromecastPanel(Color.Black))
            {
                Assert.IsNotNull(panel);
                Assert.IsNull(panel.ChromecastClient);
                Assert.IsFalse(panel.Disconnect);
            }
        });
    }

    [TestMethod]
    public void ChromecastPanel_PublicProperty_Disconnect_CanBeSet()
    {
        RunSta(() =>
        {
            using (var panel = new ChromecastPanel(Color.Black))
            {
                panel.Disconnect = true;
                Assert.IsTrue(panel.Disconnect);
            }
        });
    }

    [TestMethod]
    public void ChromecastPanel_PublicProperty_ChromecastClient_CanBeSet()
    {
        RunSta(() =>
        {
            using (var panel = new ChromecastPanel(Color.Black))
            {
                panel.ChromecastClient = null;
                Assert.IsNull(panel.ChromecastClient);
            }
        });
    }

    [TestMethod]
    public void ChromecastPanel_Load_WhenNoDevices_AddsNoDevicesButton()
    {
        RunSta(() =>
        {
            using (var panel = new ChromecastPanel(Color.Black))
            {
                // This is what the Load handler does when discovery returns zero devices.
                panel.InitializeDeviceButtons(new List<ChromecastReceiver>());

                var buttons = GetDeviceButtons(panel);
                Assert.HasCount(1, buttons);
                Assert.AreEqual("No Devices", buttons[0].Text);
            }
        });
    }

    private static List<Button> GetDeviceButtons(ChromecastPanel panel)
    {
        var flow = panel.Controls.OfType<FlowLayoutPanel>().First();
        return flow.Controls.OfType<Button>().ToList();
    }

    private static void RunSta(Action testBody)
    {
        Exception? ex = null;

        var thread = new Thread(() =>
        {
            try
            {
                testBody();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (ex != null)
        {
            throw new AssertFailedException("STA test failed", ex);
        }
    }
}
