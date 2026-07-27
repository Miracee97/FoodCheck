using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FoodCheck.Windows;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Veda;

namespace FoodCheck
{
    public class Plugin : IDalamudPlugin
    {
        public string Name => "FoodCheck";

        [PluginService] public static IDalamudPluginInterface PluginInterface { get; set; }
        [PluginService] public static ICondition Conditions { get; set; }
        [PluginService] public static IDataManager Data { get; set; }
        [PluginService] public static ISigScanner SigScanner { get; set; }
        [PluginService] public static IChatGui Chat { get; set; }
        [PluginService] public static IClientState ClientState { get; set; }
        [PluginService] public static IPartyList PartyList { get; set; }
        [PluginService] public static IPluginLog PluginLog { get; set; }
        [PluginService] public static IGameInteropProvider Hook { get; set; }
        [PluginService] public static IGameInteropProvider GameInterop { get; set; }

        public static Configuration PluginConfig { get; set; }
        private PluginCommandManager<Plugin> commandManager;

        public static bool FirstRun = true;
        public static bool Debug = false;
        public static uint FedStatus = 48;

        private delegate nint CountdownTimerHookDelegate(ulong a1);

        [Signature("40 53 48 83 EC 40 80 79 38 00", DetourName = nameof(OnCountdownTimer))]
        private readonly Hook<CountdownTimerHookDelegate>? _countdownTimerHook = null;

        private static Hook<AgentReadyCheck.Delegates.InitiateReadyCheck> ReadyCheckHook;

        private readonly CountdownEvent _countdownEvent;

        public readonly WindowSystem WindowSystem = new("FoodCheck");
        private ConfigWindow ConfigWindow { get; init; }

        public unsafe Plugin(IDalamudPluginInterface pluginInterface, IChatGui chat, IPartyList partyList, ICommandManager commands, ISigScanner sigScanner)
        {
            PluginInterface = pluginInterface;
            PartyList = partyList;
            Chat = chat;
            SigScanner = sigScanner;

            Plugin.GameInterop.InitializeFromAttributes(this);
            _countdownTimerHook?.Enable();

            // Get or create a configuration object
            PluginConfig = (Configuration)PluginInterface.GetPluginConfig() ?? new Configuration();
            PluginConfig.Initialize(PluginInterface);

            ReadyCheckHook = Plugin.Hook.HookFromAddress<AgentReadyCheck.Delegates.InitiateReadyCheck>(AgentReadyCheck.MemberFunctionPointers.InitiateReadyCheck, ReadyCheckInitiatedDetour);
            ReadyCheckHook.Enable();

            ConfigWindow = new ConfigWindow(this);

            WindowSystem.AddWindow(ConfigWindow);

            PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
            PluginInterface.UiBuilder.OpenConfigUi += ConfigWindow.Toggle;

            // Load all of our commands
            this.commandManager = new PluginCommandManager<Plugin>(this, commands);
        }

        [Command("/foodcheck")]
        [HelpMessage("Opens the Food Check config menu")]
        public void OpenSettings(string command, string args)
        {
            ConfigWindow.Toggle();
        }

        [Command("/checkfood")]
        [HelpMessage("Manually check for food")]
        public void ManuallyCheckFood(string command, string args)
        {
            CheckWhoNeedsToEat();
        }

        private float _start;

        private nint OnCountdownTimer(ulong value)
        {
            try
            {
                float countDownPointerValue = Marshal.PtrToStructure<float>((IntPtr)value + 0x2c);
                if (Math.Floor(countDownPointerValue) - 2 <= _start)
                {
                    _start = countDownPointerValue;
                    return _countdownTimerHook.Original(value);
                }

                if (Conditions[ConditionFlag.BoundByDuty] || Conditions[ConditionFlag.BoundByDuty56] || Conditions[ConditionFlag.BoundByDuty95] || Debug)
                {
                    if (!PlayerIsInHighEndDuty() & PluginConfig.OnlyDoHighEndDuties)
                    {
                        _start = countDownPointerValue;
                        return _countdownTimerHook.Original(value);
                    }
                    if (PartyList.Count() == 0)
                    {
                        //Chat.Print("(There are no other party members)");
                        _start = countDownPointerValue;
                        return _countdownTimerHook.Original(value);
                    }
                    CheckWhoNeedsToEat();
                }

                _start = countDownPointerValue;
                return _countdownTimerHook.Original(value);
            }
            catch (Exception f)
            {
                Chat.PrintError("Something went wrong - " + f.ToString());
                return _countdownTimerHook.Original(value);
            }
        }

        private static unsafe void ReadyCheckInitiatedDetour(AgentReadyCheck* ptr)
        {
            ReadyCheckHook.Original(ptr);
            if (Conditions[ConditionFlag.BoundByDuty] || Conditions[ConditionFlag.BoundByDuty56] || Conditions[ConditionFlag.BoundByDuty95] || Debug)
            {
                if (!PlayerIsInHighEndDuty() & PluginConfig.OnlyDoHighEndDuties)
                {
                    return;
                }
                if (PartyList.Count() == 0)
                {
                    //Chat.Print("(There are no other party members)");
                    return;
                }
                CheckWhoNeedsToEat();
            }
        }

        public static unsafe void CheckWhoNeedsToEat()
        {
            string playersWhoNeedToEat = "";
            foreach (var partyMember in PartyList)
            {
                if (partyMember == null) { continue; }
                
                // Get the full status manager for this party member to check all status effects
                var statusManager = ((Character*)partyMember.GameObject.Address)->GetStatusManager();
                int statusIndex = statusManager->GetStatusIndex(FedStatus);
                
                // Player needs food if buff is missing or below the time threshold
                bool needsFood = statusIndex == -1
                    || (PluginConfig.CheckForFoodUnderXMinutes && statusManager->GetRemainingTime(statusIndex) / 60 < PluginConfig.MinutesToCheck);
                if (!needsFood) { continue; }
                
                //if (first)
                //{
                //this.chat.Print($"FOOD CHECK!");
                //    first = false;
                //}
                
                //this.chat.Print($"{partyMember.Name}");
                
                string name = PluginConfig.OnlyUseFirstNames
                    ? partyMember.Name.TextValue.Split(' ')[0]
                    : partyMember.Name.TextValue;
                playersWhoNeedToEat += name + ", ";
            }
            
            if (playersWhoNeedToEat.Length <= 3) return;
            string finalMessage = PluginConfig.CustomizableMessage.Replace("<names>", playersWhoNeedToEat.Remove(playersWhoNeedToEat.Length - 2, 2));
            Chat.Print(Functions.BuildSeString("FoodCheck", finalMessage));
        }

        //Taken from the Stanley Parable plugin, https://github.com/rekyuu/StanleyParableXiv/blob/main/StanleyParableXiv/Utility/XivUtility.cs
        //Based and hilarious plugin, you should check it out
        /// <summary>
        /// Checks if the territory is Unreal, Extreme, Savage, or Ultimate difficulty.
        /// </summary>
        /// <param name="territoryType">The territory to check against.</param>
        /// <returns>True if high-end, false otherwise.</returns>
        public static bool TerritoryIsHighEndDuty(uint territoryType)
        {
            string name = Plugin.Data.Excel
                .GetSheet<TerritoryType>()!
                .GetRow(territoryType)!
                .ContentFinderCondition.Value!
                .Name
                .ToString();

            bool isHighEndDuty = name.StartsWith("the Minstrel's Ballad")
                || name.EndsWith("(Unreal)")
                || name.EndsWith("(Extreme)")
                || name.EndsWith("(Savage)")
                || name.EndsWith("(Chaotic)")
                || name.EndsWith("(Ultimate)");

            PluginLog.Debug("{DutyName} is high end: {IsHighEnd}", name, isHighEndDuty);

            return isHighEndDuty;
        }

        /// <summary>
        /// Checks if player's current territory is Unreal, Extreme, Savage, or Ultimate difficulty.
        /// </summary>
        /// <returns>True if high-end, false otherwise.</returns>
        public static bool PlayerIsInHighEndDuty()
        {
            return TerritoryIsHighEndDuty(Plugin.ClientState.TerritoryType);
        }

        #region IDisposable Support

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            this.commandManager.Dispose();

            PluginInterface.SavePluginConfig(PluginConfig);

            PluginInterface.UiBuilder.OpenConfigUi -= ConfigWindow.Toggle;

            //if (_countdownTimerHook == null) return;
            _countdownTimerHook.Disable();
            _countdownTimerHook.Dispose();
            ReadyCheckHook.Disable();
            ReadyCheckHook.Dispose();
            GC.SuppressFinalize(this);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion IDisposable Support
    }
}