﻿using DSharpPlus.Entities;
using DSharpPlus.Exceptions;
using Eco.Core;
using Eco.Core.Plugins;
using Eco.Core.Plugins.Interfaces;
using Eco.Core.Utils;
using Eco.Core.Utils.Logging;
using Eco.EW.Tools;
using Eco.Gameplay.Aliases;
using Eco.Gameplay.Civics.Elections;
using Eco.Gameplay.GameActions;
using Eco.Gameplay.Players;
using Eco.Gameplay.Property;
using Eco.Plugins.DiscordLink.Events;
using Eco.Plugins.DiscordLink.Extensions;
using Eco.Plugins.DiscordLink.Modules;
using Eco.Plugins.DiscordLink.Utilities;
using Eco.Shared.Utils;
using Eco.WorldGenerator;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Module = Eco.Plugins.DiscordLink.Modules.Module;

namespace Eco.Plugins.DiscordLink
{
    [Priority(PriorityAttribute.High)] // Need to start before WorldGenerator in order to listen for world generation finished event
    public class DiscordLink : IModKitPlugin, IInitializablePlugin, IShutdownablePlugin, IConfigurablePlugin, IDisplayablePlugin, IGameActionAware, ICommandablePlugin
    {
        public readonly Version PluginVersion = Assembly.GetExecutingAssembly().GetName().Version;

        public static DiscordLink Obj { get { return PluginManager.GetPlugin<DiscordLink>(); } }
        public DLDiscordClient Client { get; private set; } = new DLDiscordClient();
        public Module[] Modules { get; private set; } = new Module[Enum.GetNames(typeof(ModuleType)).Length];
        public IPluginConfig PluginConfig { get { return DLConfig.Instance.PluginConfig; } }
        public ThreadSafeAction<object, string> ParamChanged { get; set; }
        public DateTime InitTime { get; private set; } = DateTime.MinValue;
        public bool CanRestart { get; private set; } = false; // False to start with as we cannot restart while the initial startup is in progress

        private const string ModIOAppID = "77";
        private const string ModIODeveloperToken = ""; // This will always be empty for all but actual release builds.

        private bool _triggerWorldResetEvent = false;

        private Action<User> OnNewUserJoined;
        private Action<User> OnNewUserLoggedIn;
        private Action<User> OnUserLoggedOut;
        private Action<Election> OnElectionStarted;
        private Action<Election> OnElectionFinished;
        private Action<DLEventArgs> OnEventConverted;
        private Action<string> OnLogWritten;
        private EventHandler<LinkedUser> OnLinkedUserVerified;
        private EventHandler<LinkedUser> OnLinkedUserRemoved;

        public override string ToString() => "DiscordLink";
        public string GetCategory() => "DiscordLink";
        public string GetStatus() => Status;
        public object GetEditObject() => DLConfig.Data;
        public void OnEditObjectChanged(object o, string param) => _ = DLConfig.Instance.HandleConfigChanged();
        public LazyResult ShouldOverrideAuth(IAlias alias, IOwned property, GameAction action) => LazyResult.FailedNoMessage;

        public string Status
        {
            get { return _status; }
            private set
            {
                Logger.Debug($"Plugin status changed from \"{_status}\" to \"{value}\"");
                _status = value;
            }
        }
        private string _status = "Uninitialized";
        private Timer _activityUpdateTimer = null;

        #region Plugin Management

        public string GetDisplayText()
        {
            try
            {
                return MessageBuilder.Shared.GetDisplayStringAsync(DLConfig.Data.UseVerboseDisplay).Result;
            }
            catch (ServerErrorException e)
            {
                Logger.Exception($"Failed to get status display string", e);
                return "Failed to generate status string";
            }
            catch (Exception e)
            {
                Logger.Exception($"Failed to get status display string", e);
                return "Failed to generate status string";
            }
        }

        public void Initialize(TimedTask timer)
        {
            InitCallbacks();
            DLConfig.Instance.Initialize();
            Logger.RegisterLogger("DiscordLink", ConsoleColor.Cyan, DLConfig.Data.LogLevel);
            Status = "Initializing";
            InitTime = DateTime.Now;

            EventConverter.Instance.Initialize();
            DLStorage.Instance.Initialize();

            WorldGeneratorPlugin.OnFinishGenerate.AddUnique(this.HandleWorldReset);
            PluginManager.Controller.RunIfOrWhenInited(PostServerInitialize); // Defer some initialization for when the server initialization is completed

            // Start the Discord client so that a connection has hopefully been established before the server is done initializing
            _ = Client.Start();

            // Check mod versioning if the required data exists
            if (!string.IsNullOrWhiteSpace(ModIOAppID) && !string.IsNullOrWhiteSpace(ModIODeveloperToken))
                VersionChecker.CheckVersion("DiscordLink", ModIOAppID, ModIODeveloperToken);
            else
                Logger.Info($"Plugin version is {PluginVersion}");
        }

        private async void PostServerInitialize()
        {
            Status = "Performing post server start initialization";

            if (string.IsNullOrEmpty(DLConfig.Data.BotToken) || Client.ConnectionStatus != DLDiscordClient.ConnectionState.Connected)
            {
                Status = "Initialization aborted";
                Client.OnConnected.Add(HandleClientConnected);
                if (!string.IsNullOrEmpty(DLConfig.Data.BotToken))
                    Logger.Error("Discord client did not connect before server initialization was completed. Use restart commands to make a new connection attempt");

                CanRestart = true;
                return;
            }

            HandleClientConnected();

            if (_triggerWorldResetEvent)
            {
                await HandleEvent(DLEventType.WorldReset, null);
                _triggerWorldResetEvent = false;
            }

            await HandleEvent(DLEventType.ServerStarted, null);
        }

        public async Task ShutdownAsync()
        {
            Status = "Shutting down";

            await HandleEvent(DLEventType.ServerStopped, null);
            ShutdownModules();
            EventConverter.Instance.Shutdown();
            DLStorage.Instance.Shutdown();
        }

        public void GetCommands(Dictionary<string, Action> nameToFunction)
        {
            nameToFunction.Add("Verify Config", () => { Logger.Info($"Config Verification Report:\n{MessageBuilder.Shared.GetConfigVerificationReport()}"); });
            nameToFunction.Add("Verify Permissions", () =>
            {
                if (Client.ConnectionStatus == DLDiscordClient.ConnectionState.Connected)
                    Logger.Info($"Permission Verification Report:\n{MessageBuilder.Shared.GetPermissionsReport(MessageBuilder.PermissionReportComponentFlag.All)}");
                else
                    Logger.Error("Failed to verify permissions - Discord client not connected");
            });
            nameToFunction.Add("Force Update", async () =>
            {
                if (Client.ConnectionStatus != DLDiscordClient.ConnectionState.Connected)
                {
                    Logger.Info("Failed to force update - Disoord client not connected");
                    return;
                }

                Modules.ForEach(async module => await module.HandleStartOrStop());
                await HandleEvent(DLEventType.ForceUpdate);
                Logger.Info("Forced update");
            });
            nameToFunction.Add("Restart Plugin", () =>
            {
                if (CanRestart)
                {
                    Logger.Info("DiscordLink Restarting...");
                    _ = Restart();
                }
                else
                {
                    Logger.Info("Could not restart - The plugin is not in a ready state.");
                }
            });
        }

        public async Task<bool> Restart()
        {
            Logger.Debug("Attempting plugin restart");

            bool result = false;
            if (CanRestart)
            {
                CanRestart = false;
                result = await Client.Restart();
                if (!result)
                    CanRestart = true; // If the client setup failed, enable restarting, otherwise we should wait for the callbacks from Discord to fire
            }
            return result;
        }

        private void HandleClientConnected()
        {
            Client.OnConnected.Remove(HandleClientConnected);

            DLConfig.Instance.PostConnectionInitialize();
            if (Client.Guild == null)
            {
                Status = "Discord Server connection failed";
                CanRestart = true;
                return;
            }

            UserLinkManager.Initialize();
            InitializeModules();

            RegisterCallbacks();
            ActionUtil.AddListener(this);
            _activityUpdateTimer = new Timer(TriggerActivityStringUpdate, null, DLConstants.DISCORD_ACTIVITY_STRING_UPDATE_INTERVAL_MS, DLConstants.DISCORD_ACTIVITY_STRING_UPDATE_INTERVAL_MS);
            Client.OnDisconnecting.Add(HandleClientDisconnecting);
            _ = HandleEvent(DLEventType.DiscordClientConnected);

            Status = "Connected and running";
            Logger.Info("Connection Successful - DiscordLink Running");
            CanRestart = true;
        }

        private void HandleClientDisconnecting()
        {
            Client.OnDisconnecting.Remove(HandleClientDisconnecting);
            DeregisterCallbacks();

            SystemUtils.StopAndDestroyTimer(ref _activityUpdateTimer);
            ActionUtil.RemoveListener(this);
            ShutdownModules();
            Client.OnConnected.Add(HandleClientConnected);

            Status = "Disconnected";
        }

        public void ActionPerformed(GameAction action)
        {
            switch (action)
            {
                case ChatSent chatSent:
                    Logger.DebugVerbose($"Eco Message Received\n{chatSent.FormatForLog()}");
                    _ = HandleEvent(DLEventType.EcoMessageSent, chatSent);
                    break;

                case CurrencyTrade currencyTrade:
                    _ = HandleEvent(DLEventType.Trade, currencyTrade);
                    break;

                case WorkOrderAction workOrderAction:
                    _ = HandleEvent(DLEventType.WorkOrderCreated, workOrderAction);
                    break;

                case PostedWorkParty postedWorkParty:
                    _ = HandleEvent(DLEventType.PostedWorkParty, postedWorkParty);
                    break;

                case CompletedWorkParty completedWorkParty:
                    _ = HandleEvent(DLEventType.CompletedWorkParty, completedWorkParty);
                    break;

                case JoinedWorkParty joinedWorkParty:
                    _ = HandleEvent(DLEventType.JoinedWorkParty, joinedWorkParty);
                    break;

                case LeftWorkParty leftWorkParty:
                    _ = HandleEvent(DLEventType.LeftWorkParty, leftWorkParty);
                    break;

                case WorkedForWorkParty workedParty:
                    _ = HandleEvent(DLEventType.WorkedWorkParty, workedParty);
                    break;

                case Vote vote:
                    _ = HandleEvent(DLEventType.Vote, vote);
                    break;

                case CreateCurrency createCurrency:
                    _ = HandleEvent(DLEventType.CurrencyCreated, createCurrency);
                    break;

                case DemographicChange demographicChange:
                    DLEventType type = demographicChange.Entered == Shared.Items.EnteredOrLeftDemographic.EnteringDemographic
                        ? DLEventType.EnteredDemographic
                        : DLEventType.LeftDemographic;
                    _ = HandleEvent(type, demographicChange);
                    break;

                case GainSpecialty gainSpecialty:
                    _ = HandleEvent(DLEventType.GainedSpecialty, gainSpecialty);
                    break;

                default:
                    break;
            }
        }

        public async Task HandleEvent(DLEventType eventType, params object[] data)
        {
            Logger.DebugVerbose($"Event of type {eventType} received");

            EventConverter.Instance.HandleEvent(eventType, data);
            DLStorage.Instance.HandleEvent(eventType, data);
            await UserLinkManager.HandleEvent(eventType, data);
            UpdateModules(eventType, data);
            UpdateActivityString(eventType);
        }

        public void HandleWorldReset()
        {
            Logger.Info("New world generated - Removing storage data for previous world");
            DLStorage.Instance.ResetWorldData();
            _triggerWorldResetEvent = true;
        }

        #endregion

        #region Module Management

        private async void InitializeModules()
        {
            Status = "Initializing modules";

            Modules[(int)ModuleType.CurrencyDisplay] = new CurrencyDisplay();
            Modules[(int)ModuleType.ElectionDisplay] = new ElectionDisplay();
            Modules[(int)ModuleType.ServerInfoDisplay] = new ServerInfoDisplay();
            Modules[(int)ModuleType.TradeWatcherDisplay] = new TradeWatcherDisplay();
            Modules[(int)ModuleType.WorkPartyDisplay] = new WorkPartyDisplay();
            Modules[(int)ModuleType.CraftingFeed] = new CraftingFeed();
            Modules[(int)ModuleType.DiscordChatFeed] = new DiscordChatFeed();
            Modules[(int)ModuleType.EcoChatFeed] = new EcoChatFeed();
            Modules[(int)ModuleType.ElectionFeed] = new ElectionFeed();
            Modules[(int)ModuleType.PlayerStatusFeed] = new PlayerStatusFeed();
            Modules[(int)ModuleType.ServerLogFeed] = new ServerLogFeed();
            Modules[(int)ModuleType.ServerStatusFeed] = new ServerStatusFeed();
            Modules[(int)ModuleType.TradeFeed] = new TradeFeed();
            Modules[(int)ModuleType.TradeWatcherFeed] = new TradeWatcherFeed();
            Modules[(int)ModuleType.AccountLinkRoleModule] = new AccountLinkRoleModule();
            Modules[(int)ModuleType.DemographicRoleModule] = new DemographicsRoleModule();
            Modules[(int)ModuleType.RoleCleanupModule] = new RoleCleanupModule();
            Modules[(int)ModuleType.SpecialitiesRoleModule] = new SpecialtiesRoleModule();
            Modules[(int)ModuleType.SnippetInput] = new SnippetInput();

            foreach (Module module in Modules)
            {
                module.Setup();
            }
            foreach (Module module in Modules)
            {
                await module.HandleStartOrStop();
            }
        }

        private async void ShutdownModules()
        {
            Status = "Shutting down modules";

            foreach (Module module in Modules)
            {
                await module.Stop();
            }
            foreach (Module module in Modules)
            {
                module.Destroy();
            }
            Modules = new Module[Enum.GetNames(typeof(ModuleType)).Length];
        }

        private async void UpdateModules(DLEventType trigger, params object[] data)
        {
            foreach (Module module in Modules.NonNull())
            {
                try
                {
                    await module.Update(this, trigger, data);
                }
                catch (Exception e)
                {
                    Logger.Exception($"An error occurred while updating module: {module}", e);
                }
            }
        }

        private void TriggerActivityStringUpdate(object stateInfo)
        {
            UpdateActivityString(DLEventType.Timer);
        }

        private void UpdateActivityString(DLEventType trigger)
        {
            try
            {
                if (Client.ConnectionStatus != DLDiscordClient.ConnectionState.Connected
                    || (trigger & (DLEventType.Join | DLEventType.Login | DLEventType.Logout | DLEventType.Timer)) == 0)
                    return;

                Client.DiscordClient.UpdateStatusAsync(new DiscordActivity(MessageBuilder.Discord.GetActivityString(), ActivityType.Watching));
            }
            catch (Exception e)
            {
                Logger.Exception($"An error occured while attempting to update the activity string", e);
            }
        }

        #endregion

        private void InitCallbacks()
        {
            OnNewUserJoined = async user => await HandleEvent(DLEventType.Join, user);
            OnNewUserLoggedIn = async user => await HandleEvent(DLEventType.Login, user);
            OnUserLoggedOut = async user => await HandleEvent(DLEventType.Logout, user);
            OnElectionStarted = async election => await HandleEvent(DLEventType.StartElection, election);
            OnElectionFinished = async election => await HandleEvent(DLEventType.StopElection, election);
            OnEventConverted = async args => await HandleEvent(args.EventType, args.Data);
            OnLinkedUserVerified = async (sender, args) => await HandleEvent(DLEventType.AccountLinkVerified, args);
            OnLinkedUserRemoved = async (sender, args) => await HandleEvent(DLEventType.AccountLinkRemoved, args);
            OnLogWritten = EventConverter.Instance.ConvertServerLogEvent;
        }

        private void RegisterCallbacks()
        {
            UserManager.NewUserJoinedEvent.Add(OnNewUserJoined);
            UserManager.OnUserLoggedIn.Add(OnNewUserLoggedIn);
            UserManager.OnUserLoggedOut.Add(OnUserLoggedOut);
            Election.ElectionStartedEvent.Add(OnElectionStarted);
            Election.ElectionFinishedEvent.Add(OnElectionFinished);
            EventConverter.OnEventConverted.Add(OnEventConverted);
            ClientLogEventTrigger.OnLogWritten += (message) => EventConverter.Instance.ConvertServerLogEvent(message);
            UserLinkManager.OnLinkedUserVerified += OnLinkedUserVerified;
            UserLinkManager.OnLinkedUserRemoved += OnLinkedUserRemoved;
        }

        private void DeregisterCallbacks()
        {
            UserManager.NewUserJoinedEvent.Remove(OnNewUserJoined);
            UserManager.OnUserLoggedIn.Remove(OnNewUserLoggedIn);
            UserManager.OnUserLoggedOut.Remove(OnUserLoggedOut);
            Election.ElectionStartedEvent.Remove(OnElectionStarted);
            Election.ElectionFinishedEvent.Remove(OnElectionFinished);
            EventConverter.OnEventConverted.Remove(OnEventConverted);
            ClientLogEventTrigger.OnLogWritten -= (message) => EventConverter.Instance.ConvertServerLogEvent(message);
            UserLinkManager.OnLinkedUserVerified -= OnLinkedUserVerified;
            UserLinkManager.OnLinkedUserRemoved -= OnLinkedUserRemoved;
        }
    }
}
