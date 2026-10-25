using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CMZDedicatedSteamServer
{
    internal sealed class ServerGuiForm : Form
    {
        // ---- palette ----
        private static readonly Color C_Bg        = Color.FromArgb(14, 19, 32);
        private static readonly Color C_Header    = Color.FromArgb(18, 25, 41);
        private static readonly Color C_TabStrip  = Color.FromArgb(16, 22, 36);
        private static readonly Color C_Card      = Color.FromArgb(20, 27, 43);
        private static readonly Color C_Surface   = Color.FromArgb(11, 16, 27);
        private static readonly Color C_Row       = Color.FromArgb(24, 32, 50);
        private static readonly Color C_RowDeep   = Color.FromArgb(19, 26, 41);
        private static readonly Color C_Border    = Color.FromArgb(34, 45, 70);
        private static readonly Color C_Text      = Color.FromArgb(232, 238, 255);
        private static readonly Color C_TextDim   = Color.FromArgb(132, 146, 173);
        private static readonly Color C_TextFaint = Color.FromArgb(96, 110, 136);
        private static readonly Color C_ChromeOff = Color.FromArgb(40, 52, 78);
        private static readonly Color C_ChromeOffHot = Color.FromArgb(50, 64, 94);
        private static readonly Color C_Green     = Color.FromArgb(72, 199, 142);
        private static readonly Color C_Red       = Color.FromArgb(231, 76, 60);
        private static readonly Color C_Amber     = Color.FromArgb(240, 178, 80);
        private static readonly Color C_Idle      = Color.FromArgb(104, 116, 138);

        private Color _accent = Color.FromArgb(52, 152, 219);
        private Color _accentHot = Color.FromArgb(70, 170, 240);
        private Color _accentDim = Color.FromArgb(38, 70, 100);
        private Color _consoleText = Color.FromArgb(166, 226, 170);

        private readonly RichTextBox _output = new RichTextBox();
        private readonly Button _startButton = new Button();
        private readonly Button _stopButton = new Button();
        private readonly Button _restartButton = new Button();
        private readonly Button _terminalButton = new Button();
        private readonly Timer _restartCountdownTimer = new Timer();
        private readonly Timer _playersHeartbeatTimer = new Timer();

        private readonly Panel _statusDot = new Panel();
        private readonly Label _statusText = new Label();
        private readonly Label _playersChipText = new Label();
        private readonly Label _uptimeChipText = new Label();
        private readonly Label _tpsChipText = new Label();
        private readonly Label _memChipText = new Label();

        private readonly Panel _contentHost = new Panel();
        private readonly Panel _consolePage = new Panel();
        private readonly Panel _playersPage = new Panel();
        private readonly Panel _settingsPage = new Panel();
        private readonly Button _tabConsoleButton = new Button();
        private readonly Button _tabPlayersButton = new Button();
        private readonly Button _tabSettingsButton = new Button();
        private string _activeTab = "console";

        private readonly Panel _playersList = new Panel();
        private readonly Label _playersEmptyState = new Label();
        private readonly TextBox _playersFilterInput = new TextBox();
        private readonly Label _playersCountLabel = new Label();

        private readonly TableLayoutPanel _settingsServerGrid = new TableLayoutPanel();
        private readonly TableLayoutPanel _settingsGuiGrid = new TableLayoutPanel();
        private readonly Button _settingsServerTabButton = new Button();
        private readonly Button _settingsGuiTabButton = new Button();
        private readonly Panel _settingsServerCard = new Panel();
        private readonly Panel _settingsGuiCard = new Panel();
        private readonly Button _settingsSaveButton = new Button();
        private readonly Button _settingsReloadButton = new Button();
        private readonly FileSystemWatcher _propertiesWatcher = new FileSystemWatcher();
        private readonly Timer _propertiesReloadTimer = new Timer();
        private bool _settingsDirty;
        private bool _loadingSettings;

        private readonly ComboBox _appearanceAccentInput = new NoWheelComboBox();
        private readonly ComboBox _appearanceConsoleTextInput = new NoWheelComboBox();

        private sealed class SettingDescriptor
        {
            public string Label;
            public string Kind;      // "text" | "bool" | "enum"
            public string[] Options; // enum: "value|Display" pairs
        }

        private sealed class SettingRow
        {
            public string Key;
            public Control Editor;
        }

        private sealed class SettingsSection
        {
            public string Title;
            public readonly List<KeyValuePair<string, string>> Entries =
                new List<KeyValuePair<string, string>>();
        }

        private sealed class EnumOption
        {
            public string Value;
            public string Text;
            public override string ToString() => Text;
        }

        private readonly List<SettingRow> _settingsRows = new List<SettingRow>();
        private readonly List<SettingsSection> _settingsSections = new List<SettingsSection>();

        private static readonly Dictionary<string, SettingDescriptor> SettingDescriptors =
            new Dictionary<string, SettingDescriptor>(StringComparer.OrdinalIgnoreCase)
        {
            ["server-name"] = new SettingDescriptor { Label = "Server Name", Kind = "text" },
            ["server-message"] = new SettingDescriptor { Label = "Server Message", Kind = "text" },
            ["server-ip"] = new SettingDescriptor { Label = "Server IP", Kind = "text" },
            ["server-port"] = new SettingDescriptor { Label = "Server Port", Kind = "text" },
            ["max-players"] = new SettingDescriptor { Label = "Max Players", Kind = "text" },
            ["password"] = new SettingDescriptor { Label = "Password", Kind = "text" },
            ["game-path"] = new SettingDescriptor { Label = "Game Path", Kind = "text" },
            ["game-name"] = new SettingDescriptor { Label = "Game Name", Kind = "text" },
            ["network-version"] = new SettingDescriptor { Label = "Network Version", Kind = "text" },
            ["world-guid"] = new SettingDescriptor { Label = "World Guid", Kind = "text" },
            ["tick-rate-hz"] = new SettingDescriptor { Label = "Tick Rate", Kind = "text" },
            ["view-distance-chunks"] = new SettingDescriptor { Label = "View Distance", Kind = "text" },
            ["save-owner-steam-id"] = new SettingDescriptor { Label = "Save Owner Steam ID", Kind = "text" },
            ["steam-app-id"] = new SettingDescriptor { Label = "Steam App ID", Kind = "text" },
            ["steam-lobby-visible"] = new SettingDescriptor { Label = "Steam Lobby Visible", Kind = "bool" },
            ["steam-friends-only"] = new SettingDescriptor { Label = "Steam Friends Only", Kind = "bool" },
            ["steam-allow-minimal-updates"] = new SettingDescriptor { Label = "Allow Minimal Updates", Kind = "bool" },
            ["steam-account-required"] = new SettingDescriptor { Label = "Steam Account Required", Kind = "bool" },
            ["write-steam-appid-file"] = new SettingDescriptor { Label = "Write steam_appid.txt", Kind = "bool" },
            ["require-running-steam-client"] = new SettingDescriptor { Label = "Require Running Steam", Kind = "bool" },
            ["allow-client-time-sync"] = new SettingDescriptor { Label = "Allow Client Time Sync", Kind = "bool" },
            ["infinite-resource-mode"] = new SettingDescriptor { Label = "Infinite Resources", Kind = "bool" },
            ["log-command-audit"] = new SettingDescriptor { Label = "Log Command Audit", Kind = "bool" },
            ["log-network-packets"] = new SettingDescriptor { Label = "Log Network Packets", Kind = "bool" },
            ["log-host-messages"] = new SettingDescriptor { Label = "Log Host Messages", Kind = "bool" },
            ["log-combat"] = new SettingDescriptor { Label = "Log Combat Evidence", Kind = "bool" },
            ["log-combat-evidence"] = new SettingDescriptor { Label = "Log Combat Evidence", Kind = "bool" },
            ["game-mode"] = new SettingDescriptor
            {
                Label = "Game Mode", Kind = "enum",
                Options = new[] { "0|Endurance", "1|Survival", "2|Dragon Endurance", "3|Creative", "4|Exploration", "5|Scavenger" }
            },
            ["difficulty"] = new SettingDescriptor
            {
                Label = "Difficulty", Kind = "enum",
                Options = new[] { "0|Easy", "1|Hard", "2|No Enemies", "3|Hardcore" }
            },
            ["pvp-state"] = new SettingDescriptor
            {
                Label = "PVP State", Kind = "enum",
                Options = new[] { "0|Off", "1|Everyone", "2|Not Friends" }
            },
        };

        private readonly TextBox _commandInput = new TextBox();
        private readonly Button _sendCommandButton = new Button();
        private readonly Button _quickCommandsButton = new Button();
        private readonly Button _clearConsoleButton = new Button();
        private readonly TextBox _consoleSearchInput = new TextBox();
        private readonly Timer _consoleSearchDebounce = new Timer();
        private readonly Button _pauseScrollButton = new Button();
        private bool _scrollPaused;
        private readonly List<string> _commandHistory = new List<string>();
        private int _commandHistoryIndex = -1;
        private readonly Dictionary<string, DateTime> _connectTimesUtcBySteamId = new Dictionary<string, DateTime>();
        private readonly List<Label> _accentHeaderLabels = new List<Label>();
        private readonly TextBox _settingsFilterInput = new TextBox();
        private Panel _settingsFilterWrapper;
        private sealed class SettingsLayoutEntry
        {
            public bool IsHeader;
            public string Name;
            public Control[] Controls;
        }
        private readonly List<SettingsLayoutEntry> _serverSettingsLayout = new List<SettingsLayoutEntry>();
        private readonly Timer _consoleFlushTimer = new Timer();
        private readonly Label _toastBanner = new Label();
        private readonly Timer _toastTimer = new Timer();
        private readonly Timer _consoleAutoClearTimer = new Timer();
        private string _activeSettingsSection = "server";
        private string _currentTpsText = "--";

        private Process _serverProcess;
        private int _connectedPlayers;
        private int _maxPlayers = -1;
        private bool _serverReady;
        private bool _restartQueued;
        private bool _isStartInProgress;
        private bool _isStopping;
        private bool _stopRequestedByUser;
        private DateTime _serverRunningSinceUtc = DateTime.MinValue;
        private DateTime _lastStartAttemptUtc = DateTime.MinValue;
        private bool _startupPopupShown;
        private DateTime _restartUnlockUtc = DateTime.MinValue;
        private bool _hasSeenFirstReadyCycle;
        private int _lastRestartDelaySeconds = -1;
        private int _restartDelayStreak;
        private readonly Queue<string> _startupErrorLines = new Queue<string>();
        private readonly Queue<string> _recentErrorLines = new Queue<string>();
        private readonly ConcurrentQueue<string> _pendingConsoleLines = new ConcurrentQueue<string>();
        private readonly Dictionary<ulong, string> _pendingNamesBySteamId = new Dictionary<ulong, string>();
        private readonly Dictionary<byte, ulong> _steamIdsByGid = new Dictionary<byte, ulong>();
        private readonly Dictionary<string, string> _playerInfoBySteamId = new Dictionary<string, string>();
        private int _placeholderPlayerNumber;
        private const int LargePlayerListThreshold = 24;
        private const int StartupErrorLineLimit = 6;
        private const int RuntimeErrorLineLimit = 8;
        private const int ConsoleFlushBatchSize = 120;
        private const int ConsoleScrollEveryFlushes = 4;
        private const int ParserSafeLineLength = 4096;
        private const int DisplaySafeLineLength = 900;
        private const int MaxConsoleTextLength = 900000;
        private const int ConsoleTrimTargetLength = 650000;
        private const int ConsoleRenderPauseThreshold = 12000;
        private const int ConsoleAutoClearCountdownSeconds = 10;
        private const int ConsoleWarningLineThreshold = 11000;
        private const int MaxDisplayedConsoleLines = 13000;
        private const int ConsoleLineTrimTarget = 8500;
        private static readonly TimeSpan StartupFailureWindow = TimeSpan.FromSeconds(15);
        private static readonly Random RestartDelayRandom = new Random();
        private int _consoleAutoScrollFlushCounter;
        private int _displayedConsoleLineCount;
        private bool _consoleSafetyWarningShown;
        private bool _consoleRenderingPaused;
        private bool _consolePauseToastShown;
        private int _suppressedConsoleLineCount;
        private int _consoleAutoClearRemainingSeconds;

        private const uint MF_BYCOMMAND = 0x00000000;
        private const uint MF_ENABLED = 0x00000000;
        private const uint MF_GRAYED = 0x00000001;
        private const uint SC_CLOSE = 0xF060;
        private const int EM_SETCUEBANNER = 0x1501;
        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        private static extern bool EnableMenuItem(IntPtr hMenu, uint uIDEnableItem, uint uEnable);

        [DllImport("user32.dll")]
        private static extern bool DrawMenuBar(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool HideCaret(IntPtr hWnd);

        private sealed class NoWheelComboBox : ComboBox
        {
            private const int WM_MOUSEWHEEL = 0x020A;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_MOUSEWHEEL && !DroppedDown)
                {
                    Control parent = Parent;
                    while (parent != null)
                    {
                        if (parent is ScrollableControl scrollable && scrollable.VerticalScroll.Visible)
                        {
                            SendMessage(scrollable.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
                            return;
                        }
                        parent = parent.Parent;
                    }
                    return;
                }

                base.WndProc(ref m);
            }
        }

        private sealed class DarkMenuColorTable : ProfessionalColorTable
        {
            public override Color MenuItemSelected => Color.FromArgb(50, 64, 94);
            public override Color MenuItemBorder => Color.FromArgb(52, 152, 219);
            public override Color MenuBorder => Color.FromArgb(60, 74, 104);
            public override Color ToolStripDropDownBackground => Color.FromArgb(20, 27, 43);
            public override Color ImageMarginGradientBegin => Color.FromArgb(20, 27, 43);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(20, 27, 43);
            public override Color ImageMarginGradientEnd => Color.FromArgb(20, 27, 43);
            public override Color SeparatorDark => Color.FromArgb(60, 74, 104);
            public override Color SeparatorLight => Color.FromArgb(60, 74, 104);
            public override Color MenuItemPressedGradientBegin => Color.FromArgb(50, 64, 94);
            public override Color MenuItemPressedGradientEnd => Color.FromArgb(50, 64, 94);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(50, 64, 94);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(50, 64, 94);
        }

        private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkMenuRenderer() : base(new DarkMenuColorTable()) { }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled
                    ? Color.FromArgb(232, 238, 255)
                    : Color.FromArgb(96, 110, 136);
                base.OnRenderItemText(e);
            }
        }

        public ServerGuiForm()
        {
            Text = "CMZ Dedicated Steam Server";
            Width = 1040;
            Height = 680;
            MinimumSize = new Size(1060, 600);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = C_Bg;
            ForeColor = C_Text;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);

            // Stay invisible until the OnShown warm-up finishes — otherwise the
            // per-page pre-layout paints the half-built skeleton for a moment.
            Opacity = 0;

            LoadGuiSettings();

            // ---------- header ----------
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 54,
                BackColor = C_Header,
                Padding = new Padding(14, 10, 14, 10)
            };

            var titleLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "CastleMiner Z Server",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Font = new Font("Segoe UI Semibold", 11.5f, FontStyle.Bold),
                ForeColor = C_Text,
                BackColor = Color.Transparent
            };

            var buttonsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            ConfigureHeaderButton(_startButton, "Start", 78, _accent, (_, __) => StartServer());
            ConfigureHeaderButton(_stopButton, "Stop", 78, C_ChromeOff, (_, __) => StopServer());
            ConfigureHeaderButton(_restartButton, "Restart", 92, C_ChromeOff, (_, __) => SendCommandInternal("!restart"));
            ConfigureHeaderButton(_terminalButton, "Terminal", 88, C_ChromeOff, (_, __) => SwitchToTerminal());
            buttonsPanel.Controls.Add(_startButton);
            buttonsPanel.Controls.Add(_stopButton);
            buttonsPanel.Controls.Add(_restartButton);
            buttonsPanel.Controls.Add(_terminalButton);

            var chipsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            Panel statusChip = CreateChip();
            _statusDot.Width = 8;
            _statusDot.Height = 8;
            _statusDot.BackColor = C_Idle;
            _statusDot.Margin = new Padding(10, 0, 6, 0);
            _statusDot.Anchor = AnchorStyles.None;
            var dotHost = new Panel { Width = 22, Dock = DockStyle.Left, BackColor = Color.Transparent };
            _statusDot.Location = new Point(7, 11);
            dotHost.Controls.Add(_statusDot);
            _statusText.Dock = DockStyle.Fill;
            _statusText.TextAlign = ContentAlignment.MiddleLeft;
            _statusText.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            _statusText.ForeColor = C_Text;
            _statusText.BackColor = Color.Transparent;
            _statusText.Text = "Stopped";
            statusChip.Controls.Add(_statusText);
            statusChip.Controls.Add(dotHost);
            statusChip.Width = 96;

            Panel playersChip = CreateChip();
            ConfigureChipLabel(_playersChipText, "Players  0/?");
            playersChip.Controls.Add(_playersChipText);
            playersChip.Width = 104;

            Panel uptimeChip = CreateChip();
            ConfigureChipLabel(_uptimeChipText, "Uptime  --:--:--");
            uptimeChip.Controls.Add(_uptimeChipText);
            uptimeChip.Width = 132;

            Panel tpsChip = CreateChip();
            ConfigureChipLabel(_tpsChipText, "TPS  --");
            tpsChip.Controls.Add(_tpsChipText);
            tpsChip.Width = 86;

            Panel memChip = CreateChip();
            ConfigureChipLabel(_memChipText, "MEM  --");
            memChip.Controls.Add(_memChipText);
            memChip.Width = 100;

            chipsPanel.Controls.Add(statusChip);
            chipsPanel.Controls.Add(playersChip);
            chipsPanel.Controls.Add(uptimeChip);
            chipsPanel.Controls.Add(tpsChip);
            chipsPanel.Controls.Add(memChip);

            var headerLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var titleCell = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0), Width = 178 };
            titleLabel.Dock = DockStyle.Fill;
            titleCell.Controls.Add(titleLabel);

            var chipsCell = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            chipsPanel.Dock = DockStyle.Right;
            chipsCell.Controls.Add(chipsPanel);

            headerLayout.Controls.Add(titleCell, 0, 0);
            headerLayout.Controls.Add(buttonsPanel, 1, 0);
            headerLayout.Controls.Add(chipsCell, 2, 0);
            header.Controls.Add(headerLayout);

            // ---------- tab strip ----------
            var tabStrip = new Panel
            {
                Dock = DockStyle.Top,
                Height = 38,
                BackColor = C_TabStrip,
                Padding = new Padding(14, 0, 0, 0)
            };

            var tabButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };

            ConfigureMainTabButton(_tabConsoleButton, "Console", "console");
            ConfigureMainTabButton(_tabPlayersButton, "Players", "players");
            ConfigureMainTabButton(_tabSettingsButton, "Settings", "settings");
            tabButtons.Controls.Add(_tabConsoleButton);
            tabButtons.Controls.Add(_tabPlayersButton);
            tabButtons.Controls.Add(_tabSettingsButton);
            tabStrip.Controls.Add(tabButtons);

            var tabStripDivider = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = C_Border
            };
            var headerDivider = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = C_Border
            };
            header.Controls.Add(headerDivider);
            tabStrip.Controls.Add(tabStripDivider);

            // ---------- content host ----------
            _contentHost.Dock = DockStyle.Fill;
            _contentHost.BackColor = C_Bg;
            _contentHost.Padding = new Padding(0);

            BuildConsolePage();
            BuildPlayersPage();
            BuildSettingsPage();

            _contentHost.Controls.Add(_consolePage);
            _contentHost.Controls.Add(_playersPage);
            _contentHost.Controls.Add(_settingsPage);

            // ---------- toast ----------
            _toastBanner.Dock = DockStyle.Bottom;
            _toastBanner.Height = 30;
            _toastBanner.Visible = false;
            _toastBanner.TextAlign = ContentAlignment.MiddleLeft;
            _toastBanner.Padding = new Padding(14, 0, 14, 0);
            _toastBanner.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            _toastBanner.BackColor = Color.FromArgb(88, 64, 24);
            _toastBanner.ForeColor = Color.FromArgb(255, 229, 153);

            _toastTimer.Interval = 4500;
            _toastTimer.Tick += (_, __) =>
            {
                _toastTimer.Stop();
                _toastBanner.Visible = false;
            };

            _consoleAutoClearTimer.Interval = 1000;
            _consoleAutoClearTimer.Tick += (_, __) =>
            {
                if (!_consoleRenderingPaused)
                {
                    _consoleAutoClearTimer.Stop();
                    return;
                }

                _consoleAutoClearRemainingSeconds--;
                if (_consoleAutoClearRemainingSeconds <= 0)
                {
                    _consoleAutoClearTimer.Stop();
                    ClearConsoleOutput("Console auto-cleared for safety. Rendering resumed.");
                    return;
                }

                ShowToast("Console rendering paused for safety. Auto-clearing in " + _consoleAutoClearRemainingSeconds + "s...");
            };

            Controls.Add(_contentHost);
            Controls.Add(tabStrip);
            Controls.Add(header);
            Controls.Add(_toastBanner);

            _restartCountdownTimer.Interval = 1000;
            _restartCountdownTimer.Tick += (_, __) =>
            {
                bool running = _serverProcess != null && !_serverProcess.HasExited;
                SetRunningState(running);
            };
            _restartCountdownTimer.Start();

            _playersHeartbeatTimer.Interval = 500;
            _playersHeartbeatTimer.Tick += PlayersHeartbeat_Tick;
            _playersHeartbeatTimer.Start();

            _consoleFlushTimer.Interval = 50;
            _consoleFlushTimer.Tick += (_, __) => FlushPendingConsoleLines();
            _consoleFlushTimer.Start();

            _propertiesReloadTimer.Interval = 400;
            _propertiesReloadTimer.Tick += (_, __) =>
            {
                _propertiesReloadTimer.Stop();
                LoadSettingsFromProperties();
                ShowToast("server.properties changed on disk - settings reloaded.");
            };

            try
            {
                _propertiesWatcher.Path = AppDomain.CurrentDomain.BaseDirectory;
                _propertiesWatcher.Filter = "server.properties";
                _propertiesWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                _propertiesWatcher.Changed += (_, __) => BeginInvoke((Action)(() =>
                {
                    _propertiesReloadTimer.Stop();
                    _propertiesReloadTimer.Start();
                }));
                _propertiesWatcher.Created += (_, __) => BeginInvoke((Action)(() =>
                {
                    _propertiesReloadTimer.Stop();
                    _propertiesReloadTimer.Start();
                }));
                _propertiesWatcher.EnableRaisingEvents = true;
            }
            catch
            {
            }

            LoadSettingsFromProperties();
            SetActiveSettingsSection("server");
            ActivateTab("console");
            SetRunningState(false);

            DoubleBuffered = true;
            SetDoubleBuffered(_contentHost);
            SetDoubleBuffered(_consolePage);
            SetDoubleBuffered(_playersPage);
            SetDoubleBuffered(_settingsPage);
            SetDoubleBuffered(_settingsServerGrid);
            SetDoubleBuffered(_playersList);

            // Nothing in the header is allowed to clip: the minimum width is the
            // actual laid-out width of title + buttons + chips + padding.
            int headerNeeded = titleCell.Width + buttonsPanel.PreferredSize.Width
                + chipsPanel.PreferredSize.Width + header.Padding.Horizontal + 16;
            if (headerNeeded > MinimumSize.Width)
                MinimumSize = new Size(headerNeeded, MinimumSize.Height);
        }

        // ================= UI construction =================

        private static Panel CreateChip()
        {
            return new Panel
            {
                Height = 30,
                BackColor = Color.FromArgb(22, 30, 48),
                Margin = new Padding(6, 2, 0, 2),
                Padding = new Padding(0)
            };
        }

        private static void ConfigureChipLabel(Label label, string text)
        {
            label.Dock = DockStyle.Fill;
            label.Text = text;
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            label.ForeColor = C_TextDim;
            label.BackColor = Color.Transparent;
        }

        private void ConfigureHeaderButton(Button button, string text, int width, Color back, EventHandler onClick)
        {
            button.Text = text;
            button.Width = width;
            button.Height = 34;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = back;
            button.ForeColor = Color.White;
            button.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            button.Margin = new Padding(0, 0, 8, 0);
            button.Cursor = Cursors.Hand;
            button.Click += onClick;
        }

        private void ConfigureMainTabButton(Button button, string text, string tabKey)
        {
            button.Text = text;
            button.Width = 110;
            button.Height = 38;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            button.Margin = new Padding(0);
            button.Cursor = Cursors.Hand;
            button.Click += (_, __) => ActivateTab(tabKey);
        }

        private static void SetDoubleBuffered(Control control)
        {
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(control, true, null);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // WinForms defers layout until a control is first shown, so the first
            // visit to each tab would lay out ~200 controls live and paint
            // mid-reflow. Pre-layout every page here — all inside OnShown, so no
            // paint happens until we return and the swaps are never visible.
            try
            {
                _contentHost.SuspendLayout();
                foreach (Control page in _contentHost.Controls)
                {
                    page.Visible = true;
                    page.PerformLayout();
                    try
                    {
                        using (var warm = new Bitmap(page.Width, page.Height))
                            page.DrawToBitmap(warm, new Rectangle(0, 0, page.Width, page.Height));
                    }
                    catch
                    {
                    }

                    // Settle AutoScroll extent now — otherwise the scrollbar appears on
                    // the first real show and re-lays every row (visible reflow).
                    foreach (ScrollableControl scrollable in new ScrollableControl[] { _settingsServerGrid, _playersList })
                    {
                        if (scrollable.AutoScroll)
                        {
                            try
                            {
                                scrollable.AutoScrollPosition = new Point(0, 100000);
                                scrollable.AutoScrollPosition = Point.Empty;
                            }
                            catch
                            {
                            }
                        }
                    }

                    page.Visible = false;
                }
            }
            finally
            {
                _contentHost.ResumeLayout(true);
                ActivateTab(_activeTab);
                Opacity = 100;
            }
        }

        // Detaches the server child into a REAL console window (child calls
        // AllocConsole) and hides this GUI. Output tees to both so this console's
        // history stays complete. Closing the console window returns to the GUI.
        private void SwitchToTerminal()
        {
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                ShowToast("Start the server first — the terminal attaches to the running server.");
                return;
            }

            SendCommandInternal("@@TERMINAL");
            ShowInTaskbar = false;
            Visible = false;
        }

        private void RestoreGuiWindow()
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            Visible = true;
            Activate();
        }

        private void ActivateTab(string tabKey)
        {
            _activeTab = tabKey;

            // Freeze painting for the whole switch — ~60 child windows in the
            // settings grid would otherwise repaint one-by-one (visible cascade).
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                _consolePage.Visible = tabKey == "console";
                _playersPage.Visible = tabKey == "players";
                _settingsPage.Visible = tabKey == "settings";
                _consolePage.BringToFront();
                if (_playersPage.Visible) _playersPage.BringToFront();
                if (_settingsPage.Visible) _settingsPage.BringToFront();

                ApplyMainTabButtonState(_tabConsoleButton, tabKey == "console");
                ApplyMainTabButtonState(_tabPlayersButton, tabKey == "players");
                ApplyMainTabButtonState(_tabSettingsButton, tabKey == "settings");
            }
            finally
            {
                SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                Invalidate(true);
            }
        }

        private void ApplyMainTabButtonState(Button button, bool active)
        {
            button.BackColor = active ? C_Card : C_TabStrip;
            button.ForeColor = active ? _accentHot : C_TextDim;
        }

        private void BuildConsolePage()
        {
            _consolePage.Dock = DockStyle.Fill;
            _consolePage.BackColor = C_Bg;
            _consolePage.Padding = new Padding(14, 12, 14, 12);
            _consolePage.Visible = false;

            _output.Dock = DockStyle.Fill;
            _output.ReadOnly = true;
            _output.BackColor = C_Surface;
            _output.ForeColor = _consoleText;
            _output.Font = new Font("Consolas", 10f);
            _output.BorderStyle = BorderStyle.None;
            _output.HideSelection = false;
            _output.WordWrap = false;
            _output.ScrollBars = RichTextBoxScrollBars.Both;
            _output.Margin = new Padding(0);
            _output.TabStop = false;
            _output.GotFocus += (_, __) => HideCaret(_output.Handle);
            _output.Enter += (_, __) => HideCaret(_output.Handle);
            _output.MouseDown += (_, __) => HideCaret(_output.Handle);
            _output.MouseUp += (_, __) => HideCaret(_output.Handle);
            _output.HandleCreated += (_, __) => HideCaret(_output.Handle);

            var consoleCard = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Border,
                Padding = new Padding(1)
            };
            var consoleInner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Surface,
                Padding = new Padding(10, 8, 10, 8)
            };
            consoleInner.Controls.Add(_output);
            consoleCard.Controls.Add(consoleInner);

            var toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 34,
                BackColor = C_Bg,
                Padding = new Padding(0, 0, 0, 6)
            };

            var searchHost = new Panel
            {
                Dock = DockStyle.Right,
                Width = 230,
                BackColor = C_Surface,
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(0)
            };
            var searchWrapper = new Panel
            {
                Dock = DockStyle.Right,
                Width = 232,
                Padding = new Padding(0, 3, 0, 3),
                BackColor = Color.Transparent
            };
            _consoleSearchInput.Dock = DockStyle.Fill;
            _consoleSearchInput.BackColor = C_Surface;
            _consoleSearchInput.ForeColor = C_Text;
            _consoleSearchInput.BorderStyle = BorderStyle.None;
            _consoleSearchInput.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            _consoleSearchInput.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    FindConsoleText(_consoleSearchInput.Text);
                }
            };
            _consoleSearchDebounce.Interval = 250;
            _consoleSearchDebounce.Tick += (_, __) =>
            {
                _consoleSearchDebounce.Stop();
                ApplyConsoleHighlights();
            };
            _consoleSearchInput.TextChanged += (_, __) =>
            {
                _consoleSearchDebounce.Stop();
                _consoleSearchDebounce.Start();
            };
            _consoleSearchInput.HandleCreated += (_, __) =>
                SendMessage(_consoleSearchInput.Handle, EM_SETCUEBANNER, (IntPtr)1, "Search console...");
            searchHost.Controls.Add(_consoleSearchInput);
            searchWrapper.Controls.Add(searchHost);

            _pauseScrollButton.Text = "Pause scroll";
            _pauseScrollButton.Dock = DockStyle.Left;
            _pauseScrollButton.Width = 104;
            _pauseScrollButton.FlatStyle = FlatStyle.Flat;
            _pauseScrollButton.FlatAppearance.BorderSize = 0;
            _pauseScrollButton.BackColor = C_ChromeOff;
            _pauseScrollButton.ForeColor = C_Text;
            _pauseScrollButton.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            _pauseScrollButton.Margin = new Padding(0);
            _pauseScrollButton.Cursor = Cursors.Hand;
            _pauseScrollButton.Click += (_, __) =>
            {
                _scrollPaused = !_scrollPaused;
                _pauseScrollButton.Text = _scrollPaused ? "Resume scroll" : "Pause scroll";
                _pauseScrollButton.BackColor = _scrollPaused ? _accentDim : C_ChromeOff;
            };
            toolbar.Controls.Add(searchWrapper);
            toolbar.Controls.Add(_pauseScrollButton);

            var commandPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                Padding = new Padding(0, 8, 0, 0),
                BackColor = C_Bg
            };

            var commandCard = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Border,
                Padding = new Padding(1)
            };
            var commandInner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Surface,
                Padding = new Padding(0)
            };

            _sendCommandButton.Text = "Send";
            _sendCommandButton.Dock = DockStyle.Right;
            _sendCommandButton.Width = 74;
            _sendCommandButton.FlatStyle = FlatStyle.Flat;
            _sendCommandButton.FlatAppearance.BorderSize = 0;
            _sendCommandButton.BackColor = _accent;
            _sendCommandButton.ForeColor = Color.White;
            _sendCommandButton.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            _sendCommandButton.Margin = new Padding(0);
            _sendCommandButton.Cursor = Cursors.Hand;
            _sendCommandButton.Click += (_, __) => SendCommand();

            _clearConsoleButton.Text = "Clear";
            _clearConsoleButton.Dock = DockStyle.Right;
            _clearConsoleButton.Width = 74;
            _clearConsoleButton.FlatStyle = FlatStyle.Flat;
            _clearConsoleButton.FlatAppearance.BorderSize = 0;
            _clearConsoleButton.FlatAppearance.BorderColor = C_Border;
            _clearConsoleButton.BackColor = C_ChromeOff;
            _clearConsoleButton.ForeColor = C_Text;
            _clearConsoleButton.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
            _clearConsoleButton.Margin = new Padding(0);
            _clearConsoleButton.Cursor = Cursors.Hand;
            _clearConsoleButton.Click += (_, __) => ClearConsoleOutput();

            var inputHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Surface,
                Padding = new Padding(10, 5, 10, 5)
            };

            _commandInput.Dock = DockStyle.Fill;
            _commandInput.BackColor = C_Surface;
            _commandInput.ForeColor = C_Text;
            _commandInput.BorderStyle = BorderStyle.None;
            _commandInput.Font = new Font("Consolas", 10f);
            _commandInput.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    SendCommand();
                }
                else if (e.KeyCode == Keys.Up && _commandHistory.Count > 0)
                {
                    e.SuppressKeyPress = true;
                    _commandHistoryIndex = _commandHistoryIndex < 0
                        ? _commandHistory.Count - 1
                        : Math.Max(0, _commandHistoryIndex - 1);
                    _commandInput.Text = _commandHistory[_commandHistoryIndex];
                    _commandInput.SelectionStart = _commandInput.TextLength;
                }
                else if (e.KeyCode == Keys.Down && _commandHistoryIndex >= 0)
                {
                    e.SuppressKeyPress = true;
                    _commandHistoryIndex++;
                    if (_commandHistoryIndex >= _commandHistory.Count)
                    {
                        _commandHistoryIndex = -1;
                        _commandInput.Text = string.Empty;
                    }
                    else
                    {
                        _commandInput.Text = _commandHistory[_commandHistoryIndex];
                        _commandInput.SelectionStart = _commandInput.TextLength;
                    }
                }
            };
            inputHost.Controls.Add(_commandInput);
            _commandInput.HandleCreated += (_, __) =>
                SendMessage(_commandInput.Handle, EM_SETCUEBANNER, (IntPtr)1, "Type a server command and press Enter...");

            _quickCommandsButton.Text = "▾";
            _quickCommandsButton.Dock = DockStyle.Right;
            _quickCommandsButton.Width = 34;
            _quickCommandsButton.FlatStyle = FlatStyle.Flat;
            _quickCommandsButton.FlatAppearance.BorderSize = 0;
            _quickCommandsButton.BackColor = _accent;
            _quickCommandsButton.ForeColor = Color.White;
            _quickCommandsButton.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _quickCommandsButton.Margin = new Padding(0);
            _quickCommandsButton.Cursor = Cursors.Hand;
            _quickCommandsButton.Click += (_, __) => ShowQuickCommandsMenu();

            commandInner.Controls.Add(inputHost);
            commandInner.Controls.Add(_sendCommandButton);
            commandInner.Controls.Add(_quickCommandsButton);
            commandInner.Controls.Add(_clearConsoleButton);
            commandCard.Controls.Add(commandInner);
            commandPanel.Controls.Add(commandCard);

            _consolePage.Controls.Add(consoleCard);
            _consolePage.Controls.Add(commandPanel);
            _consolePage.Controls.Add(toolbar);
        }

        private void BuildPlayersPage()
        {
            _playersPage.Dock = DockStyle.Fill;
            _playersPage.BackColor = C_Bg;
            _playersPage.Padding = new Padding(14, 12, 14, 12);
            _playersPage.Visible = false;

            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Border,
                Padding = new Padding(1)
            };
            var cardInner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Card,
                Padding = new Padding(0)
            };

            var headerRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = C_Card,
                Padding = new Padding(16, 0, 10, 0)
            };

            var titleLabel = new Label
            {
                Dock = DockStyle.Left,
                Width = 200,
                Text = "Connected players",
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
                ForeColor = C_Text,
                BackColor = Color.Transparent
            };

            _playersCountLabel.Dock = DockStyle.Left;
            _playersCountLabel.Width = 120;
            _playersCountLabel.Text = "";
            _playersCountLabel.TextAlign = ContentAlignment.MiddleLeft;
            _playersCountLabel.Font = new Font("Segoe UI", 8.5f, FontStyle.Regular);
            _playersCountLabel.ForeColor = C_TextDim;
            _playersCountLabel.BackColor = Color.Transparent;

            var filterHost = new Panel
            {
                Dock = DockStyle.Right,
                Width = 220,
                BackColor = C_Surface,
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(0)
            };
            filterHost.Padding = new Padding(8, 0, 8, 0);
            var filterWrapper = new Panel
            {
                Dock = DockStyle.Right,
                Width = 232,
                Padding = new Padding(0, 12, 0, 12),
                BackColor = Color.Transparent
            };
            _playersFilterInput.Dock = DockStyle.Fill;
            _playersFilterInput.BackColor = C_Surface;
            _playersFilterInput.ForeColor = C_Text;
            _playersFilterInput.BorderStyle = BorderStyle.None;
            _playersFilterInput.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            _playersFilterInput.TextChanged += (_, __) => ApplyPlayersFilter();
            _playersFilterInput.HandleCreated += (_, __) =>
                SendMessage(_playersFilterInput.Handle, EM_SETCUEBANNER, (IntPtr)1, "Filter by name or SteamID...");
            filterHost.Controls.Add(_playersFilterInput);
            filterWrapper.Controls.Add(filterHost);

            headerRow.Controls.Add(filterWrapper);
            headerRow.Controls.Add(_playersCountLabel);
            headerRow.Controls.Add(titleLabel);

            var headerDivider = new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = C_Border
            };

            var listHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Card,
                Padding = new Padding(10, 10, 10, 4)
            };

            _playersList.Dock = DockStyle.Fill;
            _playersList.AutoScroll = true;
            _playersList.BackColor = C_Card;
            _playersList.Padding = new Padding(0);
            _playersList.Margin = new Padding(0);
            _playersList.SizeChanged += (_, __) => ResizePlayerCards();

            _playersEmptyState.Text = "No players connected.";
            _playersEmptyState.Dock = DockStyle.Fill;
            _playersEmptyState.TextAlign = ContentAlignment.MiddleCenter;
            _playersEmptyState.Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);
            _playersEmptyState.ForeColor = C_TextFaint;
            _playersEmptyState.BackColor = C_Card;

            listHost.Controls.Add(_playersList);
            listHost.Controls.Add(_playersEmptyState);

            cardInner.Controls.Add(listHost);
            cardInner.Controls.Add(headerDivider);
            cardInner.Controls.Add(headerRow);
            card.Controls.Add(cardInner);
            _playersPage.Controls.Add(card);
        }

        private void BuildSettingsPage()
        {
            _settingsPage.Dock = DockStyle.Fill;
            _settingsPage.BackColor = C_Bg;
            _settingsPage.Padding = new Padding(14, 12, 14, 12);
            _settingsPage.Visible = false;

            var outerCard = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Border,
                Padding = new Padding(1)
            };
            var outerInner = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Card,
                Padding = new Padding(0)
            };

            var topRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 46,
                BackColor = C_Card,
                Padding = new Padding(12, 8, 12, 8)
            };

            var sectionTabs = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            ConfigureSettingsSectionTabButton(_settingsServerTabButton, "server.properties", "server");
            ConfigureSettingsSectionTabButton(_settingsGuiTabButton, "Appearance", "gui");
            sectionTabs.Controls.Add(_settingsServerTabButton);
            sectionTabs.Controls.Add(_settingsGuiTabButton);

            var settingsFilterHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = C_Surface,
                Padding = new Padding(8, 0, 8, 0)
            };
            _settingsFilterWrapper = new Panel
            {
                Width = 232,
                Height = 30,
                Padding = new Padding(0, 4, 0, 4),
                Margin = new Padding(10, 0, 6, 0),
                BackColor = Color.Transparent
            };
            _settingsFilterInput.Dock = DockStyle.Fill;
            _settingsFilterInput.BackColor = C_Surface;
            _settingsFilterInput.ForeColor = C_Text;
            _settingsFilterInput.BorderStyle = BorderStyle.None;
            _settingsFilterInput.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
            _settingsFilterInput.TextChanged += (_, __) => ApplySettingsFilter();
            _settingsFilterInput.HandleCreated += (_, __) =>
                SendMessage(_settingsFilterInput.Handle, EM_SETCUEBANNER, (IntPtr)1, "Filter settings...");
            settingsFilterHost.Controls.Add(_settingsFilterInput);
            _settingsFilterWrapper.Controls.Add(settingsFilterHost);

            var actionsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            ConfigureSettingsActionButton(_settingsSaveButton, "Save to file", 110, _accentDim);
            _settingsSaveButton.Click += (_, __) => SaveSettingsToProperties();
            _settingsSaveButton.Enabled = false;

            ConfigureSettingsActionButton(_settingsReloadButton, "Reload", 80, C_ChromeOff);
            _settingsReloadButton.Click += (_, __) =>
            {
                LoadSettingsFromProperties();
                ShowToast("Settings reloaded from server.properties.");
            };

            actionsPanel.Controls.Add(_settingsFilterWrapper);
            actionsPanel.Controls.Add(_settingsSaveButton);
            actionsPanel.Controls.Add(_settingsReloadButton);

            var topRowLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };
            topRowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topRowLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            topRowLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var actionsCell = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, Margin = new Padding(0), Padding = new Padding(0) };
            actionsPanel.Dock = DockStyle.Right;
            actionsCell.Controls.Add(actionsPanel);

            topRowLayout.Controls.Add(sectionTabs, 0, 0);
            topRowLayout.Controls.Add(actionsCell, 1, 0);
            topRow.Controls.Add(topRowLayout);

            var topDivider = new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = C_Border
            };

            var scroll = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = C_Card,
                Padding = new Padding(12, 12, 12, 12)
            };

            var stack = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            ConfigureSettingsCardHost(_settingsServerCard);
            ConfigureSettingsCardHost(_settingsGuiCard);

            ConfigureSettingsGrid(_settingsServerGrid, 0);

            var serverCardContent = CreateSettingsCard(
                "server.properties",
                "Mirrors the file on disk, in the file's own order. Unknown keys appear with their raw name and save back like any other property.",
                _settingsServerGrid);
            serverCardContent.Dock = DockStyle.Top;
            _settingsServerCard.Controls.Add(serverCardContent);

            ConfigureAppearanceGrid();
            var guiCardContent = CreateSettingsCard(string.Empty, string.Empty, _settingsGuiGrid);
            guiCardContent.Dock = DockStyle.Top;
            _settingsGuiCard.Controls.Add(guiCardContent);

            stack.Controls.Add(_settingsGuiCard);
            stack.Controls.Add(_settingsServerCard);
            scroll.Controls.Add(stack);

            outerInner.Controls.Add(scroll);
            outerInner.Controls.Add(topDivider);
            outerInner.Controls.Add(topRow);
            outerCard.Controls.Add(outerInner);
            _settingsPage.Controls.Add(outerCard);
        }

        private void ConfigureAppearanceGrid()
        {
            ConfigureSettingsGrid(_settingsGuiGrid, 6);
            AddSettingsSectionHeader(_settingsGuiGrid, 0, "Appearance");
            AddSettingsRow(_settingsGuiGrid, 1, "Accent color", _appearanceAccentInput);
            AddSettingsRow(_settingsGuiGrid, 2, "Console text", _appearanceConsoleTextInput);
            AddSettingsSectionHeader(_settingsGuiGrid, 3, "About");
            var note = new Label
            {
                Dock = DockStyle.Top,
                Height = 44,
                Text = "Appearance is stored in GuiSettings.ini next to the server executable and applies instantly. server.properties changes save with the button above and take effect on the next server start.",
                TextAlign = ContentAlignment.TopLeft,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = C_TextDim,
                BackColor = Color.Transparent,
                Padding = new Padding(0, 4, 0, 0),
                Margin = new Padding(0)
            };
            _settingsGuiGrid.Controls.Add(note, 0, 4);
            _settingsGuiGrid.SetColumnSpan(note, 2);

            _appearanceAccentInput.Dock = DockStyle.Top;
            _appearanceAccentInput.Width = 170;
            _appearanceAccentInput.DropDownStyle = ComboBoxStyle.DropDownList;
            _appearanceAccentInput.BackColor = C_Surface;
            _appearanceAccentInput.ForeColor = C_Text;
            _appearanceAccentInput.Font = new Font("Segoe UI", 9f);
            _appearanceAccentInput.Items.AddRange(new object[]
                { "Blue", "Green", "Purple", "Orange", "Red", "Teal", "Custom..." });
            _appearanceAccentInput.SelectedIndexChanged += (_, __) => OnAccentSelectionChanged();

            _appearanceConsoleTextInput.Dock = DockStyle.Top;
            _appearanceConsoleTextInput.Width = 170;
            _appearanceConsoleTextInput.DropDownStyle = ComboBoxStyle.DropDownList;
            _appearanceConsoleTextInput.BackColor = C_Surface;
            _appearanceConsoleTextInput.ForeColor = C_Text;
            _appearanceConsoleTextInput.Font = new Font("Segoe UI", 9f);
            _appearanceConsoleTextInput.Items.AddRange(new object[]
                { "Soft green", "White", "Amber", "Cyan" });
            _appearanceConsoleTextInput.SelectedIndexChanged += (_, __) => OnConsoleTextSelectionChanged();

            _suppressAppearanceEvents = true;
            _appearanceAccentInput.SelectedItem = DescribeColor(_accent);
            _appearanceConsoleTextInput.SelectedItem = DescribeConsoleTextColor();
            _suppressAppearanceEvents = false;
        }

        private void ConfigureSettingsActionButton(Button button, string text, int width, Color back)
        {
            button.Text = text;
            button.Width = width;
            button.Height = 30;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = back;
            button.ForeColor = Color.White;
            button.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            button.Margin = new Padding(6, 0, 0, 0);
            button.Cursor = Cursors.Hand;
        }

        // ================= theme =================

        private static string GuiSettingsPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GuiSettings.ini");

        private void LoadGuiSettings()
        {
            try
            {
                if (!File.Exists(GuiSettingsPath))
                    return;

                foreach (string raw in File.ReadAllLines(GuiSettingsPath))
                {
                    string line = (raw ?? string.Empty).Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = line.Substring(eq + 1).Trim();
                    if (string.Equals(key, "accent", StringComparison.OrdinalIgnoreCase))
                        TrySetAccentFromText(value);
                    else if (string.Equals(key, "console-text", StringComparison.OrdinalIgnoreCase))
                        TrySetConsoleTextFromText(value);
                }
            }
            catch
            {
            }
        }

        private void SaveGuiSettings()
        {
            try
            {
                File.WriteAllLines(GuiSettingsPath, new[]
                {
                    "# GUI appearance settings",
                    "accent=#" + _accent.R.ToString("X2") + _accent.G.ToString("X2") + _accent.B.ToString("X2"),
                    "console-text=#" + _consoleText.R.ToString("X2") + _consoleText.G.ToString("X2") + _consoleText.B.ToString("X2")
                });
            }
            catch
            {
            }
        }

        private void TrySetAccentFromText(string value)
        {
            try
            {
                if (value.StartsWith("#", StringComparison.Ordinal))
                    value = value.Substring(1);
                _accent = ColorTranslator.FromHtml("#" + value);
            }
            catch
            {
            }

            _accentHot = ControlPaint.Light(_accent, 0.25f);
            _accentDim = ControlPaint.Dark(_accent, 0.45f);
        }

        private void TrySetConsoleTextFromText(string value)
        {
            try
            {
                if (value.StartsWith("#", StringComparison.Ordinal))
                    value = value.Substring(1);
                _consoleText = ColorTranslator.FromHtml("#" + value);
            }
            catch
            {
            }
        }

        private bool _suppressAppearanceEvents;

        private void OnAccentSelectionChanged()
        {
            if (_suppressAppearanceEvents)
                return;

            string sel = _appearanceAccentInput.SelectedItem as string;
            if (sel == "Custom...")
            {
                using (var dlg = new ColorDialog { Color = _accent, FullOpen = true })
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _accent = dlg.Color;
                        _accentHot = ControlPaint.Light(_accent, 0.25f);
                        _accentDim = ControlPaint.Dark(_accent, 0.45f);
                        ApplyAccentTheme();
                        SaveGuiSettings();
                    }
                }
                return;
            }

            Color chosen;
            switch (sel)
            {
                case "Green": chosen = Color.FromArgb(46, 204, 113); break;
                case "Purple": chosen = Color.FromArgb(155, 89, 182); break;
                case "Orange": chosen = Color.FromArgb(230, 126, 34); break;
                case "Red": chosen = Color.FromArgb(231, 76, 60); break;
                case "Teal": chosen = Color.FromArgb(26, 188, 156); break;
                default: chosen = Color.FromArgb(52, 152, 219); break;
            }

            _accent = chosen;
            _accentHot = ControlPaint.Light(_accent, 0.25f);
            _accentDim = ControlPaint.Dark(_accent, 0.45f);
            ApplyAccentTheme();
            SaveGuiSettings();
        }

        private void OnConsoleTextSelectionChanged()
        {
            if (_suppressAppearanceEvents)
                return;

            string sel = _appearanceConsoleTextInput.SelectedItem as string;
            switch (sel)
            {
                case "White": _consoleText = Color.FromArgb(226, 232, 245); break;
                case "Amber": _consoleText = Color.FromArgb(244, 200, 120); break;
                case "Cyan": _consoleText = Color.FromArgb(140, 220, 235); break;
                default: _consoleText = Color.FromArgb(166, 226, 170); break;
            }

            _output.ForeColor = _consoleText;
            SaveGuiSettings();
        }

        private void ApplyAccentTheme()
        {
            if (IsServerProcessRunning())
            {
                _startButton.BackColor = C_ChromeOff;
            }
            else
            {
                _startButton.BackColor = _accent;
            }
            _sendCommandButton.BackColor = _accent;
            _quickCommandsButton.BackColor = _accent;
            _settingsSaveButton.BackColor = _settingsSaveButton.Enabled ? _accent : _accentDim;
            foreach (Label headerLabel in _accentHeaderLabels)
                headerLabel.ForeColor = _accentHot;
            ActivateTab(_activeTab);
            ApplySettingsSectionTabButtonState(_settingsServerTabButton, _activeSettingsSection == "server");
            ApplySettingsSectionTabButtonState(_settingsGuiTabButton, _activeSettingsSection == "gui");
            Invalidate(true);
        }

        private string DescribeColor(Color c)
        {
            if (c == Color.FromArgb(46, 204, 113)) return "Green";
            if (c == Color.FromArgb(155, 89, 182)) return "Purple";
            if (c == Color.FromArgb(230, 126, 34)) return "Orange";
            if (c == Color.FromArgb(231, 76, 60)) return "Red";
            if (c == Color.FromArgb(26, 188, 156)) return "Teal";
            if (c == Color.FromArgb(52, 152, 219)) return "Blue";
            return "Custom...";
        }

        private string DescribeConsoleTextColor()
        {
            if (_consoleText == Color.FromArgb(226, 232, 245)) return "White";
            if (_consoleText == Color.FromArgb(244, 200, 120)) return "Amber";
            if (_consoleText == Color.FromArgb(140, 220, 235)) return "Cyan";
            return "Soft green";
        }

        // ================= settings =================

        private static TextBox CreateSettingsTextBox()
        {
            return new TextBox
            {
                Dock = DockStyle.Top,
                BackColor = C_Surface,
                ForeColor = C_Text,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9.5f),
                Margin = new Padding(0, 2, 0, 2)
            };
        }

        private static ComboBox CreateSettingsBoolCombo()
        {
            var combo = new NoWheelComboBox
            {
                Dock = DockStyle.Top,
                Width = 170,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = C_Surface,
                ForeColor = C_Text,
                Font = new Font("Segoe UI", 9f, FontStyle.Regular),
                Margin = new Padding(0, 2, 0, 2)
            };

            combo.Items.AddRange(new object[] { "true", "false" });
            combo.SelectedIndex = 0;
            return combo;
        }

        private static void ConfigureSettingsGrid(TableLayoutPanel grid, int rowCount)
        {
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.BackColor = C_RowDeep;
            grid.Padding = new Padding(0);
            grid.ColumnCount = 2;
            grid.ColumnStyles.Clear();
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid.RowCount = rowCount;
            grid.RowStyles.Clear();
            for (int i = 0; i < rowCount; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
        }

        private void AddSettingsRow(TableLayoutPanel grid, int rowIndex, string labelText, Control input)
        {
            var label = new Label
            {
                Dock = DockStyle.Fill,
                Text = labelText,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(180, 196, 222),
                BackColor = Color.Transparent,
                Margin = new Padding(0, 8, 12, 8),
                Height = 28
            };

            var inputHost = new Panel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.Transparent,
                Margin = new Padding(0, 4, 0, 4),
                Padding = new Padding(0)
            };

            if (input is ComboBox combo)
            {
                combo.Anchor = AnchorStyles.Left | AnchorStyles.Top;
            }
            else
            {
                input.Dock = DockStyle.Top;
            }

            if (grid.RowCount <= rowIndex)
            {
                grid.RowCount = rowIndex + 1;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            grid.Controls.Add(label, 0, rowIndex);
            inputHost.Controls.Add(input);
            grid.Controls.Add(inputHost, 1, rowIndex);

            List<SettingsLayoutEntry> layout = LayoutForGrid(grid);
            if (layout != null)
            {
                MarkInputDirtyOnEdit(input);
                layout.Add(new SettingsLayoutEntry
                {
                    IsHeader = false,
                    Name = labelText,
                    Controls = new Control[] { label, inputHost }
                });
            }
        }

        private void MarkInputDirtyOnEdit(Control input)
        {
            if (input is TextBox tb)
                tb.TextChanged += (_, __) => MarkSettingsDirty();
            else if (input is ComboBox cb)
                cb.SelectedIndexChanged += (_, __) => MarkSettingsDirty();
        }

        private void MarkSettingsDirty()
        {
            if (_loadingSettings || _settingsDirty)
                return;

            _settingsDirty = true;
            _settingsSaveButton.Enabled = true;
            _settingsSaveButton.BackColor = _accent;
        }

        private void AddSettingsSectionHeader(TableLayoutPanel grid, int rowIndex, string title)
        {
            var header = new Label
            {
                Dock = DockStyle.Fill,
                Text = title,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 9.75f, FontStyle.Bold),
                ForeColor = _accentHot,
                BackColor = Color.FromArgb(18, 25, 40),
                Padding = new Padding(10, 0, 10, 0),
                Margin = new Padding(0, 10, 0, 6),
                Height = 30
            };

            if (grid.RowCount <= rowIndex)
            {
                grid.RowCount = rowIndex + 1;
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            grid.Controls.Add(header, 0, rowIndex);
            grid.SetColumnSpan(header, 2);
            _accentHeaderLabels.Add(header);

            List<SettingsLayoutEntry> layout = LayoutForGrid(grid);
            if (layout != null)
            {
                layout.Add(new SettingsLayoutEntry
                {
                    IsHeader = true,
                    Name = title,
                    Controls = new Control[] { header }
                });
            }
        }

        private List<SettingsLayoutEntry> LayoutForGrid(TableLayoutPanel grid)
        {
            if (ReferenceEquals(grid, _settingsServerGrid))
                return _serverSettingsLayout;
            return null;
        }

        private void ApplySettingsFilter()
        {
            string filter = (_settingsFilterInput.Text ?? string.Empty).Trim();
            ApplySettingsFilterTo(_serverSettingsLayout, filter);
        }

        private static void ApplySettingsFilterTo(List<SettingsLayoutEntry> layout, string filter)
        {
            if (layout == null || layout.Count == 0)
                return;

            bool filtering = filter.Length > 0;

            for (int i = 0; i < layout.Count; i++)
            {
                SettingsLayoutEntry entry = layout[i];
                if (entry.IsHeader)
                    continue;

                bool visible = !filtering
                    || entry.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                foreach (Control c in entry.Controls)
                    c.Visible = visible;
            }

            for (int i = 0; i < layout.Count; i++)
            {
                if (!layout[i].IsHeader)
                    continue;

                bool anyVisible = false;
                for (int j = i + 1; j < layout.Count && !layout[j].IsHeader; j++)
                {
                    if (layout[j].Controls[0].Visible)
                    {
                        anyVisible = true;
                        break;
                    }
                }

                foreach (Control c in layout[i].Controls)
                    c.Visible = !filtering || anyVisible;
            }
        }

        private void ConfigureSettingsSectionTabButton(Button button, string text, string sectionKey)
        {
            button.Text = text;
            button.Width = sectionKey == "server" ? 140 : 96;
            button.Height = 30;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.Margin = new Padding(0, 0, 6, 0);
            button.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
            button.ForeColor = C_TextDim;
            button.Cursor = Cursors.Hand;
            button.Click += (_, __) => SetActiveSettingsSection(sectionKey);
        }

        private void ConfigureSettingsCardHost(Panel panel)
        {
            panel.Dock = DockStyle.Top;
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.BackColor = Color.Transparent;
            panel.Margin = new Padding(0);
            panel.Padding = new Padding(0);
        }

        private void SetActiveSettingsSection(string sectionKey)
        {
            _activeSettingsSection = string.Equals(sectionKey, "gui", StringComparison.OrdinalIgnoreCase)
                ? "gui"
                : "server";

            bool showingServer = _activeSettingsSection == "server";
            _settingsServerCard.Visible = showingServer;
            _settingsGuiCard.Visible = !showingServer;
            _settingsSaveButton.Visible = showingServer;
            _settingsReloadButton.Visible = showingServer;
            if (_settingsFilterWrapper != null)
                _settingsFilterWrapper.Visible = showingServer;

            ApplySettingsSectionTabButtonState(_settingsServerTabButton, showingServer);
            ApplySettingsSectionTabButtonState(_settingsGuiTabButton, !showingServer);
        }

        private void ApplySettingsSectionTabButtonState(Button button, bool active)
        {
            button.BackColor = active ? _accent : C_ChromeOff;
            button.ForeColor = active ? Color.White : C_TextDim;
        }

        private Panel CreateSettingsCard(string title, string description, Control content)
        {
            var card = new Panel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = C_Border,
                Padding = new Padding(1),
                Margin = new Padding(0, 0, 0, 14)
            };

            var inner = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = C_RowDeep,
                Padding = new Padding(16, 16, 16, 16),
                Margin = new Padding(0)
            };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            inner.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var titleLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Text = title,
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                ForeColor = C_Text,
                Margin = new Padding(0, 0, 0, 4)
            };

            var descriptionLabel = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                Text = description,
                Font = new Font("Segoe UI", 8.75f, FontStyle.Regular),
                ForeColor = C_TextDim,
                Margin = new Padding(0, 0, 0, 14)
            };

            content.Margin = new Padding(0);
            if (!string.IsNullOrWhiteSpace(title))
                inner.Controls.Add(titleLabel, 0, 0);

            if (!string.IsNullOrWhiteSpace(description))
                inner.Controls.Add(descriptionLabel, 0, 1);

            inner.Controls.Add(content, 0, 2);

            card.Controls.Add(inner);
            return card;
        }

        private void LoadSettingsFromProperties()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server.properties");
            _settingsSections.Clear();

            if (File.Exists(path))
            {
                string[] lines = File.ReadAllLines(path);
                SettingsSection current = null;
                var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = (lines[i] ?? string.Empty).Trim();

                    bool isCommentBar = line.StartsWith("#", StringComparison.Ordinal)
                        && line.IndexOf("====", StringComparison.Ordinal) >= 0;
                    if (isCommentBar
                        && i + 2 < lines.Length
                        && IsCommentTitleLine(lines[i + 1])
                        && (lines[i + 2] ?? string.Empty).Trim().IndexOf("====", StringComparison.Ordinal) >= 0)
                    {
                        current = new SettingsSection { Title = CleanCommentText(lines[i + 1]) };
                        _settingsSections.Add(current);
                        i += 2;
                        continue;
                    }

                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                        continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;

                    string key = line.Substring(0, eq).Trim();
                    string value = eq + 1 < line.Length ? line.Substring(eq + 1).Trim() : string.Empty;
                    if (key.Length == 0 || !seenKeys.Add(key))
                        continue;

                    if (current == null)
                    {
                        current = new SettingsSection { Title = "General" };
                        _settingsSections.Add(current);
                    }
                    current.Entries.Add(new KeyValuePair<string, string>(key, value));
                }
            }

            if (_settingsSections.Count == 0)
            {
                var fallback = new SettingsSection { Title = "General" };
                foreach (KeyValuePair<string, SettingDescriptor> d in SettingDescriptors)
                    fallback.Entries.Add(new KeyValuePair<string, string>(d.Key, string.Empty));
                _settingsSections.Add(fallback);
            }

            RebuildSettingsEditor();
        }

        private static bool IsCommentTitleLine(string line)
        {
            string t = (line ?? string.Empty).Trim();
            return t.StartsWith("#", StringComparison.Ordinal)
                && t.IndexOf("====", StringComparison.Ordinal) < 0
                && t.Length > 1;
        }

        private static string CleanCommentText(string line)
        {
            string t = (line ?? string.Empty).Trim().TrimStart('#').Trim();
            return t;
        }

        private void RebuildSettingsEditor()
        {
            _loadingSettings = true;
            try
            {
                _settingsServerGrid.SuspendLayout();
                _accentHeaderLabels.RemoveAll(l => l == null || l.Parent == _settingsServerGrid || l.Parent == null);
                _settingsServerGrid.Controls.Clear();
                _settingsServerGrid.RowCount = 0;
                _settingsServerGrid.RowStyles.Clear();
                _serverSettingsLayout.Clear();
                _settingsRows.Clear();

                int row = 0;
                bool showHeaders = _settingsSections.Count > 1;
                foreach (SettingsSection section in _settingsSections)
                {
                    if (showHeaders)
                    {
                        AddSettingsSectionHeader(_settingsServerGrid, row, section.Title);
                        row++;
                    }

                    foreach (KeyValuePair<string, string> entry in section.Entries)
                    {
                        Control editor = CreateEditorFor(entry.Key, entry.Value);
                        string label = SettingDescriptors.TryGetValue(entry.Key, out SettingDescriptor d)
                            ? d.Label
                            : entry.Key;
                        AddSettingsRow(_settingsServerGrid, row, label, editor);
                        _settingsRows.Add(new SettingRow { Key = entry.Key, Editor = editor });
                        row++;
                    }
                }

                _settingsDirty = false;
                _settingsSaveButton.Enabled = false;
                _settingsSaveButton.BackColor = _accentDim;
            }
            finally
            {
                _loadingSettings = false;
                _settingsServerGrid.ResumeLayout(true);
            }

            ApplySettingsFilter();
        }

        private Control CreateEditorFor(string key, string value)
        {
            SettingDescriptors.TryGetValue(key, out SettingDescriptor d);

            if (d != null && d.Kind == "enum")
            {
                var combo = new NoWheelComboBox
                {
                    Width = 260,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = C_Surface,
                    ForeColor = C_Text,
                    Font = new Font("Segoe UI", 9f),
                    Margin = new Padding(0, 2, 0, 2)
                };

                EnumOption match = null;
                foreach (string opt in d.Options)
                {
                    int sep = opt.IndexOf('|');
                    var item = new EnumOption
                    {
                        Value = opt.Substring(0, sep),
                        Text = opt.Substring(0, sep) + " - " + opt.Substring(sep + 1)
                    };
                    combo.Items.Add(item);
                    if (string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))
                        match = item;
                }

                if (match == null && value.Length > 0)
                {
                    match = new EnumOption { Value = value, Text = value + " - custom" };
                    combo.Items.Add(match);
                }

                combo.SelectedItem = match ?? combo.Items[0];
                return combo;
            }

            bool isBool = (d != null && d.Kind == "bool")
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            if (isBool)
            {
                ComboBox combo = CreateSettingsBoolCombo();
                combo.SelectedItem = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    ? "true"
                    : "false";
                return combo;
            }

            TextBox box = CreateSettingsTextBox();
            box.Text = value;
            return box;
        }

        private static string ReadEditorValue(Control editor)
        {
            if (editor is TextBox tb)
                return tb.Text;
            if (editor is ComboBox cb)
            {
                if (cb.SelectedItem is EnumOption opt)
                    return opt.Value;
                return cb.SelectedItem as string ?? string.Empty;
            }
            return string.Empty;
        }

        private void SaveSettingsToProperties()
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "server.properties");

            var newValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (SettingRow row in _settingsRows)
                newValues[row.Key] = ReadEditorValue(row.Editor);

            try
            {
                var writtenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var outputLines = new List<string>();

                if (File.Exists(path))
                {
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        string line = rawLine ?? string.Empty;
                        string trimmed = line.Trim();
                        int eq = trimmed.IndexOf('=');
                        bool isKeyLine = trimmed.Length > 0
                            && !trimmed.StartsWith("#", StringComparison.Ordinal)
                            && eq > 0;

                        if (!isKeyLine)
                        {
                            outputLines.Add(rawLine);
                            continue;
                        }

                        string key = trimmed.Substring(0, eq).Trim();
                        if (newValues.TryGetValue(key, out string newValue))
                        {
                            outputLines.Add(key + "=" + newValue);
                            writtenKeys.Add(key);
                        }
                        else
                        {
                            outputLines.Add(rawLine);
                        }
                    }
                }
                else
                {
                    outputLines.Add("# CMZ Dedicated Steam Server properties");
                }

                foreach (KeyValuePair<string, string> pair in newValues)
                {
                    if (!writtenKeys.Contains(pair.Key))
                        outputLines.Add(pair.Key + "=" + pair.Value);
                }

                if (File.Exists(path))
                    File.Copy(path, path + ".bak", true);

                File.WriteAllLines(path, outputLines);
                _settingsDirty = false;
                _settingsSaveButton.Enabled = false;
                _settingsSaveButton.BackColor = _accentDim;
                ShowToast("Saved to server.properties (backup at server.properties.bak). Restart the server for changes to apply.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save server.properties: " + ex.Message,
                    "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void SetBoolComboValue(ComboBox combo, string value)
        {
            string normalized = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
            combo.SelectedItem = normalized;
        }

        // ================= process control =================

        private void SendCommand()
        {
            string command = (_commandInput.Text ?? string.Empty).Trim();
            if (command.Length == 0)
                return;

            if (_commandHistory.Count == 0
                || !string.Equals(_commandHistory[_commandHistory.Count - 1], command, StringComparison.Ordinal))
            {
                _commandHistory.Add(command);
                if (_commandHistory.Count > 50)
                    _commandHistory.RemoveAt(0);
            }
            _commandHistoryIndex = -1;

            _commandInput.Clear();
            SendCommandInternal(command);
        }

        private void ShowQuickCommandsMenu()
        {
            var menu = new ContextMenuStrip
            {
                BackColor = C_Card,
                ForeColor = C_Text,
                ShowImageMargin = false,
                Renderer = new DarkMenuRenderer()
            };

            string[] commands = { "help", "list", "say ", "kick ", "ban ", "save-all", "!restart" };
            foreach (string cmd in commands)
            {
                string text = cmd;
                menu.Items.Add(cmd.TrimEnd(), null, (_, __) =>
                {
                    _commandInput.Text = text;
                    _commandInput.SelectionStart = _commandInput.TextLength;
                    _commandInput.Focus();
                });
            }

            menu.Show(_quickCommandsButton, new Point(_quickCommandsButton.Width - menu.Width, _quickCommandsButton.Height));
        }

        private void ApplyConsoleHighlights()
        {
            string needle = _consoleSearchInput.Text ?? string.Empty;
            try
            {
                _output.SuspendLayout();
                int selStart = _output.SelectionStart;
                int selLen = _output.SelectionLength;

                _output.Select(0, _output.TextLength);
                _output.SelectionBackColor = _output.BackColor;

                int first = -1;
                if (needle.Length >= 2)
                {
                    int pos = 0;
                    int matches = 0;
                    while (matches < 500)
                    {
                        int found = _output.Find(needle, pos, RichTextBoxFinds.None);
                        if (found < 0)
                            break;

                        _output.Select(found, needle.Length);
                        _output.SelectionBackColor = Color.FromArgb(130, 95, 25);
                        if (first < 0)
                            first = found;
                        pos = found + needle.Length;
                        matches++;
                    }

                    if (first >= 0)
                    {
                        _output.Select(first, needle.Length);
                        _output.ScrollToCaret();
                    }
                }

                if (first < 0)
                    _output.Select(selStart, selLen);

                _output.ResumeLayout();
            }
            catch
            {
                try { _output.ResumeLayout(); } catch { }
            }
        }

        private void FindConsoleText(string needle)
        {
            if (string.IsNullOrEmpty(needle) || _output.TextLength == 0)
                return;

            int start = _output.SelectionStart + Math.Max(_output.SelectionLength, 1);
            int found = _output.Find(needle, start, RichTextBoxFinds.None);
            if (found < 0)
                found = _output.Find(needle, 0, RichTextBoxFinds.None);

            if (found >= 0)
            {
                _output.SelectionStart = found;
                _output.SelectionLength = needle.Length;
                _output.ScrollToCaret();
                _output.Focus();
            }
            else
            {
                ShowToast("No console match for \"" + needle + "\".");
            }
        }

        private void SendCommandInternal(string command)
        {
            if (_serverProcess == null || _serverProcess.HasExited)
            {
                AppendLine("[GUI] Server is not running.");
                return;
            }

            if (string.Equals(command, "!restart", StringComparison.OrdinalIgnoreCase))
            {
                if (_restartQueued)
                {
                    AppendLine("[GUI] Restart already in progress.");
                    return;
                }

                _serverReady = false;
                _restartQueued = true;
                SetRunningState(true);
            }

            try
            {
                _serverProcess.StandardInput.WriteLine(command);
                _serverProcess.StandardInput.Flush();
            }
            catch (Exception ex)
            {
                AppendLine("[GUI] Failed to send command: " + ex.Message);
            }
        }

        private void StartServer()
        {
            if (_isStartInProgress)
            {
                NotifyAlreadyRunningServer();
                return;
            }

            if (IsServerProcessRunning())
            {
                NotifyAlreadyRunningServer();
                return;
            }

            _isStartInProgress = true;

            _serverReady = false;
            _restartQueued = false;
            _isStopping = false;
            _stopRequestedByUser = false;
            _serverRunningSinceUtc = DateTime.MinValue;
            _startupPopupShown = false;
            _startupErrorLines.Clear();
            _recentErrorLines.Clear();
            _lastStartAttemptUtc = DateTime.UtcNow;
            _restartUnlockUtc = DateTime.MinValue;
            _connectedPlayers = 0;
            _maxPlayers = -1;
            _currentTpsText = "--";
            _pendingNamesBySteamId.Clear();
            _steamIdsByGid.Clear();
            _playerInfoBySteamId.Clear();
            _playersList.Controls.Clear();
            _playersFilterInput.Clear();
            UpdatePlayersEmptyState();

            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            string workingDir = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--console",
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _serverProcess.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;

                if (e.Data.StartsWith("@@TPS ", StringComparison.Ordinal))
                {
                    string tpsValue = e.Data.Substring(6).Trim();
                    if (double.TryParse(tpsValue, out double tps))
                        _currentTpsText = tps.ToString("0.0");
                    return;
                }

                if (e.Data.StartsWith("@@PINFO ", StringComparison.Ordinal))
                {
                    string payload = e.Data.Substring(8);
                    BeginInvoke((Action)(() => HandlePlayerInfoLine(payload)));
                    return;
                }

                if (e.Data.StartsWith("@@PLAYERS", StringComparison.Ordinal))
                {
                    string roster = e.Data.Substring(9);
                    BeginInvoke((Action)(() => ReconcilePlayerCards(roster)));
                    return;
                }

                if (string.Equals(e.Data, "@@GUISHOW", StringComparison.Ordinal))
                {
                    BeginInvoke((Action)RestoreGuiWindow);
                    return;
                }

                AppendLine(e.Data);
            };
            _serverProcess.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                    return;

                TrackStartupErrorLine(e.Data);
                TrackRecentErrorLine(e.Data);
                AppendLine("[ERR] " + e.Data);
            };
            _serverProcess.Exited += (_, __) => BeginInvoke((Action)(() =>
            {
                if (!Visible)
                    RestoreGuiWindow();

                bool earlyStartupExit = !_serverReady
                    && !_stopRequestedByUser
                    && DateTime.UtcNow - _lastStartAttemptUtc <= StartupFailureWindow;
                bool unexpectedRuntimeExit = _serverReady && !_stopRequestedByUser;

                _isStopping = false;
                _serverRunningSinceUtc = DateTime.MinValue;
                _currentTpsText = "--";
                SetRunningState(false);
                AppendLine("[GUI] Server exited.");

                if (earlyStartupExit && !_startupPopupShown)
                {
                    _startupPopupShown = true;
                    string startupFailure = BuildStartupEarlyExitMessage(_serverProcess);
                    MessageBox.Show(this, startupFailure, "Server failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else if (unexpectedRuntimeExit)
                {
                    string runtimeFailure = BuildUnexpectedExitMessage(_serverProcess);
                    MessageBox.Show(this, runtimeFailure, "Server exited unexpectedly", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                _stopRequestedByUser = false;
            }));

            try
            {
                _serverProcess.Start();
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
                _serverRunningSinceUtc = DateTime.UtcNow;
                SetRunningState(true);
                AppendLine("[GUI] Server started.");
            }
            catch (Exception ex)
            {
                _startupPopupShown = true;
                string startupFailure = BuildStartupExceptionMessage(psi, ex);
                MessageBox.Show(this, startupFailure, "Server failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetRunningState(false);
            }
            finally
            {
                _isStartInProgress = false;
            }
        }

        private void StopServer()
        {
            if (_serverProcess == null || _serverProcess.HasExited)
                return;

            _serverReady = false;
            _restartQueued = false;
            _restartUnlockUtc = DateTime.MinValue;
            _isStopping = true;
            _stopRequestedByUser = true;
            SetRunningState(true);

            try
            {
                _serverProcess.Kill();
            }
            catch (Exception ex)
            {
                _isStopping = false;
                MessageBox.Show(this, "Failed to stop server: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetRunningState(_serverProcess != null && !_serverProcess.HasExited);
            }
        }

        private void SetRunningState(bool running)
        {
            bool isStarting = running && !_serverReady;
            bool isStopping = _isStopping;
            bool canRestart = running
                && _serverReady
                && !_restartQueued
                && DateTime.UtcNow >= _restartUnlockUtc;

            _startButton.Enabled = true;
            _stopButton.Enabled = true;
            _restartButton.Enabled = true;
            _startButton.BackColor = running ? C_ChromeOff : _accent;
            _stopButton.BackColor = running && !isStopping ? C_Red : C_ChromeOff;
            _restartButton.BackColor = canRestart ? C_Amber : C_ChromeOff;
            _startButton.ForeColor = running ? C_TextDim : Color.White;
            _stopButton.ForeColor = running && !isStopping ? Color.White : C_TextDim;
            _restartButton.ForeColor = canRestart ? Color.White : C_TextDim;
            _commandInput.Enabled = running && !isStarting && !isStopping;
            _sendCommandButton.Enabled = running && !isStarting && !isStopping;
            _clearConsoleButton.Enabled = true;

            string stateText = isStopping
                ? "Stopping..."
                : (isStarting ? "Starting..." : (running ? "Running" : "Stopped"));
            _statusText.Text = stateText;
            _statusDot.BackColor = isStopping || isStarting
                ? C_Amber
                : (running ? C_Green : C_Idle);
            _statusText.ForeColor = running ? C_Text : C_TextDim;

            _playersChipText.Text = "Players  " + (_maxPlayers > 0
                ? $"{Math.Max(0, _connectedPlayers)}/{_maxPlayers}"
                : Math.Max(0, _connectedPlayers) + "/?");
            _playersChipText.ForeColor = running ? C_Text : C_TextDim;

            _uptimeChipText.Text = "Uptime  " + (running ? GetUptimeText() : "--:--:--");
            _uptimeChipText.ForeColor = running ? C_Text : C_TextDim;

            _tpsChipText.Text = "TPS  " + (running && _serverReady ? _currentTpsText : "--");
            double tpsNum;
            _tpsChipText.ForeColor = running && _serverReady
                && double.TryParse(_currentTpsText, out tpsNum)
                    ? (tpsNum >= 50 ? C_Green : (tpsNum >= 30 ? C_Amber : C_Red))
                    : C_TextDim;

            string memText = "--";
            if (IsServerProcessRunning())
            {
                try
                {
                    _serverProcess.Refresh();
                    memText = (_serverProcess.WorkingSet64 / (1024.0 * 1024.0)).ToString("0") + " MB";
                }
                catch
                {
                }
            }
            _memChipText.Text = "MEM  " + memText;
            _memChipText.ForeColor = running ? C_Text : C_TextDim;

            UpdateRestartCountdownText(running, canRestart);
            UpdatePlayersEmptyState();
            UpdateWindowCloseButtonState(running);
        }

        private string GetUptimeText()
        {
            if (_serverRunningSinceUtc == DateTime.MinValue)
                return "00:00:00";

            TimeSpan uptime = DateTime.UtcNow - _serverRunningSinceUtc;
            if (uptime < TimeSpan.Zero)
                uptime = TimeSpan.Zero;

            int totalHours = (int)uptime.TotalHours;
            return $"{totalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
        }

        private void UpdateWindowCloseButtonState(bool running)
        {
            if (!IsHandleCreated)
                return;

            IntPtr menu = GetSystemMenu(Handle, false);
            if (menu == IntPtr.Zero)
                return;

            uint state = running ? MF_BYCOMMAND | MF_GRAYED : MF_BYCOMMAND | MF_ENABLED;
            EnableMenuItem(menu, SC_CLOSE, state);
            DrawMenuBar(Handle);
        }

        private void UpdateRestartCountdownText(bool running, bool canRestart)
        {
            if (!running || canRestart || !_serverReady || _restartQueued)
            {
                _restartButton.Text = "Restart";
                return;
            }

            double remainingSeconds = (_restartUnlockUtc - DateTime.UtcNow).TotalSeconds;
            if (remainingSeconds <= 0)
            {
                _restartButton.Text = "Restart";
                return;
            }

            int displaySeconds = (int)Math.Ceiling(remainingSeconds);
            _restartButton.Text = "Restart (" + displaySeconds + "s)";
        }

        // ================= console =================

        private void AppendLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            _pendingConsoleLines.Enqueue(text);
        }

        private void FlushPendingConsoleLines()
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            int processed = 0;
            int appended = 0;
            var sb = new StringBuilder();

            while (processed < ConsoleFlushBatchSize && _pendingConsoleLines.TryDequeue(out string line))
            {
                string safeLine = NormalizeLogLine(line);
                SafeProcessPlayerCountLine(safeLine);

                if (_consoleRenderingPaused)
                {
                    _suppressedConsoleLineCount++;
                }
                else
                {
                    sb.AppendLine(PrepareDisplayLine(safeLine));
                    appended++;
                }

                processed++;
            }

            if (processed == 0)
                return;

            if (_consoleRenderingPaused)
                return;

            if (appended == 0)
                return;

            TryAppendConsoleBatch(sb.ToString(), appended);
        }

        private static Color GetConsoleLineColor(string line, Color defaultColor)
        {
            if (line.IndexOf("[ERR]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("Exception", StringComparison.OrdinalIgnoreCase) >= 0)
                return Color.FromArgb(255, 110, 110);

            if (line.IndexOf("violation", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[Trust]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[Security]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("kick", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[Player Enforcement]", StringComparison.OrdinalIgnoreCase) >= 0)
                return Color.FromArgb(240, 178, 80);

            if (line.IndexOf("[GUI]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[SteamLobby]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[Steam Lobby]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[StatusChanged]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[Status Changed]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[ConnectionApproval]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[Connection Approval]", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[Handshake]", StringComparison.OrdinalIgnoreCase) >= 0)
                return Color.FromArgb(140, 160, 195);

            return defaultColor;
        }

        private void TryAppendConsoleBatch(string batchText, int appendedCount)
        {
            try
            {
                bool shouldAutoScroll = _output.SelectionStart >= Math.Max(0, _output.TextLength - 2);

                _output.SuspendLayout();
                TrimConsoleIfNeeded();

                _displayedConsoleLineCount += appendedCount;
                if (_displayedConsoleLineCount >= ConsoleWarningLineThreshold && !_consoleSafetyWarningShown)
                {
                    _consoleSafetyWarningShown = true;
                    ShowToast("Console output is getting very large. Older lines will be auto-trimmed to prevent UI crashes.");
                }

                if (_displayedConsoleLineCount >= ConsoleRenderPauseThreshold)
                {
                    _consoleRenderingPaused = true;
                    if (!_consolePauseToastShown)
                    {
                        _consolePauseToastShown = true;
                        StartConsoleAutoClearCountdown();
                    }
                    _output.ResumeLayout();
                    return;
                }

                TrimConsoleByLinesIfNeeded();

                if (appendedCount <= 60)
                {
                    foreach (string line in batchText.Split(new[] { '\n' }, StringSplitOptions.None))
                    {
                        if (line.Length == 0)
                            continue;

                        string display = line.EndsWith("\r", StringComparison.Ordinal)
                            ? line.Substring(0, line.Length - 1)
                            : line;
                        _output.SelectionStart = _output.TextLength;
                        _output.SelectionLength = 0;
                        _output.SelectionColor = GetConsoleLineColor(display, _consoleText);
                        _output.AppendText(display + "\n");
                    }
                }
                else
                {
                    _output.SelectionStart = _output.TextLength;
                    _output.SelectionLength = 0;
                    _output.SelectionColor = _consoleText;
                    _output.AppendText(batchText);
                }

                _output.SelectionColor = _consoleText;

                if (shouldAutoScroll && !_scrollPaused)
                {
                    _consoleAutoScrollFlushCounter++;
                    bool shouldScrollNow = _consoleAutoScrollFlushCounter >= ConsoleScrollEveryFlushes
                        || appendedCount < ConsoleFlushBatchSize;
                    if (shouldScrollNow)
                    {
                        _output.SelectionStart = _output.TextLength;
                        _output.ScrollToCaret();
                        _consoleAutoScrollFlushCounter = 0;
                    }
                }
                else
                {
                    _consoleAutoScrollFlushCounter = 0;
                }

                _output.ResumeLayout();
            }
            catch (Exception ex)
            {
                try
                {
                    _output.ResumeLayout();
                }
                catch
                {
                }

                _consoleAutoScrollFlushCounter = 0;
                Debug.WriteLine("[GUI] Console append warning: " + ex.Message);
            }
        }

        private void TrimConsoleIfNeeded()
        {
            if (_output.TextLength <= MaxConsoleTextLength)
                return;

            int removeChars = _output.TextLength - ConsoleTrimTargetLength;
            if (removeChars <= 0)
                return;

            _output.Select(0, removeChars);
            _output.SelectedText = string.Empty;
            RecountDisplayedConsoleLines();
        }

        private void TrimConsoleByLinesIfNeeded()
        {
            if (_displayedConsoleLineCount <= MaxDisplayedConsoleLines)
                return;

            int linesToRemove = _displayedConsoleLineCount - ConsoleLineTrimTarget;
            if (linesToRemove <= 0)
                return;

            int removeChars = _output.GetFirstCharIndexFromLine(linesToRemove);
            if (removeChars <= 0)
                return;

            _output.Select(0, removeChars);
            _output.SelectedText = string.Empty;
            RecountDisplayedConsoleLines();
        }

        private void RecountDisplayedConsoleLines()
        {
            int textLength = _output.TextLength;
            if (textLength == 0)
            {
                _displayedConsoleLineCount = 0;
                _consoleSafetyWarningShown = false;
                return;
            }

            _displayedConsoleLineCount = _output.GetLineFromCharIndex(Math.Max(0, textLength - 1)) + 1;
            if (_displayedConsoleLineCount < ConsoleWarningLineThreshold)
                _consoleSafetyWarningShown = false;
        }

        private void ClearConsoleOutput()
        {
            ClearConsoleOutput("Console cleared. Rendering resumed.");
        }

        private void ClearConsoleOutput(string toastMessage)
        {
            _output.Clear();
            _displayedConsoleLineCount = 0;
            _consoleSafetyWarningShown = false;
            _consoleRenderingPaused = false;
            _consolePauseToastShown = false;
            _suppressedConsoleLineCount = 0;
            _consoleAutoClearRemainingSeconds = 0;
            _consoleAutoClearTimer.Stop();
            ShowToast(toastMessage);
        }

        private void StartConsoleAutoClearCountdown()
        {
            _consoleAutoClearRemainingSeconds = ConsoleAutoClearCountdownSeconds;
            ShowToast("Console rendering paused for safety. Auto-clearing in " + _consoleAutoClearRemainingSeconds + "s...");
            _consoleAutoClearTimer.Stop();
            _consoleAutoClearTimer.Start();
        }

        private void ShowToast(string message)
        {
            _toastBanner.Text = message;
            _toastBanner.Visible = true;
            _toastBanner.BringToFront();
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private bool IsServerProcessRunning()
        {
            if (_serverProcess == null)
                return false;

            try
            {
                return !_serverProcess.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private void NotifyAlreadyRunningServer()
        {
            AppendLine("[GUI] You already have a server running.");
            ShowToast("You already have a server running.");
        }

        private void SafeProcessPlayerCountLine(string line)
        {
            try
            {
                ProcessPlayerCountLine(line);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GUI] Log parse warning: " + ex.Message + " | line='" + PreviewLine(line) + "'");
            }
        }

        private static string NormalizeLogLine(string line)
        {
            if (line == null)
                return string.Empty;

            string normalized = line.Replace('\0', ' ');
            if (normalized.Length > ParserSafeLineLength)
                normalized = normalized.Substring(0, ParserSafeLineLength) + " ... [truncated]";

            return normalized;
        }

        private static string PrepareDisplayLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            if (line.Length <= DisplaySafeLineLength)
                return line;

            return line.Substring(0, DisplaySafeLineLength) + " ... [display-truncated]";
        }

        private static string PreviewLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            const int maxPreviewLength = 180;
            if (line.Length <= maxPreviewLength)
                return line;

            return line.Substring(0, maxPreviewLength) + "...";
        }

        // FormatLogMessage humanizes these prefixes at the output boundary; all
        // GUI parsers key on the raw emitted forms, so reverse them here first.
        private static readonly (string Spaced, string Raw)[] LogPrefixNormalizeMap =
        {
            ("[Status Changed]", "[StatusChanged]"),
            ("[Host Message]", "[HostMsg]"),
            ("[Connection Approval]", "[ConnectionApproval]"),
            ("[Player Enforcement]", "[PlayerEnforcement]"),
            ("[Combat Evidence]", "[CombatEvidence]"),
            ("[Shadow Combat]", "[ShadowCombat]"),
            ("[Assembly Resolve]", "[AssemblyResolve]"),
            ("[Main Loop]", "[MainLoop]"),
            ("[Steam Lobby]", "[SteamLobby]"),
        };

        private static string NormalizeLogPrefix(string line)
        {
            if (string.IsNullOrEmpty(line))
                return line;
            // Label sits right after the "[yyyy-MM-dd HH:mm:ss] " stamp (~22 chars);
            // bound the search so text later in the line is never rewritten.
            int scanEnd = Math.Min(line.Length, 64);
            foreach ((string spaced, string raw) in LogPrefixNormalizeMap)
            {
                int at = line.IndexOf(spaced, 0, scanEnd, StringComparison.Ordinal);
                if (at >= 0)
                    return line.Substring(0, at) + raw + line.Substring(at + spaced.Length);
            }
            return line;
        }

        private void ProcessPlayerCountLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            line = NormalizeLogPrefix(line);

            if (line.IndexOf("[World] World handler initialized.", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _serverReady = true;
                _restartQueued = false;
                if (!_hasSeenFirstReadyCycle)
                {
                    _hasSeenFirstReadyCycle = true;
                    _restartUnlockUtc = DateTime.UtcNow;
                }
                else
                {
                    int restartDelaySeconds = SelectNextRestartDelaySeconds();
                    _restartUnlockUtc = DateTime.UtcNow.AddSeconds(restartDelaySeconds);
                    AppendLine("[GUI] Restart unlock in " + restartDelaySeconds + "s.");
                }

                SetRunningState(_serverProcess != null && !_serverProcess.HasExited);
            }

            const string maxPlayersPrefix = "MaxPlayers     :";
            int maxIndex = line.IndexOf(maxPlayersPrefix, StringComparison.OrdinalIgnoreCase);
            if (maxIndex >= 0)
            {
                string valueText = line.Substring(maxIndex + maxPlayersPrefix.Length).Trim();
                if (int.TryParse(valueText, out int maxPlayers) && maxPlayers > 0)
                {
                    _maxPlayers = maxPlayers;
                    SetRunningState(_serverProcess != null && !_serverProcess.HasExited);
                }

                return;
            }

            if (line.IndexOf("[StatusChanged] Peer connected.", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (TryAddConnectedPlayer(line))
                    _connectedPlayers++;
                EnsurePlayerCardsMatchCount();
                SetRunningState(_serverProcess != null && !_serverProcess.HasExited);
                return;
            }

            if (line.IndexOf("[StatusChanged] Peer disconnected.", StringComparison.OrdinalIgnoreCase) >= 0
                || line.IndexOf("[StatusChanged] Pending/untracked peer disconnected.", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (TryRemoveDisconnectedPlayer(line))
                    _connectedPlayers = Math.Max(0, _connectedPlayers - 1);
                EnsurePlayerCardsMatchCount();
                SetRunningState(_serverProcess != null && !_serverProcess.HasExited);
                return;
            }

            TryTrackApprovedPlayerName(line);
            TryResolvePlayerFromHandshake(line);
            TryResolvePlayerFromHostMessage(line);
        }

        private int SelectNextRestartDelaySeconds()
        {
            const int shortDelay = 5;
            const int longDelay = 10;

            double shortDelayWeight = 0.5;
            if (_lastRestartDelaySeconds == shortDelay)
            {
                shortDelayWeight = _restartDelayStreak >= 2 ? 0.2 : 0.35;
            }
            else if (_lastRestartDelaySeconds == longDelay)
            {
                shortDelayWeight = _restartDelayStreak >= 2 ? 0.8 : 0.65;
            }

            int nextDelay = RestartDelayRandom.NextDouble() < shortDelayWeight ? shortDelay : longDelay;

            if (nextDelay == _lastRestartDelaySeconds)
                _restartDelayStreak++;
            else
                _restartDelayStreak = 1;

            _lastRestartDelaySeconds = nextDelay;
            return nextDelay;
        }

        private void TrackStartupErrorLine(string line)
        {
            if (_serverReady || DateTime.UtcNow - _lastStartAttemptUtc > StartupFailureWindow)
                return;

            if (_startupErrorLines.Count >= StartupErrorLineLimit)
                _startupErrorLines.Dequeue();

            _startupErrorLines.Enqueue(line.Trim());
        }

        private void TrackRecentErrorLine(string line)
        {
            if (_recentErrorLines.Count >= RuntimeErrorLineLimit)
                _recentErrorLines.Dequeue();

            _recentErrorLines.Enqueue(line.Trim());
        }

        private string BuildStartupExceptionMessage(ProcessStartInfo psi, Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The server process could not be launched.");
            sb.AppendLine();
            sb.AppendLine("Reason:");
            sb.AppendLine($"- {ex.GetType()}: {ex.Message}");
            sb.AppendLine();
            sb.AppendLine("Launch details:");
            sb.AppendLine($"- File: {psi.FileName}");
            sb.AppendLine($"- Arguments: {psi.Arguments}");
            sb.AppendLine($"- WorkingDirectory: {psi.WorkingDirectory}");
            sb.AppendLine();
            sb.AppendLine("Check that:");
            sb.AppendLine("- The executable exists and is not locked by another process.");
            sb.AppendLine("- The working directory is valid.");
            sb.AppendLine("- Antivirus or security software is not blocking launch.");
            return sb.ToString();
        }

        private string BuildStartupEarlyExitMessage(Process process)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The server started but exited before it became ready.");

            try
            {
                sb.AppendLine($"- ExitCode: {process.ExitCode}");
            }
            catch
            {
            }

            if (_startupErrorLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Recent startup errors:");
                foreach (string line in _startupErrorLines)
                    sb.AppendLine("- " + line);
            }

            sb.AppendLine();
            sb.AppendLine("Check `server.properties`, plugin configs, and game-path dependencies.");
            return sb.ToString();
        }

        private string BuildUnexpectedExitMessage(Process process)
        {
            var sb = new StringBuilder();
            sb.AppendLine("The server process exited unexpectedly while running.");

            try
            {
                sb.AppendLine($"- ExitCode: {process.ExitCode}");
            }
            catch
            {
            }

            if (_recentErrorLines.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Recent error output:");
                foreach (string line in _recentErrorLines)
                    sb.AppendLine("- " + line);
            }

            sb.AppendLine();
            sb.AppendLine("Check recent console output, plugin changes, and config edits.");
            return sb.ToString();
        }

        private void TryTrackApprovedPlayerName(string line)
        {
            Match m = Regex.Match(
                line,
                @"\[(?:ConnectionApproval|Approve)\]\s+Accepted(?:\s+pending\s+Steam\s+join\s+for)?\s+'?(?<name>.+?)'?\s+\((?<steam>\d+)\)\."
            );

            if (!m.Success)
                return;

            if (!ulong.TryParse(m.Groups["steam"].Value, out ulong steamId))
                return;

            string name = m.Groups["name"].Value.Trim();
            if (name.Length == 0)
                return;

            _pendingNamesBySteamId[steamId] = name;
            PromotePlaceholderToKnownPlayer("-", name, steamId.ToString());
        }

        private void TryResolvePlayerFromHandshake(string line)
        {
            Match m = Regex.Match(
                line,
                @"\[Handshake\]\s+ResponseToConnection\s+sent\s+to\s+(?<name>.+?)\s+\((?<steam>\d+)\)\s+with\s+GID\s+(?<gid>\d+)\."
            );

            if (!m.Success)
                return;

            if (!ulong.TryParse(m.Groups["steam"].Value, out ulong steamId))
                return;

            if (!byte.TryParse(m.Groups["gid"].Value, out byte gid))
                return;

            string name = m.Groups["name"].Value.Trim();
            if (name.Length == 0)
                return;

            _pendingNamesBySteamId[steamId] = name;
            _steamIdsByGid[gid] = steamId;
            UpsertPlayerCard(gid.ToString(), name, steamId.ToString());
        }

        private void TryResolvePlayerFromHostMessage(string line)
        {
            Match m = Regex.Match(
                line,
                @"\[HostMsg\]\s+PlayerExistsMessage\s+from\s+player\s+(?<gid>\d+),\s+gamertag='(?<name>[^']+)'"
            );

            if (!m.Success)
                return;

            if (!byte.TryParse(m.Groups["gid"].Value, out byte gid))
                return;

            string name = m.Groups["name"].Value.Trim();
            if (name.Length == 0)
                return;

            if (!_steamIdsByGid.TryGetValue(gid, out ulong steamId))
                return;

            _pendingNamesBySteamId[steamId] = name;
            UpsertPlayerCard(gid.ToString(), name, steamId.ToString());
        }

        private bool TryAddConnectedPlayer(string line)
        {
            Match m = Regex.Match(
                line,
                @"\[StatusChanged\]\s+Peer connected\.\s+SteamID=(?<steam>\d+),\s+GID=(?<gid>\d+)"
            );

            if (!m.Success)
            {
                return TryAddConnectedPlayerFallback();
            }

            if (!ulong.TryParse(m.Groups["steam"].Value, out ulong steamId))
                return false;

            if (!byte.TryParse(m.Groups["gid"].Value, out byte gid))
                return false;

            _steamIdsByGid[gid] = steamId;

            string steamText = steamId.ToString();
            foreach (Control control in _playersList.Controls)
            {
                if (string.Equals(control.Tag as string, steamText, StringComparison.Ordinal))
                    return false;
            }

            string name = _pendingNamesBySteamId.TryGetValue(steamId, out string cachedName)
                ? cachedName
                : "Unknown";

            UpsertPlayerCard(gid.ToString(), name, steamText);
            return true;
        }

        private bool TryAddConnectedPlayerFallback()
        {
            foreach (KeyValuePair<ulong, string> pendingPlayer in _pendingNamesBySteamId)
            {
                string steamText = pendingPlayer.Key.ToString();
                bool alreadyVisible = _playersList.Controls
                    .Cast<Control>()
                    .Any(control => string.Equals(control.Tag as string, steamText, StringComparison.Ordinal));

                if (alreadyVisible)
                    continue;

                UpsertPlayerCard("-", pendingPlayer.Value, steamText);
                return true;
            }

            return false;
        }

        private bool TryRemoveDisconnectedPlayer(string line)
        {
            Match m = Regex.Match(
                line,
                @"\[StatusChanged\]\s+Peer disconnected\.\s+SteamID=(?<steam>\d+),\s+GID=(?<gid>\d+)"
            );

            if (!m.Success)
            {
                Match pendingDisconnectMatch = Regex.Match(
                    line,
                    @"\[StatusChanged\]\s+Pending/untracked peer disconnected\.\s+SteamID=(?<steam>\d+)"
                );

                if (pendingDisconnectMatch.Success)
                {
                    if (!ulong.TryParse(pendingDisconnectMatch.Groups["steam"].Value, out ulong pendingSteamId))
                        return false;

                    return RemovePlayerRowBySteamId(pendingSteamId.ToString());
                }

                return TryRemoveDisconnectedPlayerFallback();
            }

            if (!ulong.TryParse(m.Groups["steam"].Value, out ulong steamId))
                return false;

            if (byte.TryParse(m.Groups["gid"].Value, out byte gid))
                _steamIdsByGid.Remove(gid);

            return RemovePlayerRowBySteamId(steamId.ToString());
        }

        private bool RemovePlayerRowBySteamId(string steamText)
        {
            if (string.IsNullOrEmpty(steamText))
                return false;

            _connectTimesUtcBySteamId.Remove(steamText);
            if (ulong.TryParse(steamText, out ulong steamId))
            {
                _pendingNamesBySteamId.Remove(steamId);
                foreach (byte gid in _steamIdsByGid.Where(pair => pair.Value == steamId).Select(pair => pair.Key).ToList())
                {
                    _steamIdsByGid.Remove(gid);
                }
            }

            bool removed = false;
            _playersList.SuspendLayout();
            for (int i = _playersList.Controls.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_playersList.Controls[i].Tag as string, steamText, StringComparison.Ordinal))
                {
                    _playersList.Controls.RemoveAt(i);
                    removed = true;
                }
            }

            ResizePlayerCards();
            _playersList.ResumeLayout(true);
            _playersList.PerformLayout();
            _playersList.Invalidate();
            UpdatePlayersEmptyState();
            return removed;
        }

        private bool TryRemoveDisconnectedPlayerFallback()
        {
            if (_playersList.Controls.Count == 0)
                return false;

            _playersList.SuspendLayout();
            _playersList.Controls.RemoveAt(0);
            ResizePlayerCards();
            _playersList.ResumeLayout(true);
            _playersList.PerformLayout();
            _playersList.Invalidate();
            UpdatePlayersEmptyState();
            return true;
        }

        private Panel CreatePlayerCard(string idText, string name, string steamText)
        {
            bool isPlaceholder = steamText.StartsWith("placeholder-", StringComparison.Ordinal);
            var row = new Panel
            {
                Width = Math.Max(540, _playersList.ClientSize.Width - 18),
                Height = 56,
                Margin = new Padding(0, 0, 0, 6),
                Padding = new Padding(0),
                BackColor = C_Row,
                Tag = steamText,
                AccessibleName = name,
                Dock = DockStyle.Top
            };

            var summaryRow = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                Padding = new Padding(14, 9, 10, 9),
                BackColor = C_Row
            };

            var accentPanel = new Panel
            {
                Dock = DockStyle.Left,
                Width = 3,
                BackColor = isPlaceholder ? C_Idle : _accent
            };

            var actionsButton = CreatePlayerActionsButton(row, isPlaceholder);

            var idLabel = new Label
            {
                Dock = DockStyle.Left,
                Width = 44,
                Text = idText,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Consolas", 9f, FontStyle.Bold),
                ForeColor = C_TextDim,
                BackColor = Color.Transparent
            };

            var steamLabel = new Label
            {
                Dock = DockStyle.Right,
                Width = 190,
                Text = isPlaceholder ? "waiting..." : steamText,
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Consolas", 9f, FontStyle.Regular),
                ForeColor = C_TextDim,
                BackColor = Color.Transparent
            };

            var nameLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = name,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = C_Text,
                BackColor = Color.Transparent
            };

            summaryRow.Controls.Add(nameLabel);
            summaryRow.Controls.Add(steamLabel);
            summaryRow.Controls.Add(actionsButton);
            summaryRow.Controls.Add(idLabel);
            summaryRow.Controls.Add(accentPanel);

            ContextMenuStrip menu = null;
            if (!isPlaceholder)
            {
                row.Controls.Add(CreatePlayerDetailsPanel(row));
                menu = CreatePlayerContextMenu(row);
                row.ContextMenuStrip = menu;
                summaryRow.ContextMenuStrip = menu;
                nameLabel.ContextMenuStrip = menu;
                steamLabel.ContextMenuStrip = menu;
                idLabel.ContextMenuStrip = menu;
            }

            row.Controls.Add(summaryRow);

            return row;
        }

        private ContextMenuStrip CreatePlayerContextMenu(Panel row)
        {
            var menu = new ContextMenuStrip
            {
                BackColor = C_Card,
                ForeColor = C_Text,
                ShowImageMargin = false,
                Renderer = new DarkMenuRenderer()
            };

            menu.Items.Add("Copy name", null, (_, __) =>
            {
                string playerName = row.AccessibleName ?? string.Empty;
                if (playerName.Length > 0)
                    Clipboard.SetText(playerName);
            });
            menu.Items.Add("Copy SteamID", null, (_, __) =>
            {
                string tag = row.Tag as string ?? string.Empty;
                if (tag.Length > 0)
                    Clipboard.SetText(tag);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Kick", null, (_, __) =>
            {
                string playerName = row.AccessibleName ?? "player";
                SendCommandInternal("kick \"" + playerName.Replace("\"", string.Empty) + "\"");
            });
            menu.Items.Add("Ban", null, (_, __) =>
            {
                string playerName = row.AccessibleName ?? "player";
                SendCommandInternal("ban \"" + playerName.Replace("\"", string.Empty) + "\"");
            });
            menu.Items.Add(new ToolStripSeparator());

            var rankMenu = new ToolStripMenuItem("Set rank");
            foreach (string rank in new[] { "Visitor", "Member", "Moderator", "Admin" })
            {
                string captured = rank;
                rankMenu.DropDownItems.Add(rank, null, (_, __) =>
                {
                    string steamText = row.Tag as string ?? string.Empty;
                    if (steamText.Length > 0 && !steamText.StartsWith("placeholder-", StringComparison.Ordinal))
                        SendCommandInternal("setrank " + steamText + " " + captured);
                });
            }
            menu.Items.Add(rankMenu);
            return menu;
        }

        private void UpsertPlayerCard(string idText, string name, string steamText)
        {
            _playersList.SuspendLayout();

            for (int i = _playersList.Controls.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_playersList.Controls[i].Tag as string, steamText, StringComparison.Ordinal))
                    _playersList.Controls.RemoveAt(i);
            }

            RemoveFirstPlaceholderCard();

            if (!steamText.StartsWith("placeholder-", StringComparison.Ordinal))
                _connectTimesUtcBySteamId[steamText] = DateTime.UtcNow;

            Panel row = CreatePlayerCard(idText, name, steamText);
            _playersList.Controls.Add(row);
            _playersList.Controls.SetChildIndex(row, 0);
            RemoveExtraPlaceholderCards();
            ResizePlayerCards();
            ApplyPlayersFilter();
            _playersList.ResumeLayout(true);
            _playersList.Invalidate();
            UpdatePlayersEmptyState();
        }

        private void PromotePlaceholderToKnownPlayer(string idText, string name, string steamText)
        {
            bool alreadyVisible = _playersList.Controls
                .Cast<Control>()
                .Any(control => string.Equals(control.Tag as string, steamText, StringComparison.Ordinal));

            if (alreadyVisible)
                return;

            for (int i = 0; i < _playersList.Controls.Count; i++)
            {
                string tag = _playersList.Controls[i].Tag as string;
                if (tag != null && tag.StartsWith("placeholder-", StringComparison.Ordinal))
                {
                    _playersList.SuspendLayout();
                    _playersList.Controls.RemoveAt(i);
                    Panel row = CreatePlayerCard(idText, name, steamText);
                    _playersList.Controls.Add(row);
                    _playersList.Controls.SetChildIndex(row, i);
                    ResizePlayerCards();
                    ApplyPlayersFilter();
                    _playersList.ResumeLayout(true);
                    _playersList.Invalidate();
                    UpdatePlayersEmptyState();
                    return;
                }
            }
        }

        private Panel CreatePlayerDetailsPanel(Panel row)
        {
            var detailsPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 118,
                Padding = new Padding(18, 8, 14, 8),
                BackColor = C_RowDeep,
                Visible = false,
                Tag = "details"
            };

            var infoLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 66,
                Text = "Player info: expand to load",
                TextAlign = ContentAlignment.TopLeft,
                Font = new Font("Consolas", 8.5f, FontStyle.Regular),
                ForeColor = C_TextDim,
                BackColor = Color.Transparent,
                Tag = "pinfo",
                AutoEllipsis = true
            };

            var bottomRow = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 36,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            var buttonsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = false,
                BackColor = Color.Transparent,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            var copyNameButton = CreatePlayerDetailButton("Copy Name", false, (_, __) =>
            {
                string playerName = row.AccessibleName ?? string.Empty;
                if (playerName.Length > 0)
                    Clipboard.SetText(playerName);
            });

            var copyButton = CreatePlayerDetailButton("Copy SteamID", false, (_, __) =>
            {
                Clipboard.SetText(row.Tag as string ?? string.Empty);
            });

            var kickButton = CreatePlayerDetailButton("Kick", false, (_, __) =>
            {
                string playerName = row.AccessibleName ?? "player";
                SendCommandInternal("kick \"" + playerName.Replace("\"", string.Empty) + "\"");
            });

            var banButton = CreatePlayerDetailButton("Ban", true, (_, __) =>
            {
                string playerName = row.AccessibleName ?? "player";
                SendCommandInternal("ban \"" + playerName.Replace("\"", string.Empty) + "\"");
            });

            var refreshButton = CreatePlayerDetailButton("Refresh", false, (_, __) =>
            {
                string steamText = row.Tag as string ?? string.Empty;
                if (steamText.Length > 0 && !steamText.StartsWith("placeholder-", StringComparison.Ordinal))
                    SendCommandInternal("@@PINFO " + steamText);
            });

            buttonsPanel.Controls.Add(copyNameButton);
            buttonsPanel.Controls.Add(copyButton);
            buttonsPanel.Controls.Add(kickButton);
            buttonsPanel.Controls.Add(banButton);
            buttonsPanel.Controls.Add(refreshButton);

            var sessionLabel = new Label
            {
                Dock = DockStyle.Right,
                Width = 230,
                Text = "",
                TextAlign = ContentAlignment.MiddleRight,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                ForeColor = C_TextDim,
                BackColor = Color.Transparent,
                Tag = "session"
            };
            bottomRow.Controls.Add(buttonsPanel);
            bottomRow.Controls.Add(sessionLabel);
            buttonsPanel.BringToFront();
            detailsPanel.Controls.Add(bottomRow);
            detailsPanel.Controls.Add(infoLabel);
            return detailsPanel;
        }

        private Button CreatePlayerDetailButton(string text, bool danger, EventHandler onClick)
        {
            var button = new Button
            {
                Height = 32,
                Width = text.StartsWith("Copy", StringComparison.Ordinal) ? 112 : 78,
                Margin = new Padding(0, 0, 8, 0),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
                BackColor = danger ? Color.FromArgb(130, 48, 40) : C_ChromeOff,
                ForeColor = Color.White,
                Text = text,
                Cursor = Cursors.Hand
            };

            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = danger ? C_Red : C_ChromeOffHot;
            button.FlatAppearance.MouseDownBackColor = danger ? Color.FromArgb(180, 60, 48) : Color.FromArgb(34, 44, 66);
            button.Click += onClick;
            return button;
        }

        private Button CreatePlayerActionsButton(Panel row, bool isPlaceholder)
        {
            var button = new Button
            {
                Dock = DockStyle.Right,
                Width = 36,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Regular),
                BackColor = C_Row,
                ForeColor = isPlaceholder ? C_TextFaint : C_TextDim,
                Text = "▾",
                Enabled = !isPlaceholder,
                Tag = row,
                Cursor = Cursors.Hand
            };

            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseDownBackColor = C_Row;
            button.FlatAppearance.MouseOverBackColor = C_Row;
            button.Click += PlayerActionsButton_Click;
            return button;
        }

        private void PlayerActionsButton_Click(object sender, EventArgs e)
        {
            Button button = sender as Button;
            if (button == null)
                return;

            Panel row = button.Tag as Panel;
            if (row == null)
                return;

            Panel detailsPanel = null;
            foreach (Control control in row.Controls)
            {
                if (string.Equals(control.Tag as string, "details", StringComparison.Ordinal))
                {
                    detailsPanel = control as Panel;
                    break;
                }
            }

            if (detailsPanel == null)
                return;

            bool willExpand = !detailsPanel.Visible;
            detailsPanel.Visible = willExpand;
            row.Height = willExpand ? 174 : 56;
            button.Text = willExpand ? "▴" : "▾";

            if (willExpand)
            {
                string steamText = row.Tag as string ?? string.Empty;
                RefreshPlayerSessionLabel(row, steamText);
                RefreshPlayerInfoLabel(row);
                if (steamText.Length > 0 && !steamText.StartsWith("placeholder-", StringComparison.Ordinal))
                    SendCommandInternal("@@PINFO " + steamText);
            }
            ResizePlayerCards();
            if (_playersList.Controls.Count < LargePlayerListThreshold)
                _playersList.PerformLayout();
            _playersList.Invalidate();
        }

        private static Control FindPlayerDetailChild(Control root, string tag)
        {
            foreach (Control child in root.Controls)
            {
                if (string.Equals(child.Tag as string, tag, StringComparison.Ordinal))
                    return child;
                Control found = FindPlayerDetailChild(child, tag);
                if (found != null)
                    return found;
            }
            return null;
        }

        // Reconciles the cards against the server's authoritative roster
        // (@@PLAYERS). Adds missing cards, removes every card whose steamid is
        // no longer connected — including placeholders whose join never
        // finished — so the tab can never drift from reality.
        private void ReconcilePlayerCards(string roster)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string entry in roster.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = entry.Split('|');
                if (parts.Length < 3)
                    continue;
                string gid = parts[0].Trim();
                string name = parts[1].Trim();
                string steamText = parts[2].Trim();
                if (steamText.Length == 0)
                    continue;
                seen.Add(steamText);

                bool exists = _playersList.Controls
                    .Cast<Control>()
                    .Any(c => string.Equals(c.Tag as string, steamText, StringComparison.Ordinal));
                if (!exists)
                    UpsertPlayerCard(gid, name.Length > 0 ? name : "Unknown", steamText);
            }

            _connectedPlayers = seen.Count;

            _playersList.SuspendLayout();
            for (int i = _playersList.Controls.Count - 1; i >= 0; i--)
            {
                string tag = _playersList.Controls[i].Tag as string;
                if (tag == null || !seen.Contains(tag))
                    _playersList.Controls.RemoveAt(i);
            }
            _playersList.ResumeLayout(true);
            ApplyPlayersFilter();
            UpdatePlayersEmptyState();
            SetRunningState(_serverProcess != null && !_serverProcess.HasExited);
        }

        private int _playersHeartbeatTicks;

        // 500ms heartbeat: roster re-poll every 4th tick (~2s); expanded cards
        // get a fresh @@PINFO every tick so Position/Traffic/Inventory track
        // live, and the "Online for" label updates in place.
        private void PlayersHeartbeat_Tick(object sender, EventArgs e)
        {
            if (_serverProcess == null || _serverProcess.HasExited)
                return;

            if (++_playersHeartbeatTicks % 4 == 0)
                SendCommandInternal("@@PLAYERS");

            foreach (Control control in _playersList.Controls)
            {
                if (!(control is Panel row))
                    continue;
                string tag = row.Tag as string ?? string.Empty;
                if (tag.Length == 0 || tag.StartsWith("placeholder-", StringComparison.Ordinal))
                    continue;
                Control details = row.Controls
                    .Cast<Control>()
                    .FirstOrDefault(c => string.Equals(c.Tag as string, "details", StringComparison.Ordinal));
                if (details == null || !details.Visible)
                    continue;
                SendCommandInternal("@@PINFO " + tag);
                RefreshPlayerSessionLabel(row, tag);
            }
        }

        private void RefreshPlayerSessionLabel(Panel row, string steamText)
        {
            Control sessionLabel = FindPlayerDetailChild(row, "session");
            if (sessionLabel == null)
                return;
            string text = "SteamID  " + steamText;
            if (_connectTimesUtcBySteamId.TryGetValue(steamText, out DateTime connectedUtc))
            {
                TimeSpan session = DateTime.UtcNow - connectedUtc;
                string sessionText = session.TotalHours >= 1
                    ? $"{(int)session.TotalHours}h {session.Minutes}m"
                    : session.TotalMinutes >= 1
                        ? $"{(int)session.TotalMinutes}m {session.Seconds}s"
                        : $"{(int)Math.Max(0, session.TotalSeconds)}s";
                text = "Online for " + sessionText + "   SteamID  " + steamText;
            }
            sessionLabel.Text = text;
        }

        private void HandlePlayerInfoLine(string payload)
        {
            // Format: <steamid>|<name>|<rank>|<announced>|<pos>|<strikes>|<inventory>
            int bar = payload.IndexOf('|');
            if (bar <= 0)
                return;
            string steamText = payload.Substring(0, bar).Trim();
            _playerInfoBySteamId[steamText] = payload.Substring(bar + 1);

            foreach (Control control in _playersList.Controls)
            {
                if (control is Panel row &&
                    string.Equals(row.Tag as string, steamText, StringComparison.Ordinal))
                {
                    RefreshPlayerInfoLabel(row);
                }
            }
        }

        private void RefreshPlayerInfoLabel(Panel row)
        {
            Panel detailsPanel = null;
            foreach (Control control in row.Controls)
            {
                if (string.Equals(control.Tag as string, "details", StringComparison.Ordinal))
                {
                    detailsPanel = control as Panel;
                    break;
                }
            }
            Control infoLabel = detailsPanel != null
                ? FindPlayerDetailChild(detailsPanel, "pinfo")
                : null;
            if (infoLabel == null)
                return;

            string steamText = row.Tag as string ?? string.Empty;
            if (!_playerInfoBySteamId.TryGetValue(steamText, out string data))
            {
                infoLabel.Text = "Player info: requesting from server...";
                return;
            }

            string[] parts = data.Split('|');
            if (parts.Length < 6)
            {
                infoLabel.Text = "Player info: no trust data yet";
                return;
            }

            string rankPart = parts[1] + (parts[2] == "1" ? "" : "  (not announced)");
            string trafficPart = parts.Length > 6 ? parts[6] : "";
            infoLabel.Text =
                "Position  " + parts[3] + "    Rank  " + rankPart + "    Strikes  " + parts[4] + "\r\n" +
                (trafficPart.Length > 0 ? "Packets   " + trafficPart + "\r\n" : "") +
                "Inventory  " + parts[5];
        }

        private void EnsurePlayerCardsMatchCount()
        {
            while (_playersList.Controls.Count < _connectedPlayers)
            {
                _placeholderPlayerNumber++;
                string placeholderTag = "placeholder-" + _placeholderPlayerNumber;
                Panel row = CreatePlayerCard("-", "Player connecting...", placeholderTag);
                _playersList.Controls.Add(row);
                _playersList.Controls.SetChildIndex(row, 0);
            }

            while (_playersList.Controls.Count > _connectedPlayers && _playersList.Controls.Count > 0)
            {
                _playersList.Controls.RemoveAt(0);
            }

            RemoveExtraPlaceholderCards();

            ApplyPlayersFilter();
            CollapseExpandedRowsForLargeLists();
            ResizePlayerCards();
            UpdatePlayersEmptyState();
        }

        private void ApplyPlayersFilter()
        {
            foreach (Control control in _playersList.Controls)
            {
                control.Visible = PlayerMatchesCurrentFilter(control);
            }
        }

        private bool PlayerMatchesCurrentFilter(Control control)
        {
            string filter = (_playersFilterInput.Text ?? string.Empty).Trim();
            if (filter.Length == 0)
                return true;

            string steamText = control.Tag as string ?? string.Empty;
            string name = control.AccessibleName ?? string.Empty;
            return name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || steamText.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void CollapseExpandedRowsForLargeLists()
        {
            if (_playersList.Controls.Count < LargePlayerListThreshold)
                return;

            foreach (Control control in _playersList.Controls)
            {
                Panel row = control as Panel;
                if (row == null)
                    continue;

                foreach (Control child in row.Controls)
                {
                    if (string.Equals(child.Tag as string, "details", StringComparison.Ordinal))
                    {
                        child.Visible = false;
                    }
                    else if (child is Panel summaryRow)
                    {
                        foreach (Control summaryChild in summaryRow.Controls)
                        {
                            if (summaryChild is Button arrowButton)
                                arrowButton.Text = "▾";
                        }
                    }
                }

                if (!((row.Tag as string) ?? string.Empty).StartsWith("placeholder-", StringComparison.Ordinal))
                    row.Height = 56;
            }
        }

        private void RemoveExtraPlaceholderCards()
        {
            while (_playersList.Controls.Count > _connectedPlayers)
            {
                int placeholderIndex = -1;
                for (int i = _playersList.Controls.Count - 1; i >= 0; i--)
                {
                    string tag = _playersList.Controls[i].Tag as string;
                    if (tag != null && tag.StartsWith("placeholder-", StringComparison.Ordinal))
                    {
                        placeholderIndex = i;
                        break;
                    }
                }

                if (placeholderIndex >= 0)
                {
                    _playersList.Controls.RemoveAt(placeholderIndex);
                    continue;
                }

                if (_playersList.Controls.Count > 0)
                {
                    _playersList.Controls.RemoveAt(_playersList.Controls.Count - 1);
                }
            }
        }

        private void RemoveFirstPlaceholderCard()
        {
            for (int i = 0; i < _playersList.Controls.Count; i++)
            {
                string tag = _playersList.Controls[i].Tag as string;
                if (tag != null && tag.StartsWith("placeholder-", StringComparison.Ordinal))
                {
                    _playersList.Controls.RemoveAt(i);
                    return;
                }
            }
        }

        private void UpdatePlayersEmptyState()
        {
            bool hasPlayers = _playersList.Controls.Cast<Control>().Any(PlayerMatchesCurrentFilter);
            _playersEmptyState.Visible = !hasPlayers;
            _playersList.Visible = hasPlayers;
            if (hasPlayers)
                _playersList.BringToFront();
            else
                _playersEmptyState.BringToFront();

            bool running = _serverProcess != null && !_serverProcess.HasExited;
            _playersEmptyState.Text = running
                ? "No players connected yet."
                : "Start the server to see connected players.";

            if ((_playersFilterInput.Text ?? string.Empty).Trim().Length > 0 && !hasPlayers)
            {
                _playersEmptyState.Text = "No players match your search.";
            }

            _playersCountLabel.Text = _playersList.Controls.Count == 1
                ? "1 online"
                : _playersList.Controls.Count + " online";

            if (_playersList.Controls.Count < LargePlayerListThreshold)
                _playersList.PerformLayout();
            _playersList.Invalidate();
        }

        private void ResizePlayerCards()
        {
            int width = Math.Max(540, _playersList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
            foreach (Control control in _playersList.Controls)
            {
                control.Width = width;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    MessageBox.Show(this, "Stop the server first before closing the window.", "Server still running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                _stopRequestedByUser = true;
                try { _serverProcess.Kill(); } catch { }
            }

            base.OnFormClosing(e);
        }
    }
}
