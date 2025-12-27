using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sharpcaster;
using Sharpcaster.Models;

namespace MusicBeePlugin
{
    public partial class ChromecastPanel : Form
    {
        private Color backgroundColor { get; set; }

        public ChromecastClient ChromecastClient { get; set; } = null;
        public bool Disconnect { get; set; } = false;

        public ChromecastPanel(Color color)
        {
            backgroundColor = color;
            InitializeComponent();
        }

        private async void ChromecastPanel_Load(object sender, EventArgs e)
        {
            var contrastColor = ContrastColor(backgroundColor);
            this.BackColor = backgroundColor;
            this.closeText.ForeColor = contrastColor;
            this.devicesText.ForeColor = contrastColor;

            var locator = new ChromecastLocator();
            var receivers = (await locator.FindReceiversAsync(TimeSpan.FromSeconds(5)))?.ToList() ?? new List<ChromecastReceiver>();

            if (receivers.Count == 0)
            {
                Button b = new Button
                {
                    BackColor = Color.Transparent,
                    ForeColor = ContrastColor(backgroundColor),
                    FlatStyle = FlatStyle.Flat,
                    Text = "No Devices",
                    AutoSize = false,
                    Width = 200,
                };
                b.FlatAppearance.MouseOverBackColor = Color.Transparent;
                b.FlatAppearance.BorderColor = backgroundColor;
                flowLayoutPanel1.Controls.Add(b);
                return;
            }

            foreach (var device in receivers)
            {
                Button b = new Button
                {
                    BackColor = Color.Transparent,
                    ForeColor = ContrastColor(backgroundColor),
                    FlatStyle = FlatStyle.Flat,
                    Text = device.Name,
                    AutoSize = false,
                    Width = 200,
                };

                b.Click += new EventHandler((s, e2) => MyButtonHandler(s, e2, receivers));

                b.FlatAppearance.MouseOverBackColor = Color.Transparent;
                b.FlatAppearance.BorderColor = backgroundColor;

                flowLayoutPanel1.Controls.Add(b);
            }
        }

        private async void MyButtonHandler(object sender, EventArgs e, IList<ChromecastReceiver> devices)
        {
            var selectedName = (sender as Button)?.Text;
            if (string.IsNullOrWhiteSpace(selectedName))
            {
                return;
            }

            var device = devices.FirstOrDefault(d => string.Equals(d.Name, selectedName, StringComparison.Ordinal));
            if (device == null)
            {
                return;
            }

            var client = new ChromecastClient();
            await client.ConnectChromecast(device);

            // Default Media Receiver
            await client.LaunchApplicationAsync("CC1AD845");

            ChromecastClient = client;

            this.FormClosing -= ChromecastSelection_FormClosing;
            this.Close();
        }

        private void label1_Click(object sender, EventArgs e)
        {
            this.FormClosing -= ChromecastSelection_FormClosing;
            this.Close();
        }

        Color ContrastColor(Color color)
        {
            int d = 0;

            // Counting the perceptive luminance - human eye favors green color...
            double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;

            if (luminance > 0.5)
                d = 0; // bright colors - black font
            else
                d = 255; // dark colors - white font

            return Color.FromArgb(d, d, d);
        }

        private void ChromecastSelection_FormClosing(object sender, FormClosingEventArgs e)
        {
            this.FormClosing -= ChromecastSelection_FormClosing;
            this.Close();
        }
    }
}
