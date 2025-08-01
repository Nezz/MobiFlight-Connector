﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Windows.Forms;
using System.Diagnostics;
#if ARCAZE
#endif
using System.Runtime.InteropServices;
using MobiFlight.FSUIPC;
using System.Reflection;
using MobiFlight.UI.Dialogs;
using MobiFlight.UI.Forms;
using MobiFlight.SimConnectMSFS;
using MobiFlight.UpdateChecker;
using MobiFlight.Base;
using MobiFlight.xplane;
using MobiFlight.ProSim;
using MobiFlight.HubHop;
using System.Threading.Tasks;
using MobiFlight.InputConfig;
using Newtonsoft.Json;
using System.IO;
using MobiFlight.BrowserMessages.Incoming;
using MobiFlight.BrowserMessages;
using MobiFlight.BrowserMessages.Outgoing;
using System.Drawing;
using MobiFlight.BrowserMessages.Incoming.Handler;
using System.ComponentModel;

namespace MobiFlight.UI
{
    public partial class MainForm : Form, IProjectToolbar, INotifyPropertyChanged
    {
        private delegate void UpdateAircraftCallback(string aircraftName);
        private delegate DialogResult MessageBoxDelegate(string msg, string title, MessageBoxButtons buttons, MessageBoxIcon icon);
        private delegate void VoidDelegate();

        private const string fileExtensionLoadFilter = "MobiFlight Files|*.mfproj;*.mcc|MobiFlight Project (*.mfproj)|*.mfproj|MobiFlight Connector Config (*.mcc)|*.mcc|ArcazeUSB Interface Config (*.aic) |*.aic";
        private const string fileExtensionSaveFilter = "MobiFlight Project (*.mfproj)|*.mfproj";
        public static String Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        public static String VersionBeta = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(4);
        public static String Build = new System.IO.FileInfo(System.Reflection.Assembly.GetExecutingAssembly().Location).LastWriteTime.ToString("yyyyMMdd");

        private CmdLineParams cmdLineParams;
        private ExecutionManager execManager;

        protected Dictionary<string, string> AutoLoadConfigs = new Dictionary<string, string>();

        public event EventHandler<string> CurrentFilenameChanged;

        // Track whether there are any connected devices of the different types, to avoid unnecessary
        // array retrievals from execManager.
        private bool hasConnectedJoysticks = false;
        private bool hasConnectedMidiBoards = false;
        private bool hasConnectedModules = false;

        private bool IsMSFSRunning = false;

        public ExecutionManager ExecutionManager
        {
            get { return execManager; }
        }

        private void InitializeUILanguage()
        {
            if (Properties.Settings.Default.Language != "")
            {
                System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(Properties.Settings.Default.Language);
            }
        }

        public bool InitialLookupFinished { get; private set; } = false;
        public bool SettingsDialogActive { get; private set; }

        public event EventHandler<Project> ProjectLoaded;

        private readonly LogAppenderFile logAppenderFile = new LogAppenderFile();

        private int StartupProgressValue = 0;

        private bool projectHasUnsavedChanges = false;
        public bool ProjectHasUnsavedChanges
        {
            get { return projectHasUnsavedChanges; }
            set
            {
                if (value == projectHasUnsavedChanges) return;
                projectHasUnsavedChanges = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(ProjectHasUnsavedChanges))
                );
            }
        }

        private void InitializeLogging()
        {
            LogAppenderLogPanel logAppenderTextBox = new LogAppenderLogPanel(logPanel1);

            Log.Instance.AddAppender(logAppenderTextBox);
            Log.Instance.AddAppender(logAppenderFile);
            Log.Instance.AddAppender(new Base.LogAppender.MessageExchangeAppender());
            Log.Instance.LogJoystickAxis = Properties.Settings.Default.LogJoystickAxis;
            Log.Instance.Enabled = Properties.Settings.Default.LogEnabled;
            logPanel1.Visible = Log.Instance.Enabled;
            logSplitter.Visible = Log.Instance.Enabled;

            try
            {
                Log.Instance.Severity = (LogSeverity)Enum.Parse(typeof(LogSeverity), Properties.Settings.Default.LogLevel, true);
            }
            catch (Exception e)
            {
                Log.Instance.log("Unknown log level.", LogSeverity.Error);
            }
            Log.Instance.log($"MobiFlight version {CurrentVersion()}", LogSeverity.Info);
            Log.Instance.log($"Logger initialized {Log.Instance.Severity}", LogSeverity.Info);
        }

        private static void SetCurrentWorkingDirectory()
        {
            // Get the full path of the executable
            string executablePath = Assembly.GetExecutingAssembly().Location;
            // Extract the directory
            string executableDirectory = Path.GetDirectoryName(executablePath);
            // Set the current directory to the executable's directory
            Directory.SetCurrentDirectory(executableDirectory);
        }

        private void InitializeSettings()
        {
            UpgradeSettingsFromPreviousInstallation();
            Properties.Settings.Default.SettingChanging += new System.Configuration.SettingChangingEventHandler(Default_SettingChanging);
            Properties.Settings.Default.PropertyChanged += (s, e) =>
            {
                PublishSettings();
            };

            Properties.Settings.Default.SettingsSaving += (s, e) =>
            {
                PublishSettings();
            };

            UpdateAutoLoadConfig();
            RestoreAutoLoadConfig();
            CurrentFilenameChanged += (s, e) =>
            {

            };

            // we trigger this once:
            // because on a full fresh start
            // there are no recent files which
            // could lead to a filename change
            UpdateAutoLoadMenu();
        }

        public MainForm()
        {
            // this shall happen before anything else
            InitializeFrontendSubscriptions();

            // set up the old winforms UI
            InitializeUILanguage();

            // then initialize components
            InitializeComponent();

            // make sure to use app path as working dir
            SetCurrentWorkingDirectory();

            // then restore settings
            InitializeSettings();

            // finally set up logging (based on settings)
            InitializeLogging();

            // Initialize the board configurations
            BoardDefinitions.LoadDefinitions();

            // Initialize the custom device configurations
            CustomDevices.CustomDeviceDefinitions.LoadDefinitions();

            // configure tracking correctly
            InitializeTracking();
        }

        private void InitializeFrontendSubscriptions()
        {
            MessageExchange.Instance.Subscribe<CommandConfigContextMenu>((message) =>
            {
                var msg = message;
                if (msg.Action != "edit") return;
                if (msg.Item.Type == typeof(OutputConfigItem).Name)
                {
                    OpenOutputConfigWizardForId(message.Item.GUID);
                }
                else if (msg.Item.Type == typeof(InputConfigItem).Name)
                {
                    OpenInputConfigWizardForId(message.Item.GUID);
                }
            });

            MessageExchange.Instance.Subscribe<CommandAddConfigFile>((message) =>
            {
                if (message.Type == CommandAddConfigFileType.create)
                {
                    AddNewFileToProject();
                }
                else if (message.Type == CommandAddConfigFileType.merge)
                {
                    mergeToolStripMenuItem_Click(null, null);
                }
            });

            var commandMainMenuHandler = new CommandMainMenuHandler(this);

            MessageExchange.Instance.Subscribe<CommandMainMenu>((message) =>
            {
                commandMainMenuHandler.Handle(message);
            });

            var commandProjectToolbarHandler = new CommandProjectToolbarHandler(this);
            MessageExchange.Instance.Subscribe<CommandProjectToolbar>((message) =>
            {
                commandProjectToolbarHandler.Handle(message);
            });
        }

        private void OpenOutputConfigWizardForId(string guid)
        {
            if (this.InvokeRequired)
            {
                this.Invoke((Action)(() => OpenOutputConfigWizardForId(guid)));
                return;
            }

            var cfg = execManager.ConfigItems.Find(c => c.GUID == guid);
            if (cfg == null) return;
            if (!(cfg is OutputConfigItem)) return;

            // Show a modal dialog after the current event handler is completed, to avoid potential reentrancy caused by running a nested message loop in the WebView2 event handler.
            System.Threading.SynchronizationContext.Current.Post((_) =>
            {
                EditConfigWithWizard(cfg as OutputConfigItem, false);
            }, null);
        }

        private void EditConfigWithWizard(OutputConfigItem cfg, bool create)
        {
            // refactor!!! dependency to arcaze cache etc not nice
            ConfigWizard wizard = new ConfigWizard(execManager,
                                            cfg,
#if ARCAZE
                                            execManager.getModuleCache(),
                                            execManager.getModuleCache().GetArcazeModuleSettings(),
#endif
                                            execManager.ConfigItems.Where(item => item is OutputConfigItem).Cast<OutputConfigItem>().ToList(),
                                            execManager.GetAvailableVariables()
                                          )
            {
                StartPosition = FormStartPosition.CenterParent
            };
            wizard.SettingsDialogRequested += ConfigPanel_SettingsDialogRequested;

            if (wizard.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                if (wizard.ConfigHasChanged())
                {
                    // we have to update the config
                    // using the duplicated config 
                    // that the user edited with the wizard
                    var index = execManager.ConfigItems.FindIndex(c => c.GUID == cfg.GUID);
                    execManager.ConfigItems[index] = wizard.Config;
                    MessageExchange.Instance.Publish(new ConfigValuePartialUpdate() { ConfigItems = new List<IConfigItem>() { wizard.Config } });
                    ExecManager_OnConfigHasChanged(wizard.Config, null);
                }
            };
        }

        private void OpenInputConfigWizardForId(string guid)
        {
            if (this.InvokeRequired)
            {
                this.Invoke((Action)(() => OpenInputConfigWizardForId(guid)));
                return;
            }

            var cfg = execManager.ConfigItems.Find(c => c.GUID == guid);
            if (cfg == null || !(cfg is InputConfigItem)) return;

            // Show a modal dialog after the current event handler is completed, to avoid potential reentrancy caused by running a nested message loop in the WebView2 event handler.
            System.Threading.SynchronizationContext.Current.Post((_) =>
            {
                _editConfigWithInputWizard(cfg as InputConfigItem, false);
            }, null);
        }

        private void _editConfigWithInputWizard(InputConfigItem cfg, bool create)
        {
            // refactor!!! dependency to arcaze cache etc not nice
            InputConfigWizard wizard = new InputConfigWizard(
                                execManager,
                                cfg,
#if ARCAZE
                                execManager.getModuleCache(),
                                execManager.getModuleCache().GetArcazeModuleSettings(),
#endif
                                execManager.ConfigItems.Where(item => item is OutputConfigItem).Cast<OutputConfigItem>().ToList(),
                                execManager.GetAvailableVariables()
                                )
            {
                StartPosition = FormStartPosition.CenterParent
            };

            wizard.SettingsDialogRequested += ConfigPanel_SettingsDialogRequested;
            if (wizard.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                if (wizard.ConfigHasChanged())
                {
                    // we have to update the config
                    // using the duplicated config 
                    // that the user edited with the wizard
                    var index = execManager.ConfigItems.FindIndex(c => c.GUID == cfg.GUID);
                    execManager.ConfigItems[index] = wizard.Config;
                    MessageExchange.Instance.Publish(new ConfigValuePartialUpdate() { ConfigItems = new List<IConfigItem>() { wizard.Config } });
                    ExecManager_OnConfigHasChanged(wizard.Config, null);
                    execManager.OnInputConfigSettingsChanged(wizard.Config, null);
                }
            };
        }

        private void InitializeTracking()
        {
            AppTelemetry.Instance.Enabled = Properties.Settings.Default.CommunityFeedback;
        }

        private void MainForm_Load(object sender, EventArgs _)
        {
            ProjectLoaded += (s, project) =>
            {
                StopExecution();
                MessageExchange.Instance.Publish(project);
            };

            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ProjectHasUnsavedChanges))
                {
                    MessageExchange.Instance
                                   .Publish(new ProjectStatus { HasChanged = ProjectHasUnsavedChanges });
                }
            };

            RestoreWindowsPositionAndZoomLevel();
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            // Check for updates before loading anything else
#if (!DEBUG)
            AutoUpdateChecker.CheckForUpdate(true);
#endif

            if (Properties.Settings.Default.Started == 0)
            {
                OnFirstStart();
            }

            if (Properties.Settings.Default.Started > 0 && (Properties.Settings.Default.Started % 30 == 0))
            {
                OnRepeatedStart();
            }

            Properties.Settings.Default.Started = Properties.Settings.Default.Started + 1;


            cmdLineParams = new CmdLineParams(Environment.GetCommandLineArgs());
            InitializeExecutionManager();

            connectedDevicesToolStripDropDownButton.DropDownDirection = ToolStripDropDownDirection.AboveRight;
            simStatusToolStripDropDownButton1.DropDownDirection = ToolStripDropDownDirection.AboveRight;
            toolStripAircraftDropDownButton.DropDownDirection = ToolStripDropDownDirection.AboveRight;

            SimConnectionIconStatusToolStripStatusLabel.Image = Properties.Resources.warning;
            SimProcessDetectedToolStripMenuItem.Image = Properties.Resources.warning;
            FsuipcToolStripMenuItem.Image = Properties.Resources.warning;
            simConnectToolStripMenuItem.Image = Properties.Resources.warning;
            proSimToolStripMenuItem.Image = Properties.Resources.warning;
            xPlaneDirectToolStripMenuItem.Image = Properties.Resources.warning;
            toolStripConnectedDevicesIcon.Image = Properties.Resources.warning;

            // we only load the autorun value stored in settings
            // and do not use possibly passed in autoRun from cmdline
            // because latter shall only have an temporary influence
            // on the program
            setAutoRunValue(Properties.Settings.Default.AutoRun);

            updateNotifyContextMenu(false);

            // Reset the Title of the Main Window so that it displays the Version too.
            SetTitle("");

            StartupProgressValue = 0;
            MessageExchange.Instance.Publish(new StatusBarUpdate { Value = StartupProgressValue, Text = "Start Connecting" });

#if ARCAZE
            _initializeArcazeModuleSettings();
#endif
            Update();
            Refresh();

            // test subscription
            BrowserMessages.MessageExchange.Instance.Subscribe<Test>((message) =>
            {
                var msg = message;
                MessageBox.Show(msg.Message);
            });

            PublishSettings();
        }

        private void PublishSettings()
        {
            MessageExchange.Instance.Publish(new Settings(Properties.Settings.Default));

            if (execManager == null) return;

            MessageExchange.Instance.Publish(new JoystickDefinitions() { Definitions = execManager.GetJoystickManager().Definitions });
            MessageExchange.Instance.Publish(new MidiControllerDefinitions() { Definitions = execManager.GetMidiBoardManager().Definitions.Values.ToList() });
        }

        private void InitializeExecutionManager()
        {
            execManager = new ExecutionManager(this.Handle);
            execManager.OnConfigHasChanged += ExecManager_OnConfigHasChanged;
            execManager.OnProjectChanged += ExecManager_OnProjectChanged;
            execManager.OnExecute += new EventHandler(ExecManager_Executed);
            execManager.OnStopped += new EventHandler(ExecManager_Stopped);
            execManager.OnStarted += new EventHandler(ExecManager_Started);
            execManager.OnShutdown += new EventHandler(ExecManager_OnShutdown);
            execManager.OnTestModeStarted += (s, e) => UpdateExecutionState();
            execManager.OnTestModeStopped += (s, e) => UpdateExecutionState();

            execManager.OnSimAvailable += ExecManager_OnSimAvailable;
            execManager.OnSimUnavailable += ExecManager_OnSimUnavailable;
            execManager.OnSimCacheConnectionLost += new EventHandler(SimCache_ConnectionLost);
            execManager.OnSimCacheConnected += new EventHandler(SimCache_Connected);
            execManager.OnSimCacheClosed += new EventHandler(SimCache_Closed);
            execManager.OnSimAircraftChanged += ExecManager_OnSimAircraftChanged;

            // working hypothesis: we don't need this at all.
            // execManager.OnModuleCacheAvailable += new EventHandler(ModuleCache_Available);

            execManager.OnModuleConnected += new EventHandler(Module_Connected);
            execManager.OnModuleRemoved += new EventHandler(Module_Removed);
            execManager.OnInitialModuleLookupFinished += new EventHandler(ExecManager_OnInitialModuleLookupFinished);
            execManager.OnTestModeException += new EventHandler(execManager_OnTestModeException);
            execManager.OnJoystickConnectedFinished += ExecManager_OnJoystickConnectedFinished;
            execManager.OnMidiBoardConnectedFinished += ExecManager_OnMidiBoardConnectedFinished;

            // Now that the joystick and midi handlers are configured it's ok to start them
            execManager.StartJoystickManager();
            execManager.StartMidiBoardManager();

            execManager.SettingsDialogRequested += ExecManager_SettingsDialogRequested;
        }

        private void ExecManager_OnProjectChanged(object sender, Project e)
        {
            ProjectOrConfigFileHasChanged();
        }

        private void ProjectOrConfigFileHasChanged()
        {
            ProjectHasUnsavedChanges = true;
            SetProjectNameInTitle();
            UpdateAutoLoadMenu();
            UpdateAllConnectionIcons();
        }

        private void ExecManager_SettingsDialogRequested(object sender, EventArgs e)
        {
            // Show a modal dialog after the current event handler is completed, to avoid potential reentrancy caused by running a nested message loop in the WebView2 event handler.
            System.Threading.SynchronizationContext.Current.Post((_) =>
            {

                if (sender is MobiFlightModuleInfo)
                {
                    ShowSettingsDialog("mobiFlightTabPage", sender as MobiFlightModuleInfo, null, null);
                    return;
                }

                ShowSettingsDialog("peripheralsTabPage", null, null, null);
            }, null);
        }

        private void RefreshConnectedDevicesIcon()
        {
            if (hasConnectedJoysticks || hasConnectedMidiBoards || hasConnectedModules)
            {
                toolStripConnectedDevicesIcon.Image = Properties.Resources.check;
            }
            else
            {
                toolStripConnectedDevicesIcon.Image = Properties.Resources.warning;
            }
        }

        private void ExecManager_OnJoystickConnectedFinished(object sender, EventArgs e)
        {
            joysticksToolStripMenuItem.DropDownItems.Clear();

            var joysticks = execManager.GetJoystickManager().GetJoysticks();

            if (joysticks.Count == 0)
            {
                var item = new ToolStripMenuItem(i18n._tr("uiNone"));
                item.Click += peripheralsToolStripMenuItemClick;
                joysticksToolStripMenuItem.DropDownItems.Add(item);

                hasConnectedJoysticks = false;
            }
            else
            {
                foreach (var joystick in joysticks)
                {
                    var item = new ToolStripMenuItem(joystick.Name);
                    item.Click += peripheralsToolStripMenuItemClick;
                    joysticksToolStripMenuItem.DropDownItems.Add(item);
                }

                hasConnectedJoysticks = true;
            }

            RefreshConnectedDevicesIcon();
        }

        private void ExecManager_OnMidiBoardConnectedFinished(object sender, EventArgs e)
        {
            MIDIDevicesToolStripMenuItem.DropDownItems.Clear();

            var devices = execManager.GetMidiBoardManager().GetMidiBoards();

            if (devices.Count == 0)
            {
                var item = new ToolStripMenuItem(i18n._tr("uiNone"))
                {
                    Enabled = true
                };
                item.Click += peripheralsToolStripMenuItemClick;
                MIDIDevicesToolStripMenuItem.DropDownItems.Add(item);

                hasConnectedMidiBoards = false;
            }
            else
            {
                foreach (var device in devices)
                {
                    var item = new ToolStripMenuItem(device.Name);
                    item.Click += peripheralsToolStripMenuItemClick;
                    MIDIDevicesToolStripMenuItem.DropDownItems.Add(item);
                }

                hasConnectedMidiBoards = true;
            }

            RefreshConnectedDevicesIcon();
        }

        private void ExecManager_OnSimAircraftChanged(object sender, string aircraftName)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new EventHandler<string>(ExecManager_OnSimAircraftChanged), new object[] { sender, aircraftName });
            }
            else
            {
                UpdateAircraft(aircraftName);
                CheckAutoRun();
            }
        }

        private void UpdateAircraft(String aircraftName)
        {
            if (aircraftName == "")
            {
                aircraftName = i18n._tr("uiLabelNoAircraftDetected.");
            }

            toolStripAircraftDropDownButton.Text = aircraftName;
            toolStripAircraftDropDownButton.DropDown.Enabled = true;

            var key = $"{FlightSim.FlightSimType}:{aircraftName}";

            if (!Properties.Settings.Default.AutoLoadLinkedConfig ||
                !AutoLoadConfigs.ContainsKey(key))
            {
                UpdateAutoLoadMenu();

                return;
            }

            var filename = AutoLoadConfigs[key];

            // we only really load the config if it is different from 
            // the current one.
            // the orphaned serials dialog would pop up multiple times
            // especially because we get two events sometimes:
            //      one coming from FSUIPC and
            //      one coming from SimConnect
            if (execManager.Project.FilePath == filename)
            {
                // we still have to update the menu correctly.
                UpdateAutoLoadMenu();
                return;
            }

            if (ProjectHasUnsavedChanges && MessageBox.Show(
                       i18n._tr("uiMessageConfirmDiscardUnsaved"),
                       i18n._tr("uiMessageConfirmDiscardUnsavedTitle"),
                       MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                saveToolStripButton_Click(this, new EventArgs());
            }

            Log.Instance.log($"Auto loading config for {aircraftName}", LogSeverity.Info);
            LoadConfig(filename);
        }

        private void OnRepeatedStart()
        {
            DonateDialog cfpForm = new DonateDialog();
            cfpForm.StartPosition = FormStartPosition.CenterParent;
            if (cfpForm.ShowDialog() == DialogResult.OK)
            {
                // we can track the click.
            }
            this.BringToFront();
        }

        private void OnFirstStart()
        {
            int i = Properties.Settings.Default.Started;
            WelcomeDialog wd = new WelcomeDialog();
            wd.WebsiteUrl = $"https://github.com/MobiFlight/MobiFlight-Connector/releases/tag/{CurrentVersion()}";
            wd.ReleaseNotesClicked += (sender, e) =>
            {
                Process.Start($"https://github.com/MobiFlight/MobiFlight-Connector/releases/tag/{CurrentVersion()}");
            };

            wd.StartPosition = FormStartPosition.CenterParent;
            wd.Text = String.Format(wd.Text, DisplayVersion());
            wd.ShowDialog();
            this.BringToFront();

            // MSFS2020
            WasmModuleUpdater updater = new WasmModuleUpdater();

            if (updater.AutoDetectCommunityFolder() && (updater.WasmModulesAreDifferent(updater.CommunityFolder) || updater.WasmModulesAreDifferent(updater.CommunityFolder2024)))
            {
                // MSFS2020 installed
                Msfs2020StartupForm msfsForm = new Msfs2020StartupForm();
                msfsForm.StartPosition = FormStartPosition.CenterParent;
                if (msfsForm.ShowDialog() == DialogResult.OK)
                {
                    InstallWasmModule();
                }
                this.BringToFront();
            }

            // if the user is not participating yet, ask for permission
            if (!Properties.Settings.Default.CommunityFeedback)
            {
                CommunityFeedbackStartupForm cfpForm = new CommunityFeedbackStartupForm();
                cfpForm.StartPosition = FormStartPosition.CenterParent;
                if (cfpForm.ShowDialog() == DialogResult.OK)
                {
                    Properties.Settings.Default.CommunityFeedback = true;
                }
                this.BringToFront();
            }
        }

        private void UpdateAllConnectionIcons()
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(UpdateAllConnectionIconsAction));
            }
            else
            {
                UpdateAllConnectionIconsAction();
            }
        }

        private void UpdateAllConnectionIconsAction()
        {
            UpdateSimStatusIcon();
            UpdateSimConnectStatusIcon();
            UpdateXplaneDirectConnectStatusIcon();
            UpdateFsuipcStatusIcon();
            UpdateProSimStatusIcon();
            UpdateSeparatorInStatusMenu();
        }

        private void ConfigPanel_SettingsDialogRequested(object sender, EventArgs e)
        {
            MobiFlightModule module = (sender as MobiFlightModule);
            MobiFlightModuleInfo moduleInfo = null;

            if (module != null) moduleInfo = module.ToMobiFlightModuleInfo();

            ShowSettingsDialog("mobiFlightTabPage", moduleInfo, null, null);
        }

        private void ExecManager_OnConfigHasChanged(object sender, EventArgs e)
        {
            ProjectOrConfigFileHasChanged();
        }

        /// <summary>
        /// properly disconnects all connections to FSUIPC and Arcaze
        /// </summary>
        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            AppTelemetry.Instance.TrackShutdown();
            execManager.Shutdown();
            SaveWindowPositionAndZoomLevel();
            Properties.Settings.Default.Save();
            logPanel1.Shutdown();
        } //Form1_FormClosed

        private void SaveWindowPositionAndZoomLevel()
        {
            // Only save if not minimized or maximized, otherwise save RestoreBounds
            if (this.WindowState == FormWindowState.Normal)
            {
                Properties.Settings.Default.WindowLocation = this.Location;
                Properties.Settings.Default.WindowSize = this.Size;
            }
            else
            {
                Properties.Settings.Default.WindowLocation = this.RestoreBounds.Location;
                Properties.Settings.Default.WindowSize = this.RestoreBounds.Size;
            }

            if (this.WindowState != FormWindowState.Minimized)
            {
                Properties.Settings.Default.WindowState = this.WindowState;
            }

            Properties.Settings.Default.WindowZoomFactor = frontendPanel1.GetZoomFactor();
        }

        private void RestoreWindowsPositionAndZoomLevel()
        {
            if (Properties.Settings.Default.WindowZoomFactor >= 0.0)
            {
                frontendPanel1.SetZoomFactor(Properties.Settings.Default.WindowZoomFactor);
            }

            var proposedBounds = new Rectangle(Properties.Settings.Default.WindowLocation, Properties.Settings.Default.WindowSize);

            if (!IsOnScreen(proposedBounds)) return;

            // Restore window position and size
            if (Properties.Settings.Default.WindowSize.Width > 0 && Properties.Settings.Default.WindowSize.Height > 0)
            {
                this.StartPosition = FormStartPosition.Manual;
                this.Size = Properties.Settings.Default.WindowSize;
                this.Location = Properties.Settings.Default.WindowLocation;
            }

            this.WindowState = Properties.Settings.Default.WindowState;
        }

        private bool IsOnScreen(Rectangle rect)
        {
            return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect));
        }

        void ExecManager_OnInitialModuleLookupFinished(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                this.Invoke(new EventHandler(ExecManager_OnInitialModuleLookupFinished), new object[] { sender, e });
                return;
            }
            StartupProgressValue = 70;
            MessageExchange.Instance.Publish(new StatusBarUpdate { Value = StartupProgressValue, Text = "Checking for Firmware Updates..." });
            CheckForFirmwareUpdates();

            StartupProgressValue = 90;
            MessageExchange.Instance.Publish(new StatusBarUpdate { Value = StartupProgressValue, Text = "Loading last config..." });
            _autoloadConfig();

            StartupProgressValue = 100;
            MessageExchange.Instance.Publish(new StatusBarUpdate { Value = StartupProgressValue, Text = "Finished." });

            CheckForWasmModuleUpdate();
            CheckForHubhopUpdate();

            UpdateAllConnectionIcons();

            UpdateStatusBarModuleInformation();

            // Track config loaded event
            AppTelemetry.Instance.TrackStart();

            InitialLookupFinished = true;

            // Now we are done with all initialization stuff
            // and now we are ready to look for sims
            execManager.AutoConnectStart();
        }

        private void CheckForHubhopUpdate()
        {

            if (!WasmModuleUpdater.HubHopPresetsPresent())
            {
                DownloadHubHopPresets();
                return;
            }

            var lastModification = WasmModuleUpdater.HubHopPresetTimestamp();
            UpdateHubHopTimestampInStatusBar(lastModification);

            if (lastModification > DateTime.UtcNow.AddDays(-7)) return;
            // we could provide a warning icon or so.
            if (!Properties.Settings.Default.HubHopAutoCheck) return;
            // we haven't updated hubhop events in more than 7 days.

            TimeoutMessageDialog tmd = new TimeoutMessageDialog();
            tmd.StartPosition = FormStartPosition.CenterParent;
            tmd.DefaultDialogResult = DialogResult.Cancel;
            tmd.Message = i18n._tr("uiMessageHubHopAutoUpdate");
            tmd.Text = i18n._tr("uiTitleHubhopAutoUpdate");

            if (tmd.ShowDialog() == DialogResult.OK)
            {
                DownloadHubHopPresets();
            }
        }

        private void CheckForWasmModuleUpdate()
        {
            WasmModuleUpdater updater = new WasmModuleUpdater();

        }

        void CheckForFirmwareUpdates()
        {
            MobiFlightCache mfCache = execManager.getMobiFlightModuleCache();

            List<MobiFlightModuleInfo> modules = mfCache.GetDetectedCompatibleModules();
            List<MobiFlightModule> modulesForUpdate = new List<MobiFlightModule>();
            List<MobiFlightModuleInfo> modulesForFlashing = new List<MobiFlightModuleInfo>();

            foreach (MobiFlightModule module in mfCache.GetModules())
            {
                if (module.Board.Info.CanInstallFirmware)
                {
                    if (module.FirmwareRequiresUpdate())
                    {
                        // Update needed!!!
                        modulesForUpdate.Add(module);
                    }
                }
            }

            foreach (MobiFlightModuleInfo moduleInfo in modules)
            {
                if (moduleInfo.Type == "Ignored") continue;

                if (moduleInfo.FirmwareInstallPossible())
                {
                    modulesForFlashing.Add(moduleInfo);
                }
            }


            if (Properties.Settings.Default.FwAutoUpdateCheck && (modulesForFlashing.Count > 0 || modulesForUpdate.Count > 0))
            {
                if (!MobiFlightFirmwareUpdater.IsValidArduinoIdePath(Properties.Settings.Default.ArduinoIdePathDefault))
                {
                    ArduinoIdePathForm idePath = new ArduinoIdePathForm();
                    idePath.StartPosition = FormStartPosition.CenterParent;
                    if (idePath.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }
                }
            }

            if (modulesForUpdate.Count > 0)
            {
                PerformFirmwareUpdateProcess(modulesForUpdate);
            }

            // this is only for non mobiflight boards
            if (Properties.Settings.Default.FwAutoUpdateCheck && modulesForFlashing.Count > 0)
            {
                PerformFirmwareInstallProcess(modulesForFlashing);
            }
        }

        private void PerformFirmwareInstallProcess(MobiFlightModuleInfo module)
        {
            PerformFirmwareInstallProcess(new List<MobiFlightModuleInfo>() { module });
        }
        private void PerformFirmwareInstallProcess(List<MobiFlightModuleInfo> modulesForFlashing)
        {
            TimeoutMessageDialog tmd = new TimeoutMessageDialog();
            tmd.StartPosition = FormStartPosition.CenterParent;
            tmd.DefaultDialogResult = DialogResult.Cancel;
            tmd.Message = i18n._tr("uiMessageUpdateArduinoOkCancel");
            tmd.Text = i18n._tr("uiMessageUpdateOldFirmwareTitle");

            if (tmd.ShowDialog() == DialogResult.OK)
            {
                if (ShowSettingsDialog("mobiFlightTabPage", null, modulesForFlashing, null) == System.Windows.Forms.DialogResult.OK)
                {
                }
            }
            else
            {
                tmd.StartPosition = FormStartPosition.CenterParent;
                tmd.DefaultDialogResult = DialogResult.Cancel;
                tmd.Message = i18n._tr("uiMessageUpdateArduinoFwAutoDisableYesNo");
                tmd.Text = i18n._tr("Hint");

                if (tmd.ShowDialog() == DialogResult.OK)
                {
                    Properties.Settings.Default.FwAutoUpdateCheck = false;
                };
            }
        }

        private void PerformFirmwareUpdateProcess(MobiFlightModule module)
        {
            PerformFirmwareUpdateProcess(new List<MobiFlightModule>() { module });
        }

        private void PerformFirmwareUpdateProcess(List<MobiFlightModule> modulesForUpdate)
        {
            TimeoutMessageDialog tmd = new TimeoutMessageDialog();
            tmd.StartPosition = FormStartPosition.CenterParent;
            tmd.DefaultDialogResult = DialogResult.Cancel;
            tmd.Message = i18n._tr("uiMessageUpdateOldFirmwareOkCancel");
            tmd.Text = i18n._tr("uiMessageUpdateOldFirmwareTitle");

            if (tmd.ShowDialog() == DialogResult.OK)
            {
                if (ShowSettingsDialog("mobiFlightTabPage", null, null, modulesForUpdate) == System.Windows.Forms.DialogResult.OK)
                {
                }
            };
        }

        private DialogResult ShowSettingsDialog(String SelectedTab, MobiFlightModuleInfo SelectedBoard, List<MobiFlightModuleInfo> BoardsForFlashing, List<MobiFlightModule> BoardsForUpdate)
        {
            SettingsDialog dlg = new SettingsDialog(execManager);
            dlg.StartPosition = FormStartPosition.CenterParent;
            execManager.OnModuleConnected += dlg.UpdateConnectedModule;
            execManager.OnModuleRemoved += dlg.UpdateRemovedModule;

            switch (SelectedTab)
            {
                case "mobiFlightTabPage":
                    dlg.tabControl1.SelectedTab = dlg.mobiFlightTabPage;
                    break;
                case "ArcazeTabPage":
                    dlg.tabControl1.SelectedTab = dlg.ArcazeTabPage;
                    break;
                case "peripheralsTabPage":
                    dlg.tabControl1.SelectedTab = dlg.peripheralsTabPage;
                    break;
            }
            if (SelectedBoard != null)
                dlg.PreselectedBoard = SelectedBoard;

            if (BoardsForFlashing != null)
                dlg.MobiFlightModulesForFlashing = BoardsForFlashing;

            if (BoardsForUpdate != null)
                dlg.MobiFlightModulesForUpdate = BoardsForUpdate;

            SettingsDialogActive = true;
            var dialogResult = dlg.ShowDialog();
            execManager.OnModuleConnected -= dlg.UpdateConnectedModule;
            execManager.OnModuleRemoved -= dlg.UpdateRemovedModule;
            SettingsDialogActive = false;
            return dialogResult;
        }

        // this performs the update of the existing user settings 
        // when updating to a new MobiFlight Version
        private void UpgradeSettingsFromPreviousInstallation()
        {
            if (Properties.Settings.Default.UpgradeRequired)
            {
                try
                {
                    Properties.Settings.Default.Upgrade();
                }
                catch
                {
                    // If the properties file is corrupted for some reason catch the exception and
                    // reset back to a default version.

                    Properties.Settings.Default.Reset();
                }
                Properties.Settings.Default.UpgradeRequired = false;
                Properties.Settings.Default.StartedTotal += Properties.Settings.Default.Started;
                Properties.Settings.Default.Started = 0;
                Properties.Settings.Default.Save();
            }
        }

        public void checkForUpdateToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AutoUpdateChecker.CheckForUpdate();
        }

        void execManager_OnTestModeException(object sender, EventArgs e)
        {
            StopExecution();
            _showError((sender as Exception).Message);
        }

        void Default_SettingChanging(object sender, System.Configuration.SettingChangingEventArgs e)
        {
            if (e.SettingName == "TestTimerInterval")
            {
                execManager.SetTestModeInterval((int)e.NewValue);
            }

            if (e.SettingName == "PollInterval")
            {
                // set FSUIPC update interval
                execManager.SetPollInterval((int)e.NewValue);
            }

            if (e.SettingName == "CommunityFeedback")
            {
                AppTelemetry.Instance.Enabled = Properties.Settings.Default.CommunityFeedback;
            }

            if (e.SettingName == "LogEnabled")
            {
                logPanel1.Visible = (bool)e.NewValue;
                logSplitter.Visible = (bool)e.NewValue;
            }
        }

        private void _autoloadConfig()
        {
            if (cmdLineParams.ConfigFile != null)
            {
                if (!System.IO.File.Exists(cmdLineParams.ConfigFile))
                {
                    MessageBox.Show(
                                i18n._tr("uiMessageCmdParamConfigFileDoesNotExist") + "\r" + cmdLineParams.ConfigFile,
                                i18n._tr("Hint"),
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Exclamation,
                                MessageBoxDefaultButton.Button1);
                    return;
                }
                else
                {
                    LoadConfig(cmdLineParams.ConfigFile);
                    return;
                }
            }

            _autoloadLastConfig();
        }

        private void _autoloadLastConfig()
        {
            // the new autoload feature
            // step1 load config always... good feature ;)
            // step2 run automatically -> see fsuipc connected event
            if (Properties.Settings.Default.RecentFiles.Count > 0)
            {
                foreach (string file in Properties.Settings.Default.RecentFiles)
                {
                    if (!System.IO.File.Exists(file)) continue;
                    LoadConfig(file);
                    return;
                }
            } //if 


            // Initialize properly the empty project state.
            CreateNewProject();
        }

#if ARCAZE
        private void _initializeArcazeModuleSettings()
        {
            if (!Properties.Settings.Default.ArcazeSupportEnabled) return;

            Dictionary<string, ArcazeModuleSettings> settings = execManager.getModuleCache().GetArcazeModuleSettings();
            List<string> serials = new List<string>();

            // get all currently connected devices
            // add 'em to the list
            foreach (IModuleInfo arcaze in execManager.getModuleCache().getModuleInfo())
            {
                serials.Add(arcaze.Serial);
            }

            // and now verify that all modules that are connected
            // really are configured
            // show message box if not
            if (settings.Keys.Intersect(serials).ToArray().Count() != serials.Count)
            {
                if (MessageBox.Show(
                                i18n._tr("uiMessageModulesNotConfiguredYet"),
                                i18n._tr("Hint"),
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Exclamation,
                                MessageBoxDefaultButton.Button1) == System.Windows.Forms.DialogResult.OK)
                {
                    if (ShowSettingsDialog("ArcazeTabPage", null, null, null) == System.Windows.Forms.DialogResult.OK)
                    {
                    }
                }
            }

            execManager.updateModuleSettings(execManager.getModuleCache().GetArcazeModuleSettings());
        }
#endif

        private void Module_Connected(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                this.Invoke(new EventHandler(Module_Connected), new object[] { sender, e });
                return;
            }
            UpdateStatusBarModuleInformation();

            // During initial lookup we are showing the panel
            // and we would like to display some progress information
            if (!InitialLookupFinished)
            {
                StartupProgressValue = 50;
                var progressIncrement = (75 - StartupProgressValue) / 2;
                StartupProgressValue += progressIncrement;
                MessageExchange.Instance.Publish(new StatusBarUpdate { Value = StartupProgressValue, Text = "Scanning for boards." });
                return;
            }

            var module = (sender as MobiFlightModule);
            if (module == null) return;

            // When we open the settings dialog
            // many of these module connected events
            // are on purpose because we are 
            // flashing & resetting modules
            // in such cases we don't want the auto-detect feature
            if (SettingsDialogActive) return;

            // This board is not flashed yet
            if (module.ToMobiFlightModuleInfo()?.FirmwareInstallPossible() ?? false)
            {
                // We can install firmware on this device but the user
                // has asked us not to auto scan devices so we bail
                if (!Properties.Settings.Default.FwAutoUpdateCheck) return;

                PerformFirmwareInstallProcess(module.ToMobiFlightModuleInfo());
                return;
            }

            // The board already has MF firmware
            if (!module.FirmwareRequiresUpdate()) return;


            PerformFirmwareUpdateProcess(module);
        }

        void Module_Removed(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                this.Invoke(new EventHandler(Module_Removed), new object[] { sender, e });
                return;
            }
            // _disconnectArcaze();
            UpdateStatusBarModuleInformation();

            // Todo: Show this error outside of the context of firmware update
            // _showError(string.Format(i18n._tr("uiMessageModuleRemoved"), (sender as MobiFlightModuleInfo)?.Name ?? "Unknown", (sender as MobiFlightModuleInfo)?.Port ?? "???"));
        }

        /// <summary>
        /// updates the UI with appropriate icon states
        /// </summary>
        void ExecManager_OnShutdown(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                this.Invoke(new EventHandler(ExecManager_OnShutdown), new object[] { sender, e });
                return;
            }
            UpdateStatusBarModuleInformation();
        }

        /// <summary>
        /// updates the UI with appropriate icon states
        /// </summary>
        void ModuleCache_Available(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                this.Invoke(new EventHandler(ModuleCache_Available), new object[] { sender, e });
                return;
            }
            UpdateStatusBarModuleInformation();
        }

        /// <summary>
        /// Returns true if the run button should be enabled based on various MobiFlight states.
        /// </summary>
        private bool RunIsAvailable()
        {
            return
                // We are not already running
                !execManager.IsStarted() && !execManager.TestModeIsStarted();
        }

        /// <summary>
        /// updates the UI with appropriate icon states
        /// </summary>
        void SimCache_Closed(object sender, EventArgs e)
        {
            if (sender.GetType() == typeof(SimConnectCache))
            {
                UpdateSimConnectStatusIcon();
            }
            else if (sender.GetType() == typeof(XplaneCache))
            {
                UpdateXplaneDirectConnectStatusIcon();
            }
            else if (sender.GetType() == typeof(Fsuipc2Cache))
            {
                UpdateFsuipcStatusIcon();
            }
            else if (sender is ProSim.ProSimCacheInterface)
            {
                UpdateProSimStatusIcon();
            }

            UpdateSeparatorInStatusMenu();

            SimConnectionIconStatusToolStripStatusLabel.Image = Properties.Resources.warning;

            // Stop execution manager?
        }

        private void ExecManager_OnSimAvailable(object sender, EventArgs e)
        {
            FlightSimType flightSim = (FlightSimType)sender;

            switch (flightSim)
            {
                case FlightSimType.MSFS2020:
                    SimProcessDetectedToolStripMenuItem.Text = "MSFS Detected";
                    SimProcessDetectedToolStripMenuItem.Image = Properties.Resources.check;
                    IsMSFSRunning = true;
                    break;

                case FlightSimType.FS9:
                    SimProcessDetectedToolStripMenuItem.Text = "FS2004 Detected";
                    SimProcessDetectedToolStripMenuItem.Image = Properties.Resources.check;
                    break;

                case FlightSimType.FSX:
                    SimProcessDetectedToolStripMenuItem.Text = "FSX Detected";
                    SimProcessDetectedToolStripMenuItem.Image = Properties.Resources.check;
                    break;

                case FlightSimType.P3D:
                    SimProcessDetectedToolStripMenuItem.Text = "P3D Detected";
                    SimProcessDetectedToolStripMenuItem.Image = Properties.Resources.check;
                    break;

                case FlightSimType.XPLANE:
                    SimProcessDetectedToolStripMenuItem.Text = "X-Plane Detected";
                    SimProcessDetectedToolStripMenuItem.Image = Properties.Resources.check;
                    break;

                case FlightSimType.UNKNOWN:
                    SimProcessDetectedToolStripMenuItem.Text = "Unkown Detected";
                    SimProcessDetectedToolStripMenuItem.Image = Properties.Resources.module_unknown;
                    break;

                default:
                    SimProcessDetectedToolStripMenuItem.Text = "Undefined";
                    break;
            }
            SimProcessDetectedToolStripMenuItem.Image = Properties.Resources.check;
        }

        private void ExecManager_OnSimUnavailable(object sender, EventArgs e)
        {
            FlightSimType flightSim = (FlightSimType)sender;

            SimProcessDetectedToolStripMenuItem.Text = "No sim running.";
            SimProcessDetectedToolStripMenuItem.Image = Properties.Resources.warning;
            IsMSFSRunning = false;

            UpdateAllConnectionIcons();
        }

        /// <summary>
        /// updates the UI with appropriate icon states
        /// </summary>        
        void SimCache_Connected(object sender, EventArgs e)
        {
            // Typically the information in this static object is correct
            // only in the case of FSUIPC it might actually be not correct
            // because we can have a native connection and a fsuipc connection at the same time
            FlightSimConnectionMethod CurrentConnectionMethod = FlightSim.FlightSimConnectionMethod;
            FlightSimType CurrentFlightSimType = FlightSim.FlightSimType;

            if ((sender as CacheInterface).IsConnected())
            {
                // Can be triggered from ProSim GraphQL observable subscription, so need to invoke on UI thread
                if (this.InvokeRequired)
                {
                    this.Invoke(new Action(() => 
                    {
                        SimConnectionIconStatusToolStripStatusLabel.Image = Properties.Resources.check;
                    }));
                } 
                else
                {
                    SimConnectionIconStatusToolStripStatusLabel.Image = Properties.Resources.check;
                }

                Log.Instance.log($"Connected to {FlightSim.SimNames[CurrentFlightSimType]}. [{FlightSim.SimConnectionNames[CurrentConnectionMethod]}].", LogSeverity.Info);
            }

            if (sender.GetType() == typeof(SimConnectCache) && FlightSim.FlightSimType == FlightSimType.MSFS2020)
            {
                SimProcessDetectedToolStripMenuItem.Text = "MSFS Detected";

                if ((sender as SimConnectCache).IsSimConnectConnected())
                {
                    simConnectToolStripMenuItem.Text = "SimConnect OK. Waiting for WASM Module. (MSFS)";
                    Log.Instance.log("Connected to SimConnect (MSFS).", LogSeverity.Info);
                }

                if ((sender as SimConnectCache).IsConnected())
                {
                    simConnectToolStripMenuItem.Text = "WASM Module (MSFS)";
                    simConnectToolStripMenuItem.Image = Properties.Resources.check;
                    simConnectToolStripMenuItem.Enabled = true;
                    Log.Instance.log("Connected to WASM Module (MSFS).", LogSeverity.Info);

                    if (!execManager.GetFsuipcConnectCache().IsConnected())
                    {
                        UpdateFsuipcStatusIcon();
                    }
                }

                UpdateSimConnectStatusIcon();

                AppTelemetry.Instance.TrackFlightSimConnected(FlightSim.FlightSimType.ToString(), FlightSimConnectionMethod.SIMCONNECT.ToString());
                Log.Instance.log($"{FlightSim.SimNames[FlightSim.FlightSimType]} detected. [{FlightSim.SimConnectionNames[FlightSim.FlightSimConnectionMethod]}].", LogSeverity.Info);
            }
            else if (sender.GetType() == typeof(XplaneCache) && FlightSim.FlightSimType == FlightSimType.XPLANE)
            {
                SimProcessDetectedToolStripMenuItem.Text = "X-Plane Detected";
                if ((sender as XplaneCache).IsConnected())
                {
                    UpdateXplaneDirectConnectStatusIcon();
                    xPlaneDirectToolStripMenuItem.Text = FlightSim.SimConnectionNames[FlightSim.FlightSimConnectionMethod].ToString();
                    xPlaneDirectToolStripMenuItem.Image = Properties.Resources.check;
                    xPlaneDirectToolStripMenuItem.Enabled = true;
                }

                AppTelemetry.Instance.TrackFlightSimConnected(FlightSim.FlightSimType.ToString(), FlightSimConnectionMethod.XPLANE.ToString());
                Log.Instance.log($"{FlightSim.SimNames[FlightSim.FlightSimType]} detected. [{FlightSim.SimConnectionNames[FlightSim.FlightSimConnectionMethod]}].", LogSeverity.Info);
            }
            else if (sender.GetType() == typeof(Fsuipc2Cache))
            {

                Fsuipc2Cache c = sender as Fsuipc2Cache;
                switch (FlightSim.FlightSimConnectionMethod)
                {
                    case FlightSimConnectionMethod.FSUIPC:
                        CurrentConnectionMethod = FlightSimConnectionMethod.FSUIPC;
                        FsuipcToolStripMenuItem.Text = i18n._tr("fsuipcStatus") + " (" + FlightSim.FlightSimType.ToString() + ")";
                        break;

                    case FlightSimConnectionMethod.XPLANE:
                    case FlightSimConnectionMethod.XPUIPC:
                        CurrentConnectionMethod = FlightSimConnectionMethod.XPUIPC;
                        FsuipcToolStripMenuItem.Text = "XPUIPC Status";
                        break;

                    case FlightSimConnectionMethod.WIDECLIENT:
                        CurrentConnectionMethod = FlightSimConnectionMethod.WIDECLIENT;
                        FsuipcToolStripMenuItem.Text = "WideClient Status";
                        break;
                }
                FsuipcToolStripMenuItem.Image = Properties.Resources.check;
                FsuipcToolStripMenuItem.Image.Tag = "check";
                FsuipcToolStripMenuItem.Enabled = true;
                AppTelemetry.Instance.TrackFlightSimConnected(FlightSim.FlightSimType.ToString(), c.FlightSimConnectionMethod.ToString());
                Log.Instance.log($"{FlightSim.SimNames[FlightSim.FlightSimType]} detected. [{FlightSim.SimConnectionNames[CurrentConnectionMethod]}].", LogSeverity.Info
                );
            } 
            else if (sender is ProSim.ProSimCacheInterface)
            {
                proSimToolStripMenuItem.Text = "ProSim";
                proSimToolStripMenuItem.Image = Properties.Resources.check;
                proSimToolStripMenuItem.Enabled = true;
                Log.Instance.log("Connected to ProSim", LogSeverity.Info);
            }

            UpdateSeparatorInStatusMenu();
        }

        /// <summary>
        /// gets triggered as soon as the fsuipc is connected
        /// </summary>        
        void CheckAutoRun()
        {
            if (Properties.Settings.Default.AutoRun || cmdLineParams.AutoRun)
            {
                execManager.Stop();
                execManager.Start();
                if (Properties.Settings.Default.MinimizeOnAutoRun)
                {
                    minimizeMainForm(true);
                }
            }
        }

        /// <summary>
        /// shows message to user and stops execution of timer
        /// </summary>
        void SimCache_ConnectionLost(object sender, EventArgs e)
        {
            execManager.Stop();

            if (!execManager.SimAvailable())
            {
                _showError(i18n._tr("uiMessageFsHasBeenStopped"));
                UpdateAllConnectionIcons();
                return;
            }

            if (sender.GetType() == typeof(SimConnectCache))
            {
                _showError(i18n._tr("uiMessageSimConnectConnectionLost"));
                UpdateSimConnectStatusIcon();
            }
            else if (sender.GetType() == typeof(XplaneCache))
            {
                _showError(i18n._tr("uiMessageXplaneConnectionLost"));
                UpdateXplaneDirectConnectStatusIcon();
            }
            else if (sender is ProSim.ProSimCacheInterface)
            {
                UpdateProSimStatusIcon();
            }
            else
            {
                _showError(i18n._tr("uiMessageFsuipcConnectionLost"));
                if (execManager.GetSimConnectCache().IsConnected())
                    UpdateFsuipcStatusIcon();
            }

            UpdateSeparatorInStatusMenu();
        }

        /// <summary>
        /// handler which sets the states of UI elements when timer gets started
        /// </summary>
        void ExecManager_Started(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new EventHandler(ExecManager_Started), new object[] { sender, e });
                return;
            }

            updateNotifyContextMenu(execManager.IsStarted());
            UpdateExecutionState();
        } //timer_Started()

        private void UpdateExecutionState()
        {
            MessageExchange.Instance.Publish(new ExecutionState()
            {
                IsRunning = execManager.IsStarted(),
                IsTesting = execManager.TestModeIsStarted(),
                RunAvailable = RunIsAvailable(),
                TestAvailable = TestRunIsAvailable(),
            });
        }

        /// <summary>
        /// handler which sets the states of UI elements when timer gets stopped
        /// </summary>
        void ExecManager_Stopped(object sender, EventArgs e)
        {
            if (InvokeRequired)
            {
                Invoke(new EventHandler(ExecManager_Stopped), new object[] { sender, e });
                return;
            }

            updateNotifyContextMenu(execManager.IsStarted());

            UpdateExecutionState();
        } //timer_Stopped

        private bool TestRunIsAvailable()
        {
            return execManager.ModulesAvailable() && !execManager.TestModeIsStarted() && !execManager.IsStarted();
        }

        /// <summary>
        /// Timer eventhandler
        /// </summary>        
        void ExecManager_Executed(object sender, EventArgs e)
        {
            toolStripStatusLabel.Text += ".";
            if (toolStripStatusLabel.Text.Length > (10 + i18n._tr("Running").Length))
            {
                toolStripStatusLabel.Text = i18n._tr("Running");
            }
        } //timer_Tick()

        /// <summary>
        /// gathers infos about the connected modules and stores information in different objects
        /// </summary>
        /// <returns>returns true if there are modules present</returns>
        private void UpdateStatusBarModuleInformation()
        {
            modulesToolStripMenuItem.DropDownItems.Clear();

#if ARCAZE
            var modules = execManager.getModuleCache().getModuleInfo();

            foreach (IModuleInfo module in modules)
            {
                modulesToolStripMenuItem.DropDownItems.Add(module.Name + "/ " + module.Serial);
            }
#endif

#if MOBIFLIGHT
            var mfModules = execManager.getMobiFlightModuleCache().GetModuleInfo();

            foreach (IModuleInfo module in mfModules)
            {
                ToolStripDropDownItem item = new ToolStripMenuItem($"{module.Name} ({module.Port})")
                {
                    Tag = module
                };
                item.Click += statusToolStripMenuItemClick;
                modulesToolStripMenuItem.DropDownItems.Add(item);
            }
#endif

            if ((modules.Count() + mfModules.Count()) == 0)
            {
                var item = new ToolStripMenuItem(i18n._tr("uiNone"));
                modulesToolStripMenuItem.DropDownItems.Add(item);
                item.Click += statusToolStripMenuItemClick;

                hasConnectedModules = false;
            }
            else
            {
                hasConnectedModules = true;
            }

            RefreshConnectedDevicesIcon();
        }

        private void statusToolStripMenuItemClick(object sender, EventArgs e)
        {
            MobiFlightModuleInfo moduleInfo = (sender as ToolStripMenuItem).Tag as MobiFlightModuleInfo;

            ShowSettingsDialog("mobiFlightTabPage", moduleInfo, null, null);
        }

        private void peripheralsToolStripMenuItemClick(object sender, EventArgs e)
        {
            ShowSettingsDialog("peripheralsTabPage", null, null, null);
        }



        /// <summary>
        /// updates the context menu entries for start and stop depending
        /// on the current application state
        /// </summary>
        /// <param name="isRunning"></param>
        protected void updateNotifyContextMenu(bool isRunning)
        {
            try
            {
                // The Start entry
                contextMenuStripNotifyIcon.Items[0].Enabled = !isRunning;

                // The Stop entry
                contextMenuStripNotifyIcon.Items[1].Enabled = isRunning;
            }
            catch (Exception ex)
            {
                // do nothing
                Log.Instance.log(ex.Message, LogSeverity.Info);
            }
        }

        /// <summary>
        /// present errors to user via message dialog or when minimized via balloon
        /// </summary>        
        private void _showError(string msg)
        {
            if (this.WindowState != FormWindowState.Minimized)
            {
                TimeoutMessageDialog.Show(msg, i18n._tr("Hint"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                notifyIcon.ShowBalloonTip(1000, i18n._tr("Hint"), msg, ToolTipIcon.Warning);
            }
        } //_showError()

        /// <summary>
        /// handles the resize event
        /// </summary>
        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Maximized) return;

            minimizeMainForm(FormWindowState.Minimized == this.WindowState);
        } //MainForm_Resize()

        /// <summary>
        /// handles minimize event
        /// </summary>        
        protected void minimizeMainForm(bool minimized)
        {
            if (minimized)
            {
                notifyIcon.Visible = true;
                notifyIcon.BalloonTipTitle = i18n._tr("uiMessageMFConnectorInterfaceActive");
                notifyIcon.BalloonTipText = i18n._tr("uiMessageApplicationIsRunningInBackgroundMode");
                notifyIcon.ShowBalloonTip(1000);
                this.Hide();
            }
            else
            {
                notifyIcon.Visible = false;
                this.Show();
                if (this.WindowState != FormWindowState.Normal)
                    this.WindowState = FormWindowState.Normal;
                this.BringToFront();
            }

            execManager?.OnMinimize(minimized);
        } //minimizeMainForm()

        /// <summary>
        /// restores the current main form when user clicks on "restore" menu item in notify icon context menu
        /// </summary>
        private void restoreToolStripMenuItem_Click(object sender, EventArgs e)
        {
            minimizeMainForm(false);
        } //wiederherstellenToolStripMenuItem_Click()

        /// <summary>
        /// restores the current main form when user double clicks notify icon
        /// </summary>        
        private void notifyIcon_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) return;
            minimizeMainForm(false);
        }

        /// <summary>
        /// exits when user selects according menu item in notify icon's context menu
        /// </summary>
        public void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        } //exitToolStripMenuItem_Click()

        /// <summary>
        /// opens file dialog when clicking on according button
        /// </summary>
        public void loadToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenFileDialog fd = new OpenFileDialog();
            fd.Filter = fileExtensionLoadFilter;

            if (ProjectHasUnsavedChanges && MessageBox.Show(
                       i18n._tr("uiMessageConfirmDiscardUnsaved"),
                       i18n._tr("uiMessageConfirmDiscardUnsavedTitle"),
                       MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                saveToolStripButton_Click(this, new EventArgs());
            }

            if (DialogResult.OK == fd.ShowDialog())
            {
                LoadConfig(fd.FileName);
            }
        }

        private void mergeToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenMergeDialog();
        }

        private void OpenMergeDialog()
        {
            // Show a modal dialog after the current event handler is completed, to avoid potential reentrancy caused by running a nested message loop in the WebView2 event handler.
            System.Threading.SynchronizationContext.Current.Post((_) =>
            {
                OpenFileDialog fd = new OpenFileDialog();
                fd.Filter = fileExtensionLoadFilter;

                if (DialogResult.OK == fd.ShowDialog())
                {
                    LoadConfig(fd.FileName, true);
                }
            }, null);
        }

        /// <summary>
        /// stores the provided filename in the list of recently used files
        /// </summary>
        /// <param name="fileName">the filename to be used</param>
        private void _storeAsRecentFile(string fileName)
        {
            if (Properties.Settings.Default.RecentFiles.Contains(fileName))
            {
                Properties.Settings.Default.RecentFiles.Remove(fileName);
            }
            Properties.Settings.Default.RecentFiles.Insert(0, fileName);
            Properties.Settings.Default.Save();
        }

        /// <summary>
        /// gets triggered when user clicks on recent used file entry
        /// loads the according config
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        void recentMenuItem_Click(object sender, EventArgs e)
        {
            if (ProjectHasUnsavedChanges && MessageBox.Show(
                       i18n._tr("uiMessageConfirmDiscardUnsaved"),
                       i18n._tr("uiMessageConfirmDiscardUnsavedTitle"),
                       MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                saveToolStripButton_Click(this, new EventArgs());
            };
            LoadConfig((sender as ToolStripMenuItem).Text);
        } //recentMenuItem_Click()

        /// <summary>
        /// loads the according config given by filename
        /// </summary>        
        public void LoadConfig(string fileName, bool merge = false)
        {
            if (!System.IO.File.Exists(fileName))
            {
                MessageBox.Show(i18n._tr("uiMessageConfigNotFound"), i18n._tr("Hint"));
                return;
            }

            if (fileName.IndexOf(".aic") != -1)
            {
                if (MessageBox.Show(i18n._tr("uiMessageMigrateConfigFileYesNo"), i18n._tr("Hint"), MessageBoxButtons.OKCancel) == System.Windows.Forms.DialogResult.Cancel)
                {
                    return;
                }

                SaveFileDialog fd = new SaveFileDialog
                {
                    FileName = fileName.Replace(".aic", ".mcc"),
                    Filter = "MobiFlight Connector Config (*.mcc)|*.mcc"
                };
                if (DialogResult.OK != fd.ShowDialog())
                {
                    return;
                }

                String file = System.IO.File.ReadAllText(fileName);
                String newFile = file.Replace("ArcazeFsuipcConnector", "MFConnector");
                System.IO.File.WriteAllText(fd.FileName, newFile);
                fileName = fd.FileName;
            }
            else
            {
                String file = System.IO.File.ReadAllText(fileName);
                if (file.IndexOf("ArcazeUSB.ArcazeConfigItem") != -1)
                {
                    SaveFileDialog fd = new SaveFileDialog
                    {
                        FileName = fileName.Replace(".mcc", "_v6.0.mcc")
                    };

                    if (MessageBox.Show(i18n._tr("uiMessageMigrateConfigFileV60YesNo"), i18n._tr("Hint"), MessageBoxButtons.YesNo) == System.Windows.Forms.DialogResult.Yes)
                    {
                        fd.Filter = "MobiFlight Connector Config (*.mcc)|*.mcc";
                        if (DialogResult.OK != fd.ShowDialog())
                        {
                            return;
                        }
                    }

                    String newFile = file.Replace("ArcazeUSB.ArcazeConfigItem", "MobiFlight.OutputConfigItem");
                    System.IO.File.WriteAllText(fd.FileName, newFile);
                    fileName = fd.FileName;
                }
            }

            execManager.Stop();

            try
            {
                if (!merge)
                {
                    var newProject = new Project() { FilePath = fileName };
                    newProject.OpenFile();
                    execManager.Project = newProject;
                }
                else
                {
                    // this is the old logic
                    // we simply add the second file to the first file
                    // this will have to be changed in the future
                    var additionalProject = new Project() { FilePath = fileName };
                    additionalProject.OpenFile();
                    execManager.Project.ConfigFiles.Add(additionalProject.ConfigFiles.First());
                }

                execManager.Project.ConfigFiles.ToList().ForEach(configFile =>
                {
                    if (!configFile.HasDuplicateGuids()) return;
                    Log.Instance.log($"{configFile.FileName} has duplicate GUIDs and will be fixed.", LogSeverity.Warn);
                    configFile.RemoveDuplicateGuids();
                    ProjectOrConfigFileHasChanged();
                });
            }
            catch (InvalidExpressionException)
            {
                // no inputs configured... old format... just ignore
            }
            catch (Exception ex)
            {
                Log.Instance.log($"Unable to load configuration file: {ex.Message}", LogSeverity.Error);
                MessageBox.Show(i18n._tr("uiMessageProblemLoadingConfig"), i18n._tr("Hint"));
                return;
            }

            if (!merge)
            {
                // the original file name has to be stored
                // in the list of recent files.
                _storeAsRecentFile(execManager.Project.FilePath);

                // set the button back to "disabled"
                // since due to initiliazing the dataSet
                // it will automatically gets enabled
                ResetProjectAndConfigChanges();
            }
            else
            {
                // indicate that the merge changed
                // the current config and that the user
                ProjectOrConfigFileHasChanged();
            }

            // always put this after "normal" initialization
            // savetoolstripbutton may be set to "enabled"
            // if user has changed something
            _checkForOrphanedSerials(false);
            _checkForOrphanedJoysticks(false);
            _checkForOrphanedMidiBoards(false);

            // Track config loaded event
            AppTelemetry.Instance.ProjectLoaded(execManager.Project);
            AppTelemetry.Instance.TrackBoardStatistics(execManager);
            AppTelemetry.Instance.TrackSettings();

            ProjectLoaded?.Invoke(this, execManager.Project);
        }

        private void ResetProjectAndConfigChanges()
        {
            ProjectHasUnsavedChanges = false;
            SetProjectNameInTitle();
        }

        private void _checkForOrphanedJoysticks(bool showNotNecessaryMessage)
        {
            List<string> serials = new List<string>();
            List<string> NotConnectedJoysticks = new List<string>();

            foreach (Joystick j in execManager.GetJoystickManager().GetJoysticks())
            {
                serials.Add($"{j.Name} {SerialNumber.SerialSeparator}{j.Serial}");
            }

            if (execManager.Project == null) return;

            var allConfigItems = execManager.Project.ConfigFiles.SelectMany(file => file.ConfigItems).ToList();

            foreach (IConfigItem item in allConfigItems)
            {
                if (item.ModuleSerial.Contains(Joystick.SerialPrefix) &&
                    !serials.Contains(item.ModuleSerial) &&
                    !NotConnectedJoysticks.Contains(item.ModuleSerial))
                {
                    NotConnectedJoysticks.Add(item.ModuleSerial);
                }
            }

            if (NotConnectedJoysticks.Count > 0)
            {
                TimeoutMessageDialog tmd = new TimeoutMessageDialog();
                tmd.HasCancelButton = false;
                tmd.StartPosition = FormStartPosition.CenterParent;
                tmd.Message = string.Format(
                                    i18n._tr("uiMessageNotConnectedJoysticksInConfigFound"),
                                    string.Join("\n", NotConnectedJoysticks)
                                    );
                tmd.Text = i18n._tr("Hint");
                tmd.ShowDialog();
            }
            else if (showNotNecessaryMessage)
            {
                TimeoutMessageDialog.Show(i18n._tr("uiMessageNoNotConnectedJoysticksInConfigFound"), i18n._tr("Hint"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void _checkForOrphanedMidiBoards(bool showNotNecessaryMessage)
        {
            List<string> serials = new List<string>();
            List<string> NotConnectedMidiBoards = new List<string>();

            foreach (MidiBoard mb in execManager.GetMidiBoardManager().GetMidiBoards())
            {
                serials.Add($"{mb.Name} {SerialNumber.SerialSeparator}{mb.Serial}");
            }

            if (execManager.Project == null) return;

            var allConfigItems = execManager.Project.ConfigFiles.SelectMany(file => file.ConfigItems).ToList();

            foreach (IConfigItem item in allConfigItems)
            {
                if (item.ModuleSerial.Contains(MidiBoard.SerialPrefix) &&
                    !serials.Contains(item.ModuleSerial) &&
                    !NotConnectedMidiBoards.Contains(item.ModuleSerial))
                {
                    NotConnectedMidiBoards.Add(item.ModuleSerial);
                }
            }

            if (NotConnectedMidiBoards.Count > 0)
            {
                TimeoutMessageDialog tmd = new TimeoutMessageDialog();
                tmd.HasCancelButton = false;
                tmd.StartPosition = FormStartPosition.CenterParent;
                tmd.Message = string.Format(
                                    i18n._tr("uiMessageNotConnectedMidiBoardsInConfigFound"),
                                    string.Join("\n", NotConnectedMidiBoards)
                                    );
                tmd.Text = i18n._tr("Hint");
                tmd.ShowDialog();
            }
            else if (showNotNecessaryMessage)
            {
                TimeoutMessageDialog.Show(i18n._tr("uiMessageNoNotConnectedMidiBoardsInConfigFound"), i18n._tr("Hint"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void _checkForOrphanedSerials(bool showNotNecessaryMessage)
        {
            List<string> serials = new List<string>();

            foreach (IModuleInfo moduleInfo in execManager.GetAllConnectedModulesInfo())
            {
                serials.Add($"{moduleInfo.Name}{SerialNumber.SerialSeparator}{moduleInfo.Serial}");
            }

            foreach (var joystick in execManager.GetJoystickManager().GetJoysticks())
            {
                // Extra space between Name and Separator is necessary!
                serials.Add($"{joystick.Name} {SerialNumber.SerialSeparator}{joystick.Serial}");
            }

            if (serials.Count == 0) return;

            try
            {
                var allConfigItems = execManager.Project.ConfigFiles.Select(file => file.ConfigItems).ToList();
                OrphanedSerialsDialog opd = new OrphanedSerialsDialog(serials, allConfigItems);

                void UpdateProject()
                {
                    ProjectHasUnsavedChanges = opd.HasChanged();
                    var updatedConfigs = opd.GetUpdatedConfigs();

                    for (int i = 0; i < execManager.Project.ConfigFiles.Count; i++)
                    {
                        execManager.Project.ConfigFiles[i].ConfigItems = updatedConfigs[i];
                    }

                    MessageExchange.Instance.Publish(execManager.Project);
                }
                
                opd.StartPosition = FormStartPosition.CenterParent;
                if (opd.HasOrphanedSerials())
                {
                    if (opd.ShowDialog() == DialogResult.OK)
                    {
                        UpdateProject();
                    }
                }
                else if (opd.HasChanged())
                {
                    // If there are no orphaned serials, serials can still be changed automatically
                    UpdateProject();
                }
                else if (showNotNecessaryMessage)
                {
                    TimeoutMessageDialog.Show(i18n._tr("uiMessageNoOrphanedSerialsFound"), i18n._tr("Hint"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                // do nothing
                Log.Instance.log($"Orphaned serials exception: {ex.Message}", LogSeverity.Error);
            }
        }

        private void SetTitle(string title)
        {
            string NewTitle = $"MobiFlight Connector - {DisplayVersion()}";
            var saveStatus = ProjectHasUnsavedChanges ? "*" : string.Empty;

            if (title != null && title != "")
            {
                NewTitle = $"{title}{saveStatus} - {NewTitle}";
            }

            Text = NewTitle;
        }

        public static String DisplayVersion()
        {
            if (VersionBeta.Split('.')[3] != "0")
            {
                return VersionBeta + " (BETA)";
            }

            return Version;
        }

        public static String CurrentVersion()
        {
            if (VersionBeta.Split('.')[3] != "0")
                return VersionBeta;

            return Version;
        }

        private void SetProjectNameInTitle()
        {
            SetTitle(execManager.Project.Name);
        }

        /// <summary>
        /// saves the current config to filename
        /// </summary>        
        private void SaveConfig(string fileName)
        {
            execManager.Project.FilePath = fileName;
            // Issue 1401: Saving the file can result in an UnauthorizedAccessException, so catch any
            // errors during save and show it in a dialog instead of crashing.
            try
            {
                execManager.Project.SaveFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to save: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _storeAsRecentFile(execManager.Project.FilePath);
            ResetProjectAndConfigChanges();
        }

        private void UpdateSimConnectStatusIcon()
        {
            simConnectToolStripMenuItem.Image = Properties.Resources.warning;
            simConnectToolStripMenuItem.Visible = true;
            simConnectToolStripMenuItem.Enabled = true;
            simConnectToolStripMenuItem.ToolTipText = "Some configs are using MSFS2020 presets -> WASM module required";

            if (!ContainsConfigOfSourceType(execManager.ConfigItems, new SimConnectSource()))
            {
                simConnectToolStripMenuItem.Image = Properties.Resources.disabled;
                simConnectToolStripMenuItem.Visible = false;
                simConnectToolStripMenuItem.Enabled = false;
                UpdateSeparatorInStatusMenu();
                return;
            }

            if (execManager.GetSimConnectCache().IsConnected())
                simConnectToolStripMenuItem.Image = Properties.Resources.check;
            else
                SimConnectionIconStatusToolStripStatusLabel.Image = Properties.Resources.warning;

            UpdateSeparatorInStatusMenu();
        }

        private void UpdateSeparatorInStatusMenu()
        {
            separatorToolStripMenuItem.Visible = simConnectToolStripMenuItem.Enabled || xPlaneDirectToolStripMenuItem.Enabled || FsuipcToolStripMenuItem.Enabled || proSimToolStripMenuItem.Enabled;
        }

        private void UpdateXplaneDirectConnectStatusIcon()
        {
            xPlaneDirectToolStripMenuItem.Image = Properties.Resources.warning;
            xPlaneDirectToolStripMenuItem.Visible = true;
            xPlaneDirectToolStripMenuItem.Enabled = true;
            xPlaneDirectToolStripMenuItem.ToolTipText = "Some configs are using XPlane DataRefs/Commands -> XPlane direct required";

            if (!ContainsConfigOfSourceType(execManager.ConfigItems, new XplaneSource()))
            {
                xPlaneDirectToolStripMenuItem.Image = Properties.Resources.disabled;
                xPlaneDirectToolStripMenuItem.Visible = false;
                xPlaneDirectToolStripMenuItem.Enabled = false;
                UpdateSeparatorInStatusMenu();
                return;
            }

            if (execManager.GetXPlaneConnectCache().IsConnected())
                xPlaneDirectToolStripMenuItem.Image = Properties.Resources.check;
            else
                SimConnectionIconStatusToolStripStatusLabel.Image = Properties.Resources.warning;

            UpdateSeparatorInStatusMenu();
        }

        private void UpdateFsuipcStatusIcon()
        {
            FsuipcToolStripMenuItem.Image = Properties.Resources.warning;
            FsuipcToolStripMenuItem.Visible = true;
            FsuipcToolStripMenuItem.Enabled = true;
            FsuipcToolStripMenuItem.ToolTipText = "Some configs are using FSUIPC -> FSUIPC required";

            if (!ContainsConfigOfSourceType(execManager.ConfigItems, new FsuipcSource()))
            {
                FsuipcToolStripMenuItem.Image = Properties.Resources.disabled;
                FsuipcToolStripMenuItem.Visible = false;
                FsuipcToolStripMenuItem.Enabled = false;
                UpdateSeparatorInStatusMenu();
                return;
            }

            if (execManager.GetFsuipcConnectCache().IsConnected())
                FsuipcToolStripMenuItem.Image = Properties.Resources.check;
            else
                SimConnectionIconStatusToolStripStatusLabel.Image = Properties.Resources.warning;

            UpdateSeparatorInStatusMenu();
        }

        private void UpdateProSimStatusIcon()
        {
            proSimToolStripMenuItem.Image = Properties.Resources.warning;
            proSimToolStripMenuItem.Visible = true;
            proSimToolStripMenuItem.Enabled = true;


            if (execManager.GetProSimCache().IsConnected())
            {
                proSimToolStripMenuItem.Image = Properties.Resources.check;
            }

            UpdateSeparatorInStatusMenu();
        }

        private void UpdateSimStatusIcon()
        {
            if (execManager.SimConnected())
            {
                SimConnectionIconStatusToolStripStatusLabel.Image = Properties.Resources.check;
            }
        }

        /// <summary>
        /// shows the about form
        /// </summary>
        public void AboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            AboutForm ab = new AboutForm();
            ab.StartPosition = FormStartPosition.CenterParent;
            ab.ShowDialog();
        } //aboutToolStripMenuItem_Click()

        /// <summary>
        /// resets the config after presenting a message box where user hast to confirm the reset first
        /// </summary>
        public void newFileToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (ProjectHasUnsavedChanges && MessageBox.Show(
                       i18n._tr("uiMessageConfirmNewConfig"),
                       i18n._tr("uiMessageConfirmNewConfigTitle"),
                       MessageBoxButtons.OKCancel) == DialogResult.Cancel)
            {
                return;
            }

            CreateNewProject();
        } //toolStripMenuItem3_Click()

        public void CreateNewProject()
        {
            var project = new Project() { Name = i18n._tr("DefaultProjectName") };
            project.ConfigFiles.Add(CreateDefaultConfigFile());
            execManager.Project = project;
            ResetProjectAndConfigChanges();
        }

        public void AddNewFileToProject()
        {
            execManager.Stop();

            ConfigFile newConfigFile = CreateDefaultConfigFile();
            execManager.Project.ConfigFiles.Add(newConfigFile);

            ProjectOrConfigFileHasChanged();


            ProjectLoaded?.Invoke(this, execManager.Project);
        }

        private static ConfigFile CreateDefaultConfigFile()
        {
            return new ConfigFile()
            {
                Label = "New file",
                EmbedContent = true
            };
        }

        /// <summary>
        /// gets triggered if user uses quick save button from toolbar
        /// </summary>
        public void saveToolStripButton_Click(object sender, EventArgs e)
        {
            // if filename of loaded file is known use it
            if (execManager.Project.FilePath != null)
            {
                SaveConfig(execManager.Project.FilePath);
                return;
            }
            // otherwise trigger normal open file dialog
            saveAsToolStripMenuItem_Click(sender, e);
        } //saveToolStripButton_Click()

        /// <summary>
        /// triggers the save dialog if user clicks on according buttons
        /// </summary>
        public void saveAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveFileDialog fd = new SaveFileDialog();
            fd.FileName = execManager.Project.Name;

            if (execManager.Project.FilePath != null)
            {
                fd.InitialDirectory = Path.GetDirectoryName(execManager.Project.FilePath);
                fd.FileName = Path.GetFileNameWithoutExtension(execManager.Project.FilePath);
            }

            fd.Filter = fileExtensionSaveFilter;
            if (DialogResult.OK == fd.ShowDialog())
            {
                SaveConfig(fd.FileName);
            }
        } //saveToolStripMenuItem_Click()

        private void TaskBar_StartProjectExecution(object sender, EventArgs e)
        {
            StartProjectExecution();
        }

        private void TaskBar_StopExecution(object sender, EventArgs e)
        {
            StopExecution();
        }

        /// <summary>
        /// toggles the current timer when user clicks on respective run/stop buttons
        /// </summary>
        public void StartProjectExecution()
        {
            if (execManager.IsStarted()) return;

            execManager.Start();
        } //buttonToggleStart_Click()

        public void StartTestModeExecution()
        {
            execManager.TestModeStart();
        }

        public void StopExecution()
        {
            execManager.Stop();
            execManager.TestModeStop();
        }

        public void ToggleAutoRunSetting()
        {
            setAutoRunValue(!Properties.Settings.Default.AutoRun);
        }

        public void RenameProject(string newName)
        {
            if (string.IsNullOrEmpty(newName)) return;
            execManager.Project.Name = newName;
        }

        private void setAutoRunValue(bool value)
        {
            Properties.Settings.Default.AutoRun = value;
        }

        public void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (ShowSettingsDialog("GeneralTabPage", null, null, null) == System.Windows.Forms.DialogResult.OK)
            {
#if ARCAZE
                execManager.updateModuleSettings(execManager.getModuleCache().GetArcazeModuleSettings());
#endif
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            execManager.Stop();
            if (ProjectHasUnsavedChanges && MessageBox.Show(
                       i18n._tr("uiMessageConfirmDiscardUnsaved"),
                       i18n._tr("uiMessageConfirmDiscardUnsavedTitle"),
                       MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                // only cancel closing if not saved before
                // which is indicated by empty CurrentFilename
                e.Cancel = (execManager.Project.FilePath == null);
                saveToolStripButton_Click(this, new EventArgs());
            };
        }

        public void documentationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start(i18n._tr("WebsiteUrlHelp"));
        }

        public void orphanedSerialsFinderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            _checkForOrphanedSerials(true);
        }

        public void donateToolStripButton_Click(object sender, EventArgs e)
        {
            Process.Start("https://www.paypal.com/cgi-bin/webscr?cmd=_s-xclick&hosted_button_id=7GV3DCC7BXWLY");
        }

        private void MainForm_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)       // Ctrl-S Save
            {
                // Do what you want here
                e.SuppressKeyPress = true;  // Stops bing! Also sets handled which stop event bubbling
                if (ProjectHasUnsavedChanges)
                    saveToolStripButton_Click(null, null);
            }

            if (e.Control && (e.KeyCode == Keys.D0 || e.KeyCode == Keys.NumPad0))
            {
                frontendPanel1.SetZoomFactor(1.0f);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_SHOWME)
            {
                ShowMe();
            }
            if (m.Msg == SimConnectMSFS.SimConnectCache.WM_USER_SIMCONNECT) execManager?.HandleWndProc(ref m);

            base.WndProc(ref m);
        }

        private void ShowMe()
        {
            minimizeMainForm(false);
        }

        public void installWasmModuleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            InstallWasmModule();
        }

        private void InstallWasmModule()
        {
            WasmModuleUpdater updater = new WasmModuleUpdater();
            bool Is2020Different = false;
            bool Is2024Different = false;
            bool Update2020Successful = false;
            bool Update2024Successful = false;

            try
            {

                if (!updater.AutoDetectCommunityFolder())
                {
                    TimeoutMessageDialog.Show(
                       i18n._tr("uiMessageWasmUpdateCommunityFolderNotFound"),
                       i18n._tr("uiMessageWasmUpdater"),
                       MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                Is2020Different = updater.WasmModulesAreDifferent(updater.CommunityFolder);
                Is2024Different = updater.WasmModulesAreDifferent(updater.CommunityFolder2024);

                // If neither are different then just tell the user and return, doing nothing.
                if (!Is2020Different && !Is2024Different)
                {
                    TimeoutMessageDialog.Show(
                       i18n._tr("uiMessageWasmUpdateAlreadyInstalled"),
                       i18n._tr("uiMessageWasmUpdater"),
                       MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Issue 1872: If the sim is running warn the user then bail
                if (IsMSFSRunning)
                {
                    MessageBox.Show(
                       i18n._tr("uiMessageWasmMSFSRunning"),
                       i18n._tr("uiMessageWasmUpdater"),
                       MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (Is2020Different)
                {
                    Update2020Successful = HandleWasmInstall(updater, updater.CommunityFolder, "2020");
                }
                else
                {
                    Log.Instance.log($"WASM module for MSFS2020 is already up-to-date.", LogSeverity.Info);
                }

                if (Is2024Different)
                {
                    Update2024Successful = HandleWasmInstall(updater, updater.CommunityFolder2024, "2024");
                }
                else
                {
                    Log.Instance.log($"WASM module for MSFS2024 is already up-to-date.", LogSeverity.Info);
                }

                // If either update is successful then show the success dialog.
                if (Update2020Successful || Update2024Successful)
                {
                    TimeoutMessageDialog.Show(
                       i18n._tr("uiMessageWasmUpdateInstallationSuccessful"),
                       i18n._tr("uiMessageWasmUpdater"),
                       MessageBoxButtons.OK, MessageBoxIcon.Information);

                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Instance.log(ex.Message, LogSeverity.Error);
            }

            // We only get here in case of an error.
            TimeoutMessageDialog.Show(
                i18n._tr("uiMessageWasmUpdateInstallationError"),
                i18n._tr("uiMessageWasmUpdater"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>
        /// Handles all the necessary and log messages for installing the WASM with different versions of MSFS.
        /// </summary>
        /// <param name="updater">The WASM updater to use</param>
        /// <param name="communityFolder">The path to the community folder</param>
        /// <param name="msfsVersion">The version of MSFS, either "2020" or "2024"</param>
        /// <returns></returns>
        private static bool HandleWasmInstall(WasmModuleUpdater updater, string communityFolder, string msfsVersion)
        {
            if (String.IsNullOrEmpty(communityFolder))
            {
                Log.Instance.log($"Skipping WASM install for MSFS{msfsVersion} since no community folder was found. This likely means MSFS{msfsVersion} is not installed.", LogSeverity.Info);
                return true;
            }

            bool result = updater.InstallWasmModule(communityFolder);

            if (result)
            {
                Log.Instance.log($"Successfully installed WASM module for MSFS{msfsVersion}.", LogSeverity.Info);
            }

            return result;
        }

        private void downloadLatestEventsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            WasmModuleUpdater updater = new WasmModuleUpdater();
            ProgressForm progressForm = new ProgressForm();
            Control MainForm = this;

            updater.DownloadAndInstallProgress += progressForm.OnProgressUpdated;
            Task.Run(async () =>
                {
                    if (!updater.AutoDetectCommunityFolder())
                    {
                        Log.Instance.log(i18n._tr("uiMessageWasmUpdateCommunityFolderNotFound"), LogSeverity.Error);
                        return;
                    }

                    if (await updater.InstallWasmEvents())
                    {
                        progressForm.DialogResult = DialogResult.OK;
                    }
                    else
                    {
                        progressForm.DialogResult = DialogResult.No;
                        Log.Instance.log(i18n._tr("uiMessageWasmEventsInstallationError"), LogSeverity.Error);
                    }
                }
            );

            if (progressForm.ShowDialog() == DialogResult.OK)
            {
                TimeoutMessageDialog.Show(
                   i18n._tr("uiMessageWasmEventsInstallationSuccessful"),
                   i18n._tr("uiMessageWasmUpdater"),
                   MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                TimeoutMessageDialog.Show(
                    i18n._tr("uiMessageWasmEventsInstallationError"),
                    i18n._tr("uiMessageWasmUpdater"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            progressForm.Dispose();
        }

        private void DownloadHubHopPresets()
        {
            WasmModuleUpdater updater = new WasmModuleUpdater();
            ProgressForm progressForm = new ProgressForm();
            Control MainForm = this;

            progressForm.Text = i18n._tr("uiTitleHubhopAutoUpdate");
            updater.DownloadAndInstallProgress += progressForm.OnProgressUpdated;

            Task.Run(async () =>
            {
                if (await updater.DownloadHubHopPresets())
                {
                    Msfs2020HubhopPresetListSingleton.Instance.Clear();
                    XplaneHubhopPresetListSingleton.Instance.Clear();
                    progressForm.DialogResult = DialogResult.OK;
                }
                else
                {
                    progressForm.DialogResult = DialogResult.No;
                    Log.Instance.log(i18n._tr("uiMessageHubHopUpdateError"), LogSeverity.Error);
                }
            });

            if (progressForm.ShowDialog() == DialogResult.OK)
            {
                TimeoutMessageDialog.Show(
                   i18n._tr("uiMessageHubHopUpdateSuccessful"),
                   i18n._tr("uiMessageWasmUpdater"),
                   MessageBoxButtons.OK, MessageBoxIcon.Information);

                var lastModification = WasmModuleUpdater.HubHopPresetTimestamp();
                UpdateHubHopTimestampInStatusBar(lastModification);
            }
            else
            {
                TimeoutMessageDialog.Show(
                    i18n._tr("uiMessageWasmEventsInstallationError"),
                    i18n._tr("uiMessageWasmUpdater"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            progressForm.Dispose();
        }

        public void downloadHubHopPresetsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DownloadHubHopPresets();
        }

        private void UpdateHubHopTimestampInStatusBar(DateTime lastModification)
        {
            toolStripStatusLabelHubHop.Text = lastModification.ToLocalTime().ToShortDateString();
            toolStripStatusLabelHubHop.ToolTipText = lastModification.ToLocalTime().ToString();
        }

        public void openDiscordServer_Click(object sender, EventArgs e)
        {
            Process.Start("https://discord.gg/U28QeEJpBV");
        }

        private void StatusBarToolStripButton_Click(object sender, EventArgs e)
        {
            ShowSettingsDialog("mobiFlightTabPage", null, null, null);
        }

        public void YouTubeToolStripButton_Click(object sender, EventArgs e)
        {
            Process.Start("https://www.youtube.com/channel/UCxsoCWDKRyu3MpQKNZEXUYA");
        }

        public void HubHopToolStripButton_Click(object sender, EventArgs e)
        {
            Process.Start("https://hubhop.mobiflight.com/");
        }

        public void releaseNotesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Process.Start($"https://github.com/MobiFlight/MobiFlight-Connector/releases/tag/{CurrentVersion()}");
        }

        public static bool ContainsConfigOfSourceType(List<IConfigItem> configItems, Source type)
        {
            var result = false;
            if (type is SimConnectSource)
            {
                result = configItems
                        .Any(x => x is OutputConfigItem && (x as OutputConfigItem)?.Source is SimConnectSource) ||
                        configItems
                        .Any(x => x is InputConfigItem && (x as InputConfigItem)?.GetInputActionsByType(typeof(MSFS2020CustomInputAction)).Count > 0);
            }
            else if (type is FsuipcSource)
            {
                result = configItems
                        .Any(x => x is OutputConfigItem && (x as OutputConfigItem)?.Source is FsuipcSource) ||
                         configItems
                        .Any(x => x is InputConfigItem &&
                                  (
                                  (x as InputConfigItem)?.GetInputActionsByType(typeof(FsuipcOffsetInputAction)).Count > 0 ||
                                  (x as InputConfigItem)?.GetInputActionsByType(typeof(EventIdInputAction)).Count > 0 ||
                                  (x as InputConfigItem)?.GetInputActionsByType(typeof(PmdgEventIdInputAction)).Count > 0 ||
                                  (x as InputConfigItem)?.GetInputActionsByType(typeof(JeehellInputAction)).Count > 0 ||
                                  (x as InputConfigItem)?.GetInputActionsByType(typeof(LuaMacroInputAction)).Count > 0
                                  )
                                  );
            }
            else if (type is XplaneSource)
            {
                result = configItems
                        .Any(x => x is OutputConfigItem && (x as OutputConfigItem)?.Source is XplaneSource) ||
                         configItems
                        .Any(x => x is InputConfigItem && (x as InputConfigItem)?.GetInputActionsByType(typeof(XplaneInputAction)).Count > 0);
            }
            else if (type is VariableSource)
            {
                result = configItems
                        .Any(x => x is OutputConfigItem && (x as OutputConfigItem)?.Source is VariableSource) ||
                         configItems
                        .Any(x => x is InputConfigItem && (x as InputConfigItem)?.GetInputActionsByType(typeof(VariableInputAction)).Count > 0);
            }
            else if (type is ProSimSource)
            {
                result = configItems
                        .Any(x => x is OutputConfigItem && (x as OutputConfigItem)?.Source is ProSimSource) ||
                         configItems
                        .Any(x => x is InputConfigItem && (x as InputConfigItem)?.GetInputActionsByType(typeof(ProSimInputAction)).Count > 0);
            }
            return result;
        }


        private void RestoreAutoLoadConfig()
        {
            AutoLoadConfigs = JsonConvert.DeserializeObject<Dictionary<string, string>>(Properties.Settings.Default.AutoLoadLinkedConfigList);
            if (AutoLoadConfigs == null)
                AutoLoadConfigs = new Dictionary<string, string>();
            ;
        }

        private void SaveAutoLoadConfig()
        {
            Properties.Settings.Default.AutoLoadLinkedConfigList = JsonConvert.SerializeObject(AutoLoadConfigs);
            Properties.Settings.Default.Save();
            UpdateAutoLoadMenu();
        }

        private void UpdateAutoLoadConfig()
        {
            autoloadToggleToolStripMenuItem.Checked = Properties.Settings.Default.AutoLoadLinkedConfig;
        }

        private void autoloadToggleToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.AutoLoadLinkedConfig = !Properties.Settings.Default.AutoLoadLinkedConfig;
            UpdateAutoLoadConfig();
        }

        private void linkCurrentConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var aircraftName = toolStripAircraftDropDownButton.Text ?? string.Empty;
            var key = $"{FlightSim.FlightSimType}:{aircraftName}";

            AutoLoadConfigs[key] = execManager.Project.FilePath;

            SaveAutoLoadConfig();
        }

        private void unlinkConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var aircraftName = toolStripAircraftDropDownButton.Text ?? string.Empty;
            var key = $"{FlightSim.FlightSimType}:{aircraftName}";
            toolStripAircraftDropDownButton.Image = null;

            if (!AutoLoadConfigs.Remove(key)) return;

            SaveAutoLoadConfig();
        }

        protected void UpdateAutoLoadMenu()
        {
            var aircraftName = toolStripAircraftDropDownButton.Text;
            var key = $"{FlightSim.FlightSimType}:{aircraftName}";

            ResetAutoLoadMenu();

            if (!AutoLoadConfigs.ContainsKey(key)) return;
            var linkedFile = AutoLoadConfigs[key];

            UpdateAutoLoadMenuWithLinkedFile(linkedFile);
        }

        private void UpdateAutoLoadMenuWithLinkedFile(string linkedFile)
        {

            if (string.IsNullOrEmpty(linkedFile)) return;

            toolStripAircraftDropDownButton.Image = Properties.Resources.warning;
            linkCurrentConfigToolStripMenuItem.Enabled = (execManager?.Project?.FilePath != null);
            removeLinkConfigToolStripMenuItem.Enabled = true;
            openLinkedConfigToolStripMenuItem.Enabled = true;
            openLinkFilenameToolStripMenuItem.Text = linkedFile;

            if (linkedFile != execManager?.Project?.FilePath) return;

            linkCurrentConfigToolStripMenuItem.Enabled = false;
            openLinkedConfigToolStripMenuItem.Enabled = false;
            toolStripAircraftDropDownButton.Image = Properties.Resources.check;
        }

        private void ResetAutoLoadMenu()
        {
            toolStripAircraftDropDownButton.Image = null;
            linkCurrentConfigToolStripMenuItem.Enabled = false;
            removeLinkConfigToolStripMenuItem.Enabled = false;
            openLinkedConfigToolStripMenuItem.Enabled = false;
            openLinkFilenameToolStripMenuItem.Text = "";
        }

        private void openLinkedConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var aircraftName = toolStripAircraftDropDownButton.Text;
            var key = $"{FlightSim.FlightSimType}:{aircraftName}";
            if (!AutoLoadConfigs.ContainsKey(key)) return;

            var linkedFile = AutoLoadConfigs[key];

            if (ProjectHasUnsavedChanges && MessageBox.Show(
                       i18n._tr("uiMessageConfirmDiscardUnsaved"),
                       i18n._tr("uiMessageConfirmDiscardUnsavedTitle"),
                       MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                saveToolStripButton_Click(this, new EventArgs());
            };

            LoadConfig(linkedFile);
        }

        public void copyLogsToClipboardToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                logAppenderFile.CopyToClipboard();
            }
            catch (FileLoadException ex)
            {
                MessageBox.Show("No logs available to copy. Make sure logging is enabled in settings then try again.", "Copy to clipboard");
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to copy logs to the clipboard: {ex.Message}", "Copy to clipboard");
                return;
            }

            MessageBox.Show("Logs successfully copied to the clipboard.", "Copy to clipboard");
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal static class Helper
    {
        public static void DoubleBufferedDGV(this DataGridView dgv, bool setting)
        {
            Type dgvType = dgv.GetType();
            PropertyInfo pi = dgvType.GetProperty("DoubleBuffered",
                  BindingFlags.Instance | BindingFlags.NonPublic);
            pi.SetValue(dgv, setting, null);
        }
    }

    // this class just wraps some Win32 stuff that we're going to use
    internal class NativeMethods
    {
        public const int HWND_BROADCAST = 0xffff;
        public static readonly int WM_SHOWME = RegisterWindowMessage("WM_SHOWME");
        [DllImport("user32")]
        public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam);
        [DllImport("user32")]
        public static extern int RegisterWindowMessage(string message);
    }
}
