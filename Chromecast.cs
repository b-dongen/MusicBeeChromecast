using FlacLibSharp;
using Nito.AsyncEx.Synchronous;
using Sharpcaster;
using Sharpcaster.Models.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Web;
using System.Windows.Forms;
using System.Xml;

namespace MusicBeePlugin
{
    internal sealed class SongHash
    {
        public string Previous { get; set; }
        public string Current { get; set; }
        public string Next { get; set; }

        public void NewCurrent()
        {
            Current = Next;
            Next = null;
        }
    }

    internal sealed class IterableStack<T> : IEnumerable<T>
    {
        private readonly List<T> _items = new List<T>();

        public int Count()
        {
            return _items.Count;
        }

        public void Push(T item)
        {
            _items.Add(item);
        }

        public T ElementAt(int index)
        {
            return _items[index];
        }

        public void Remove(int index)
        {
            _items.RemoveAt(index);
        }

        public IEnumerator<T> GetEnumerator()
        {
            return _items.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }

    public partial class Plugin
    {
        #region WebServer Variables

        private int? WebserverPort;
        private WebServer mediaWebServer;
        string mediaContentURL = null;

        #endregion WebServer Variables

        #region SharpCaster Chromecast Variables

        private ChromecastClient chromecastClient = null;

        #endregion SharpCaster Chromecast Variables

        #region Musicbee API Variables

        private MusicBeeApiInterface mbApiInterface;
        private PluginInfo about = new PluginInfo();

        #endregion Musicbee API Variables

        #region Misc Variables

        System.Timers.Timer fileDeletionTimer;
        System.Timers.Timer progressTimer;
        IterableStack<string> filenameStack;
        SongHash songHash;
        bool natural = false;
        private int _lastMbIndex = -1;

        #endregion Misc Variables

        #region Musicbee API Methods

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mbApiInterface = new MusicBeeApiInterface();
            mbApiInterface.Initialise(apiInterfacePtr);
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "MusicBee Chromecast";
            about.Description = "Adds casting functionality to MusicBee";
            about.Author = "Troy Fernandes";
            about.TargetApplication = "";   // current only applies to artwork, lyrics or instant messenger name that appears in the provider drop down selector or target Instant Messenger
            about.Type = PluginType.General;
            about.VersionMajor = 2;  // your plugin version
            about.VersionMinor = 0;
            about.Revision = 1;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = (ReceiveNotificationFlags.PlayerEvents | ReceiveNotificationFlags.TagEvents);
            about.ConfigurationPanelHeight = 25;   // height in pixels that musicbee should reserve in a panel for config settings. When set, a handle to an empty panel will be passed to the Configure function

            mbApiInterface.MB_RegisterCommand("Chromecast", OnChromecastSelection);
            

            ToolStripMenuItem mainMenuItem = (ToolStripMenuItem)mbApiInterface.MB_AddMenuItem("mnuTools/MB Chromecast", null, null);

            mainMenuItem.DropDown.Items.Add("Check Status", null, ShowStatusInMessagebox);
            mainMenuItem.DropDown.Items.Add("Disconnect from Chromecast", null, (sender, e) => DisconnectFromChromecast(sender, e, false));
            ReadSettings();

            _ = EmptyDirectory();

            fileDeletionTimer = new System.Timers.Timer();
            fileDeletionTimer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
            fileDeletionTimer.Interval = 10000;

            filenameStack = new IterableStack<string>();

            songHash = new SongHash();

            progressTimer = new System.Timers.Timer();
            progressTimer.Elapsed += new ElapsedEventHandler(DoSomething);

            _lastMbIndex = -1;

            return about;
        }

        public bool Configure(IntPtr panelHandle)
        {
            // save any persistent settings in a sub-folder of this path
            string dataPath = mbApiInterface.Setting_GetPersistentStoragePath();
            // panelHandle will only be set if you set about.ConfigurationPanelHeight to a non-zero value
            // keep in mind the panel width is scaled according to the font the user has selected
            // if about.ConfigurationPanelHeight is set to 0, you can display your own popup window
            if (panelHandle != IntPtr.Zero)
            {
                Panel configPanel = (Panel)Control.FromHandle(panelHandle);
                Button prompt = new Button
                {
                    AutoSize = true,
                    Location = new Point(0, 0),
                    Text = "Settings"
                };
                prompt.Click += ShowSettings;
                configPanel.Controls.AddRange(new Control[] { prompt });
            }
            return false;
        }

        // MusicBee is closing the plugin (plugin is being disabled by user or MusicBee is shutting down)
        public void Close(PluginCloseReason reason)
        {
            StopChromecast();
            StopWebserver();

            EmptyDirectory().WaitWithoutException();

            RevertSettings();


        }

        public void Uninstall()
        {

        }

        private int _suppressMbNotifications;

        private bool TrySuppressMbNotifications()
        {
            return Interlocked.Exchange(ref _suppressMbNotifications, 1) == 0;
        }

        private void ReleaseSuppressMbNotifications()
        {
            Interlocked.Exchange(ref _suppressMbNotifications, 0);
        }

        private int _lastKnownQueueItemId;

        private void HandleQueueAdvanceFromChromecast(MediaStatus status)
        {
            try
            {
                var itemId = status?.CurrentItemId ?? 0;
                if (itemId == 0)
                    return;

                if (_lastKnownQueueItemId == 0)
                {
                    _lastKnownQueueItemId = itemId;
                    return;
                }

                if (itemId == _lastKnownQueueItemId)
                    return;

                // Best-effort mapping: if Cast advanced to a new queue item, advance MusicBee.
                // If it moved backwards (unlikely without explicit prev), go previous.
                if (itemId > _lastKnownQueueItemId)
                {
                    mbApiInterface.Player_PlayNextTrack();
                }
                else
                {
                    mbApiInterface.Player_PlayPreviousTrack();
                }

                _lastKnownQueueItemId = itemId;
            }
            catch
            {
            }
        }

        private void SynchronizeFromChromecast(MediaStatus status)
        {
            if (status == null)
                return;

            if (!TrySuppressMbNotifications())
                return;

            try
            {
                // Detect queue item change (Next/Prev) and reflect to MusicBee
                HandleQueueAdvanceFromChromecast(status);

                // Position
                if (status.CurrentTime >= 0)
                {
                    mbApiInterface.Player_SetPosition((int)(status.CurrentTime * 1000));
                }

                // Play state
                var musicbeePlayerState = mbApiInterface.Player_GetPlayState();
                var ccState = status.PlayerState.ToString();

                if (string.Equals(ccState, "PAUSED", StringComparison.OrdinalIgnoreCase) && musicbeePlayerState == PlayState.Playing)
                {
                    mbApiInterface.Player_PlayPause();
                }
                else if (string.Equals(ccState, "PLAYING", StringComparison.OrdinalIgnoreCase) && musicbeePlayerState == PlayState.Paused)
                {
                    mbApiInterface.Player_PlayPause();
                }
            }
            catch
            {
            }
            finally
            {
                Task.Delay(150).ContinueWith(_ => ReleaseSuppressMbNotifications());
            }
        }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            if (Interlocked.CompareExchange(ref _suppressMbNotifications, 0, 0) != 0)
            {
                return;
            }

            if (chromecastClient != null)
            {
                switch (type)
                {
                    case NotificationType.PlayStateChanged:
                        // SharpCaster v3 doesn't expose a nullable Status property on MediaChannel like GoogleCast did.
                        if (chromecastClient.MediaChannel != null)
                        {
                            switch (mbApiInterface.Player_GetPlayState())
                            {
                                case PlayState.Paused:
                                    chromecastClient.MediaChannel.PauseAsync().WaitWithoutException();
                                    break;

                                case PlayState.Playing:
                                    chromecastClient.MediaChannel.PlayAsync().WaitWithoutException();
                                    break;
                            }
                        }
                        break;

                    case NotificationType.NowPlayingListChanged:
                        break;

                    case NotificationType.PluginStartup:
                        break;

                    case NotificationType.VolumeLevelChanged:
                        break;

                    case NotificationType.TrackChanged:
                        if (!PrerequisitesMet())
                        {
                            return;
                        }

                        fileDeletionTimer.Enabled = false;
                        fileDeletionTimer.Enabled = true;

                        var currIndex = -1;
                        try
                        {
                            currIndex = mbApiInterface.NowPlayingList_GetCurrentIndex();
                        }
                        catch
                        {
                        }

                        // If we are maintaining a Chromecast queue, drive it from MusicBee track navigation.
                        if (chromecastClient.MediaChannel != null && _lastKnownQueueItemId != 0)
                        {
                            try
                            {
                                if (_lastMbIndex >= 0 && currIndex >= 0)
                                {
                                    if (currIndex > _lastMbIndex)
                                    {
                                        chromecastClient.MediaChannel.QueueNextAsync().WaitWithoutException();
                                    }
                                    else if (currIndex < _lastMbIndex)
                                    {
                                        chromecastClient.MediaChannel.QueuePrevAsync().WaitWithoutException();
                                    }
                                }

                                // Refresh last known queue item id from the channel status.
                                var ccStatus = chromecastClient.MediaChannel.MediaStatus;
                                _lastKnownQueueItemId = ccStatus?.CurrentItemId ?? _lastKnownQueueItemId;

                                _lastMbIndex = currIndex;

                                // Skip re-loading media when queue is active.
                                break;
                            }
                            catch
                            {
                                // fall through to LoadSong if queue navigation fails
                            }
                        }

                        _lastMbIndex = currIndex;

                        CalculateHash(mbApiInterface.NowPlaying_GetFileUrl(), 1).WaitWithoutException();

                        var info = CopySong(sourceFileUrl, songHash.Current).WaitAndUnwrapException();
                        _ = LoadSong(info.Item1, info.Item2);

                        break;
                }
            }
        }

        #endregion Musicbee API Methods

        #region User Saved Settings

        //Read the settings file
        private void ReadSettings()
        {
            var fullFilePath = @mbApiInterface.Setting_GetPersistentStoragePath() + @"\MB_Chromecast_Settings.xml";
            if (File.Exists(fullFilePath))
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(fullFilePath);
                var temp = doc.GetElementsByTagName("server_port")[0].InnerText;
                if (!string.IsNullOrEmpty(temp))
                {
                    WebserverPort = Convert.ToInt16(temp);
                }
            }
        }

        //Fired when the user clicks Apply/Save in the preferences panel
        public void SaveSettings()
        {
            ReadSettings();
        }

        //Show the Settings Form
        private void ShowSettings(object sender, EventArgs e)
        {
            using (var settingsForm = new Settings(mbApiInterface.Setting_GetPersistentStoragePath()))
            {
                settingsForm.ShowDialog();
            }
        }

        #endregion User Saved Settings

        #region MB Chromecast UI Elements

        #endregion MB Chromecast UI Elements

        #region Core Methods

        protected void OnChromecastSelection(object sender, EventArgs e)
        {
            //If the webserver started with no issues
            try
            {
                //If there's already an active connection
                if (chromecastClient != null)
                {
                    MessageBox.Show("There is already an active connection to a device. Please Disconnect then try again");
                    return;
                }


            }
            catch (Exception ex)
            {
                MessageBox.Show("The webserver could not be started. Cancelling\n Error: " + ex.Message);
                return;
            }

            using (var cs = new ChromecastPanel(
                Color.FromArgb(mbApiInterface.Setting_GetSkinElementColour(SkinElement.SkinInputPanel, ElementState.ElementStateDefault, ElementComponent.ComponentBackground))))
            {
                try
                {
                    cs.StartPosition = FormStartPosition.CenterParent;
                    cs.ShowDialog();

                    chromecastClient = cs.ChromecastClient;
                    if (chromecastClient == null)
                    {
                        RevertSettings();
                        return;
                    }

                    //Change some musicbee settings
                    PauseIfPlaying();
                    ChangeSettings();

                    AttatchChromecastHandlers();

                    //If the webserver started with an issue 
                    if (StartWebserver() == -1)
                    {
                        return;
                    }
                }
                catch (NullReferenceException ex)
                {
                    MessageBox.Show(ex.Message);
                }
            }
        }

        public void ChromecastDisconnect(object sender, EventArgs e)
        {
            Debug.WriteLine("Disconnected from chromecast");
            chromecastClient = null;
            StopIfPlaying();
            StopWebserver();
            RevertSettings();
        }

        public void DisconnectFromChromecast(object sender, EventArgs e, bool userCalled)
        {
            try
            {
                StopChromecast();
            }
            catch (NullReferenceException)
            {
            }

        }

        #endregion Core Methods

        #region WebServer 

        private int StartWebserver()
        {
            //If theres a web server already running, then theres no need to start a new one
            if (mediaWebServer != null)
            {
                return 1;
            }
            try
            {
                //Create the web server
                mediaWebServer = new WebServer(WebserverPort);
                //Save the web server url
                mediaContentURL = "http://" + GetLocalIP() + ":" + WebserverPort + "/";
                return 0;
            }
            catch (Exception e)
            {
                MessageBox.Show("Error starting the webserver. \n " + e.Message);
                return -1;
            }

        }

        private void StopWebserver(object sender = null, EventArgs e = null)
        {
            try
            {

                if (mediaWebServer == null)
                {
                    return;
                }
                mediaWebServer.Stop();
                mediaWebServer = null;
            }
            catch (NullReferenceException)
            {
                //Nothing to do since there was no web server initialized in the first place
            }
        }

        #endregion WebServer

        #region MB Settings

        private void ChangeSettings()
        {
            //Settings need to be changed because they might change how the player interacts with chromecast.
            //These settings get reverted back to their original settings after
            mbApiInterface.Player_SetMute(true);

        }

        private void RevertSettings()
        {
            try
            {
                if (mbApiInterface.Player_GetMute())
                {
                    mbApiInterface.Player_SetMute(false);
                }
            }
            catch
            {
            }
        }

        #endregion MB Settings

        #region Helper Functions

        public bool AttatchChromecastHandlers()
        {
            if (chromecastClient?.MediaChannel != null)
            {
                chromecastClient.MediaChannel.StatusChanged += (s, status) => SynchronizeFromChromecast(status);
            }

            if (chromecastClient != null)
            {
                chromecastClient.Disconnected += ChromecastDisconnect;
            }

            return true;
        }

        public string GetLocalIP()
        {
            UnicastIPAddressInformation mostSuitableIp = null;

            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var network in networkInterfaces)
            {
                if (network.OperationalStatus != OperationalStatus.Up)
                    continue;

                var properties = network.GetIPProperties();

                if (properties.GatewayAddresses.Count == 0)
                    continue;

                foreach (var address in properties.UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    if (IPAddress.IsLoopback(address.Address))
                        continue;

                    if (!address.IsDnsEligible)
                    {
                        if (mostSuitableIp == null)
                            mostSuitableIp = address;
                        continue;
                    }

                    // The best IP is the IP got from DHCP server
                    if (address.PrefixOrigin != PrefixOrigin.Dhcp)
                    {
                        if (mostSuitableIp == null || !mostSuitableIp.IsDnsEligible)
                            mostSuitableIp = address;
                        continue;
                    }

                    return address.Address.ToString();
                }
            }

            return mostSuitableIp != null
                ? mostSuitableIp.Address.ToString()
                : "";
        }


        private bool PrerequisitesMet()
        {
            //The mediaChannel must not be null
            //The server must be running
            return chromecastClient != null && mediaWebServer != null;
        }

        public void UserClosingPlugin(object sender, EventArgs e)
        {
            Close(PluginCloseReason.UserDisabled);
        }

        public void PauseIfPlaying()
        {
            try
            {
                if (mbApiInterface.Player_GetPlayState() == PlayState.Playing)
                {
                    mbApiInterface.Player_PlayPause();
                }
            }
            catch
            {
            }
        }

        public void StopIfPlaying()
        {
            try
            {
                if (mbApiInterface.Player_GetPlayState() == PlayState.Playing)
                {
                    mbApiInterface.Player_Stop();
                }
            }
            catch
            {
            }
        }

        private void ShowStatusInMessagebox(object sender, EventArgs e)
        {
            StringBuilder status = new StringBuilder();
            if (chromecastClient != null)
            {
                status.Append("Chromecast: Connected\n");
            }
            else
            {
                status.Append("Chromecast: Not Connected\n");
            }


            if (mediaWebServer != null)
            {
                status.Append("Server Status: Running @ " + mediaContentURL + "\n");
            }
            else
            {
                status.Append("Server Status: Not Running\n");
            }
            MessageBox.Show(status.ToString());
        }

        #endregion Helper Functions


        public void DeleteFiles(string hashed)
        {
            try
            {
                System.IO.DirectoryInfo di = new DirectoryInfo(@System.IO.Path.GetTempPath() + @"\\MusicBeeChromecast");

                foreach (FileInfo file in di.GetFiles())
                {

                    if (Path.GetFileNameWithoutExtension(file.Name) == hashed)
                    {
                        file.Delete();
                    }
                }

            }
            catch (System.IO.IOException)
            {

            }

        }

        public async Task<Tuple<string, string, string>> CopySong(string songFile, string hashed)
        {

            string songFileExt = Path.GetExtension(songFile);
            File.Copy(songFile, @System.IO.Path.GetTempPath() + @"\\MusicBeeChromecast\" + hashed + songFileExt, true);

            string imageFile = mbApiInterface.Library_GetArtworkUrl(songFile, 0);

            string imageFileExt = ".jpg";

            if (imageFile != null)
            {
                File.Copy(imageFile, @System.IO.Path.GetTempPath() + @"\\MusicBeeChromecast\" + hashed + imageFileExt, true);
            }

            return Tuple.Create(hashed, songFileExt, imageFileExt);
        }


        public async Task LoadSong(string hashed, string songFileExt)
        {
            string filetype = mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.Kind).Replace(" audio file", "");
            string samplerate = mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.SampleRate);
            string bitrate = mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.Bitrate);
            string channels = mbApiInterface.NowPlaying_GetFileProperty(FilePropertyType.Channels);
            string properties = "";
            string nextSong = mbApiInterface.NowPlayingList_GetFileTag(mbApiInterface.NowPlayingList_GetNextIndex(1), MetaDataType.TrackTitle)
                + " by " + mbApiInterface.NowPlayingList_GetFileTag(mbApiInterface.NowPlayingList_GetNextIndex(1), MetaDataType.Artist);
            nextSong = nextSong == " by " || nextSong == null ? "End of List" : nextSong;

            if (filetype == "FLAC")
            {
                using (FlacFile file = new FlacFile(mbApiInterface.NowPlaying_GetFileUrl()))
                {
                    properties = filetype + " " + file.StreamInfo.BitsPerSample.ToString() + " bit, " + samplerate + ", " + bitrate + ", " + channels;
                }
            }
            else
            {
                properties = filetype + " " + samplerate + ", " + bitrate + ", " + channels;
            }

            string[] temp = null;
            mbApiInterface.NowPlayingList_QueryFilesEx("", ref temp);
            int size = temp.Count();

            try
            {
                var media = new Media
                {
                    ContentUrl = HttpUtility.UrlPathEncode(mediaContentURL + hashed + songFileExt),
                    ContentType = GetContentTypeFromExtension(songFileExt),
                    StreamType = StreamType.Buffered,
                    Metadata = new MusicTrackMetadata
                    {
                        Artist = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Artist),
                        Title = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.TrackTitle),
                        AlbumName = mbApiInterface.NowPlaying_GetFileTag(MetaDataType.Album)
                    }
                };

                await chromecastClient.MediaChannel.LoadAsync(media);

                filenameStack.Push(hashed.ToString());
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Requested to close");
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }
        }

        private static string GetContentTypeFromExtension(string ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return "audio/mpeg";
            ext = ext.StartsWith(".") ? ext.ToLowerInvariant() : ("." + ext.ToLowerInvariant());
            switch (ext)
            {
                case ".mp3": return "audio/mpeg";
                case ".flac": return "audio/flac";
                case ".wav": return "audio/wav";
                case ".m4a": return "audio/mp4";
                case ".aac": return "audio/aac";
                case ".ogg": return "audio/ogg";
                default: return "application/octet-stream";
            }
        }

        public void StopChromecast()
        {
            if (chromecastClient != null)
            {
                try
                {
                    // SharpCaster v3 exposes Disconnected event; connection teardown is handled internally.
                    // Dispose when available.
                    (chromecastClient as IDisposable)?.Dispose();
                }
                catch
                {
                }
            }

            ChromecastDisconnect(null, null);
        }

        private void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            DeleteOld().WaitWithoutException();
            source.GetType().GetProperty("Enabled").SetValue(source, false, null);
        }

        private void DoSomething(object sender, EventArgs e)
        {
            try
            {
                var index = mbApiInterface.NowPlayingList_GetCurrentIndex();
                ProcessNextAndQueue(index).WaitWithoutException();
            }
            catch
            {
            }
            finally
            {
                progressTimer.Enabled = false;
            }
        }

        public async Task CalculateHash(string songName, int which)
        {
            switch (which)
            {
                case 0:
                    songHash.Previous = Math.Abs(songName.GetHashCode()).ToString();
                    return;
                case 1:
                    songHash.Current = Math.Abs(songName.GetHashCode()).ToString();
                    return;
                case 2:
                    songHash.Next = Math.Abs(songName.GetHashCode()).ToString();
                    return;
            }
        }

        public async Task DeleteOld()
        {
            if (filenameStack.Count() > 1)
            {
                for (int i = 0; i < filenameStack.Count(); i++)
                {
                    var element = filenameStack.ElementAt(i);
                    if (element != songHash.Current)
                    {
                        DeleteFiles(element.ToString());
                        filenameStack.Remove(i);
                    }
                }
            }
        }

        public async Task EmptyDirectory()
        {
            try
            {
                var dirPath = Path.Combine(Path.GetTempPath(), "MusicBeeChromecast");
                if (!Directory.Exists(dirPath))
                    return;

                DirectoryInfo di = new DirectoryInfo(dirPath);
                foreach (FileInfo file in di.GetFiles())
                {
                    file.Delete();
                }
            }
            catch (IOException)
            {
            }
        }

        internal async Task LoadSongs(List<SongInfo> songs)
        {
            if (chromecastClient?.MediaChannel == null || songs == null || songs.Count == 0)
                return;

            try
            {
                // Ensure local files are copied and build queue items
                var items = new Sharpcaster.Models.Queue.QueueItem[songs.Count];

                for (int i = 0; i < songs.Count; i++)
                {
                    string[] res = null;
                    mbApiInterface.Library_GetFileTags(songs[i].FileURL, new[] { MetaDataType.Artist, MetaDataType.TrackTitle, MetaDataType.Album }, ref res);

                    await CopySong(songs[i].FileURL, songs[i].Hashed);

                    var media = new Media
                    {
                        ContentUrl = HttpUtility.UrlPathEncode(mediaContentURL + songs[i].Hashed + songs[i].SongFileExt),
                        ContentType = GetContentTypeFromExtension(songs[i].SongFileExt),
                        StreamType = StreamType.Buffered,
                        Metadata = new MusicTrackMetadata
                        {
                            Artist = res != null && res.Length > 0 ? res[0] : null,
                            Title = res != null && res.Length > 1 ? res[1] : null,
                            AlbumName = res != null && res.Length > 2 ? res[2] : null
                        }
                    };

                    items[i] = new Sharpcaster.Models.Queue.QueueItem
                    {
                        Media = media,
                        IsAutoPlay = true
                    };

                    filenameStack.Push(songs[i].Hashed);
                }

                // Start from the first item
                var status = await chromecastClient.MediaChannel.QueueLoadAsync(items, RepeatModeType.OFF, 0);
                _lastKnownQueueItemId = status?.CurrentItemId ?? 0;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Requested to close");
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
            }
        }

        public async Task QueueItem(string hashedName, string songFileExt, int duration, string artist, string title, string album)
        {
            if (chromecastClient?.MediaChannel == null)
                return;

            var item = new Sharpcaster.Models.Queue.QueueItem
            {
                Media = new Media
                {
                    ContentUrl = HttpUtility.UrlPathEncode(mediaContentURL + hashedName + songFileExt),
                    ContentType = GetContentTypeFromExtension(songFileExt),
                    StreamType = StreamType.Buffered,
                    Metadata = new MusicTrackMetadata
                    {
                        Artist = artist,
                        Title = title,
                        AlbumName = album
                    }
                },
                IsAutoPlay = true
            };

            await chromecastClient.MediaChannel.QueueInsertAsync(new[] { item }, null);
        }

        public async Task ProcessNextAndQueue(int currentPos)
        {
            if (chromecastClient?.MediaChannel == null)
                return;

            var nextFileUrl = mbApiInterface.NowPlayingList_GetListFileUrl(currentPos + 1);
            if (string.IsNullOrWhiteSpace(nextFileUrl) || !File.Exists(nextFileUrl))
                return;

            await CalculateHash(nextFileUrl, 2);

            string[] res = null;
            mbApiInterface.Library_GetFileTags(nextFileUrl, new[] { MetaDataType.Artist, MetaDataType.TrackTitle, MetaDataType.Album }, ref res);

            await CopySong(nextFileUrl, songHash.Next);

            await QueueItem(
                songHash.Next,
                Path.GetExtension(nextFileUrl),
                0,
                res != null && res.Length > 0 ? res[0] : null,
                res != null && res.Length > 1 ? res[1] : null,
                res != null && res.Length > 2 ? res[2] : null);

            filenameStack.Push(songHash.Next);
            natural = true;
        }

        public string NextFileURL()
        {
            var nowPlayingIndex = mbApiInterface.NowPlayingList_GetCurrentIndex();
            return mbApiInterface.NowPlayingList_GetListFileUrl(nowPlayingIndex + 1);
        }
    }

    internal sealed class SongInfo
    {
        public string FileURL { get; set; }
        public string Hashed { get; set; }
        public string SongFileExt { get; set; }
    }
}