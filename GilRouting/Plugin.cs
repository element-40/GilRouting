using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Objects;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.Game;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Chat;
using FFXIVClientStructs.FFXIV.Client.UI;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.Automation;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using System;


namespace GIllionaire;


public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] public static IGameGui GameGui { get; private set; } = null!;
    [PluginService] public static ITargetManager TargetManager { get; private set; } = null!;

    private const int MaxTradeAmount = 1_000_000; // 1 million gil cap per trade

    // Command-handler chat output is deferred to the next framework update.
    private string? pendingBalanceChatMessage;

    private int remainingGil = 0;
    private bool isTradeInProgress = false;
    private int pendingGil;
    private DateTime nextActionAt;
    private DateTime tradeStartedAt;
    private TradeStage stage = TradeStage.Idle;
    private enum TradeStage { Idle, WaitingForWindow, WaitingForNumeric, WaitingForPartner, WaitingForCompletion, BetweenTrades }
    private const int TimeoutSeconds = 30;
    private bool yesClicked;
    private string? tradeTargetName;

    // Receiver automation: armed explicitly by the player, never automatically on load.
    private bool receiverArmed;
    private ReceiverStage receiverStage = ReceiverStage.WaitingForTrade;
    private enum ReceiverStage { WaitingForTrade, WaitingForSenderReady, WaitingForConfirmation, WaitingForClose, WaitingForWindowToDisappear }
    private DateTime receiverStageAt;
    private DateTime receiverNextActionAt;
    private bool receiverSawTrade;
    private bool receiverYesClicked;
    private int receiverExpectedGil;
    private int receiverRemainingGil;
    private int receiverPendingGil;
    private int receiverCompletedTrades;
    private bool receiverCompletionSeen;
    private DateTime receiverLastConfirmationErrorAt;
    private int receiverConfirmationFailures;
    private string? receiverExpectedSenderFirstName;
    private string? receiverValidatedSenderFirstName;


    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);
        ChatGui.ChatMessage += OnChatMessage;
        Framework.Update += OnFrameworkUpdate;
        CommandManager.AddHandler("/gilsend", new CommandInfo(OnCommand)
        {
            HelpMessage = "Starts automated gil trading: /gilsend <amount>"
        });

        CommandManager.AddHandler("/gilreceive", new CommandInfo(OnReceiveCommand)
        {
            HelpMessage = "Arms automatic incoming trade acceptance: /gilreceive <expected total gil>, /gilreceive off"
        });

        Log.Information($"===GilRouting Loaded===");
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (pendingBalanceChatMessage is { } balanceMessage)
        {
            pendingBalanceChatMessage = null;
            try
            {
                ChatGui.Print(balanceMessage);
                Log.Information("GilRouting: Posted insufficient-gil notice to normal chat.");
            }
            catch (Exception ex)
            {
                Log.Error($"GilRouting: Failed to post insufficient-gil notice to chat: {ex}");
            }
        }

        if (receiverArmed) UpdateReceiver();
        if (!isTradeInProgress || stage == TradeStage.Idle) return;
        if ((DateTime.UtcNow - tradeStartedAt).TotalSeconds > TimeoutSeconds && stage != TradeStage.BetweenTrades)
        {
            Abort("Timed out waiting for trade UI or completion. No automatic retry.");
            return;
        }
        if (DateTime.UtcNow < nextActionAt) return;
        try
        {
            switch (stage)
            {
                case TradeStage.WaitingForWindow:
                    var trade = GameGui.GetAddonByName<AtkUnitBase>("Trade");
                    if (trade == null || !trade->IsVisible) return;
                    TradeAmount(pendingGil);
                    stage = TradeStage.WaitingForNumeric;
                    nextActionAt = DateTime.UtcNow.AddMilliseconds(300);
                    break;
                case TradeStage.WaitingForNumeric:
                    var agent = GetTradeAgent();
                    if (agent == null) return;
                    SendAgentEvent(agent, -1, 1, true);
                    SendAgentEvent(agent, 4, 0, false);
                    var input = GameGui.GetAddonByName("InputNumeric");
                    if (!input.IsNull) new AddonMaster.InputNumeric(input).Cancel();
                    stage = TradeStage.WaitingForPartner;
                    nextActionAt = DateTime.UtcNow.AddMilliseconds(300);
                    break;
                case TradeStage.WaitingForPartner:
                    var window = GameGui.GetAddonByName<AtkUnitBase>("Trade");
                    if (window == null || !window->IsVisible) return;
                    var traderNode = window->GetNodeById(4);
                    var receiverNode = window->GetNodeById(5);
                    if (traderNode == null || receiverNode == null) return;
                    var trader = NodeUtils.GetAsAtkComponent<AtkComponentBase>(traderNode);
                    var receiver = NodeUtils.GetAsAtkComponent<AtkComponentBase>(receiverNode);
                    if (trader == null || receiver == null) return;
                    var receiverOk = receiver->GetTextNodeById(2);
                    if (receiverOk == null || receiverOk->Alpha_2 <= 200) return;
                    agent = GetTradeAgent();
                    if (agent == null) return;
                    SendAgentEvent(agent, 0, 0, false);
                    stage = TradeStage.WaitingForCompletion;
                    nextActionAt = DateTime.UtcNow.AddMilliseconds(250);
                    break;
                case TradeStage.WaitingForCompletion:
                    // Only accept confirmation while the active trade is being finalized.
                    if (yesClicked) return;
                    var yes = GameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
                    if (yes == null || !yes->AtkUnitBase.IsVisible) return;
                    new AddonMaster.SelectYesno(yes).Yes();
                    yesClicked = true;
                    break;
                case TradeStage.BetweenTrades:
                    isTradeInProgress = false;
                    stage = TradeStage.Idle;
                    StartNextTrade();
                    break;
            }
        }
        catch (Exception ex) { Abort($"Trade UI operation failed: {ex.Message}"); }
    }

    private void OnChatMessage(IHandleableChatMessage chat)
    {
        var message = chat.Message.TextValue;
        if (receiverArmed)
        {
            if (message.Contains("Trade canceled.", StringComparison.OrdinalIgnoreCase))
            {
                StopReceiver("Trade canceled. Receiver automation stopped.");
            }
            else if (message.Contains("Trade complete.", StringComparison.OrdinalIgnoreCase)
                     && receiverSawTrade
                     && receiverStage != ReceiverStage.WaitingForTrade
                     && receiverStage != ReceiverStage.WaitingForWindowToDisappear
                     && !receiverCompletionSeen)
            {
                // Chat may arrive before the final-confirmation stage transition.
                // Do not start another trade until the old window has disappeared.
                receiverCompletionSeen = true;
                // Count only completed trades, never a merely visible offer.
                if (receiverPendingGil <= 0 || receiverPendingGil > receiverRemainingGil)
                {
                    StopReceiver("Unexpected completion without a validated pending gil amount.");
                    return;
                }
                receiverRemainingGil -= receiverPendingGil;
                receiverCompletedTrades++;
                ChatGui.Print($"GilRouting: Received {receiverPendingGil:N0} gil in trade #{receiverCompletedTrades}; {receiverRemainingGil:N0} remaining.");
                receiverPendingGil = 0;
                receiverStage = ReceiverStage.WaitingForWindowToDisappear;
                receiverStageAt = DateTime.UtcNow;
                receiverNextActionAt = DateTime.UtcNow;
                ChatGui.Print($"GilRouting: Completed trade #{receiverCompletedTrades}; waiting for the trade window to close.");
            }
        }
        if (!isTradeInProgress) return;
        if (message.Contains("Trade complete.", StringComparison.OrdinalIgnoreCase) && stage == TradeStage.WaitingForCompletion)
        {
            remainingGil -= pendingGil;
            Log.Information($"Trade completed. {pendingGil} gil sent; {remainingGil} remaining.");
            pendingGil = 0;
            yesClicked = false;
            if (remainingGil > 0)
            {
                stage = TradeStage.BetweenTrades;
                nextActionAt = DateTime.UtcNow.AddMilliseconds(750);
            }
            else
            {
                isTradeInProgress = false;
                stage = TradeStage.Idle;
                ChatGui.Print("GilRouting: Gil trading completed.");
            }
        }
        else if (message.Contains("Trade canceled.", StringComparison.OrdinalIgnoreCase))
            Abort("Trade canceled. Remaining amount has not been retried.");
    }

    private void Abort(string reason)
    {
        Log.Warning(reason);
        ReportPlayerError($"GilRouting: {reason}");
        isTradeInProgress = false;
        stage = TradeStage.Idle;
        pendingGil = 0;
        remainingGil = 0;
        yesClicked = false;
    }

    private void StartNextTrade()
    {
        if (isTradeInProgress || remainingGil <= 0) return;
        var target = TargetManager.Target;
        if (target == null) { Abort("Target a player before trading."); return; }
        if (tradeTargetName != null && target.Name.TextValue != tradeTargetName)
        { Abort("Target changed during the trade sequence."); return; }
        tradeTargetName = target.Name.TextValue;
        pendingGil = CalculateTradeAmount(remainingGil, MaxTradeAmount);
        // Recheck before every installment; the balance can change during a sequence.
        if (!TryCheckSenderBalance(pendingGil, remainingGil)) { Abort("Sender balance insufficient for remaining transfer."); return; }
        tradeStartedAt = DateTime.UtcNow;
        nextActionAt = DateTime.UtcNow.AddMilliseconds(800);
        yesClicked = false;
        stage = TradeStage.WaitingForWindow;
        isTradeInProgress = true;
        Chat.ExecuteCommand("/trade");
    }

    // Calculate amount to send in current trade
    public int CalculateTradeAmount(int totalRemaining, int maxPerTrade)
    {
        // If we can send it all, do so
        if (totalRemaining <= maxPerTrade)
            return totalRemaining;

        // Calculate the optimal amount to send
        int amountToSend = totalRemaining % maxPerTrade;

        // If the remainder is 0, we need to send the max amount
        if (amountToSend == 0)
            return maxPerTrade;

        return amountToSend;
    }

    internal static unsafe class NodeUtils
    {
        public static AtkComponentNode* GetAsAtkComponentNode(AtkResNode* me) =>
            (ushort)me->Type < 1000
                ? throw new ArgumentException("Node is not a component node", nameof(me))
                : (AtkComponentNode*)me;

        public static T* GetComponent<T>(AtkComponentNode* node) where T : unmanaged =>
            GetNodeComponentType(node) != GetComponentType(typeof(T))
                ? throw new ArgumentException($"{GetNodeComponentType(node)} node is not a {typeof(T).Name}", nameof(node))
                : (T*)node->Component;

        public static T* GetAsAtkComponent<T>(AtkResNode* me) where T : unmanaged =>
            GetComponent<T>(GetAsAtkComponentNode(me));

        private static ComponentType GetComponentType(Type t)
        {
            if (t == typeof(AtkComponentBase)) return ComponentType.Base;
            if (t == typeof(AtkComponentButton)) return ComponentType.Button;
            if (t == typeof(AtkComponentWindow)) return ComponentType.Window;
            if (t == typeof(AtkComponentCheckBox)) return ComponentType.CheckBox;
            if (t == typeof(AtkComponentRadioButton)) return ComponentType.RadioButton;
            if (t == typeof(AtkComponentGaugeBar)) return ComponentType.GaugeBar;
            if (t == typeof(AtkComponentSlider)) return ComponentType.Slider;
            if (t == typeof(AtkComponentTextInput)) return ComponentType.TextInput;
            if (t == typeof(AtkComponentNumericInput)) return ComponentType.NumericInput;
            if (t == typeof(AtkComponentList)) return ComponentType.List;
            if (t == typeof(AtkComponentDropDownList)) return ComponentType.DropDownList;
            // if (t == typeof(AtkComponentTab)) return ComponentType.Tab;
            if (t == typeof(AtkComponentTreeList)) return ComponentType.TreeList;
            if (t == typeof(AtkComponentScrollBar)) return ComponentType.ScrollBar;
            if (t == typeof(AtkComponentListItemRenderer)) return ComponentType.ListItemRenderer;
            if (t == typeof(AtkComponentIcon)) return ComponentType.Icon;
            if (t == typeof(AtkComponentIconText)) return ComponentType.IconText;
            if (t == typeof(AtkComponentDragDrop)) return ComponentType.DragDrop;
            if (t == typeof(AtkComponentGuildLeveCard)) return ComponentType.GuildLeveCard;
            if (t == typeof(AtkComponentTextNineGrid)) return ComponentType.TextNineGrid;
            if (t == typeof(AtkComponentJournalCanvas)) return ComponentType.JournalCanvas;
            // if (t == typeof(AtkComponentMultipurpose)) return ComponentType.Multipurpose;
            // if (t == typeof(AtkComponentMap)) return ComponentType.Map;
            // if (t == typeof(AtkComponentPreview)) return ComponentType.Preview;
            if (t == typeof(AtkComponentHoldButton)) return ComponentType.HoldButton;
            if (t == typeof(AtkComponentPortrait)) return ComponentType.Portrait;
            throw new ArgumentOutOfRangeException(nameof(t), t, "Unknown component type");
        }

        private static ComponentType? GetNodeComponentType(AtkComponentNode* node)
        {
            var info = ((AtkUldComponentInfo*)node->Component->UldManager.Objects);
            return info == null ? null : info->ComponentType;
        }

        public static AtkResNode* GetNodeById(AtkComponentBase* node, uint id) =>
            node->UldManager.SearchNodeById(id);

        public static AtkResNode* GetNodeByIdStrict(AtkComponentBase* node, uint id)
        {
            for (var i = 0; i < node->UldManager.NodeListCount; i++)
            {
                var n = node->UldManager.NodeList[i];
                if (n->NodeId == id)
                    return n;
            }
            return null;
        }

        public static void SetVisibility(AtkResNode* node, bool visible)
        {
            if (visible)
                node->NodeFlags |= NodeFlags.Visible;
            else
                node->NodeFlags &= ~NodeFlags.Visible;
        }
    }

    private static unsafe void SendAgentEvent(AgentInterface* agent, int value, ulong eventKind, bool returnBool)
    {
        var result = new AtkValue { Bool = returnBool };
        var values = new AtkValue { Type = AtkValueType.Int, Int = value };
        agent->ReceiveEvent(&result, &values, eventKind == 1 ? 1u : 2u, eventKind);
    }

    private static unsafe AgentInterface* GetTradeAgent()
    {
        var wrapper = GameGui.FindAgentInterface("Trade");
        return wrapper.IsNull ? null : (AgentInterface*)wrapper.Address;
    }

    public unsafe void TradeAmount(int amount)
    {
        var agent = GetTradeAgent();
        if (agent == null) throw new InvalidOperationException("Trade agent not available.");
        SendAgentEvent(agent, 2, 0, false);
        SendAgentEvent(agent, amount, 1, true);
    }

    public void Dispose()
    {
        ECommonsMain.Dispose();
        ChatGui.ChatMessage -= OnChatMessage;
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler("/gilsend");
        CommandManager.RemoveHandler("/gilreceive");
    }

    private void OnReceiveCommand(string command, string args)
    {
        var arg = args.Trim();
        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase) || arg.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            StopReceiver("Receiver automation disabled.");
            return;
        }
        if (!int.TryParse(arg, out var amount) || amount <= 0)
        {
            ReportPlayerError("GilRouting: Usage: /gilreceive <expected total gil> or /gilreceive off");
            return;
        }
        if (isTradeInProgress)
        {
            ReportPlayerError("GilRouting: Stop sending before enabling receive mode.");
            return;
        }
        // The target name may be abbreviated in the UI (e.g. Blue B.).
        // Compare first names only, using the full current target where available.
        var target = TargetManager.Target;
        var expectedFirstName = FirstNameOnly(target?.Name.TextValue);
        if (expectedFirstName == null)
        {
            ReportPlayerError("GilRouting: Target the sending player before using /gilreceive.");
            return;
        }
        receiverExpectedSenderFirstName = expectedFirstName;
        receiverValidatedSenderFirstName = null;
        receiverExpectedGil = amount;
        receiverRemainingGil = amount;
        receiverPendingGil = 0;
        receiverCompletedTrades = 0;
        receiverArmed = true;
        receiverSawTrade = false;
        receiverYesClicked = false;
        receiverCompletionSeen = false;
        receiverConfirmationFailures = 0;
        receiverStage = ReceiverStage.WaitingForTrade;
        receiverStageAt = DateTime.UtcNow;
        receiverNextActionAt = DateTime.UtcNow;
        ChatGui.Print($"GilRouting: Receiver armed for {amount:N0} gil. Gil trade amount must match each expected installment. Sender first name must match targeted player ({receiverExpectedSenderFirstName}). Use /gilreceive off to stop.");
    }

    private void StopReceiver(string reason)
    {
        receiverArmed = false;
        receiverSawTrade = false;
        receiverYesClicked = false;
        receiverCompletionSeen = false;
        receiverPendingGil = 0;
        receiverRemainingGil = 0;
        receiverExpectedSenderFirstName = null;
        receiverValidatedSenderFirstName = null;
        receiverStage = ReceiverStage.WaitingForTrade;
        ChatGui.Print($"GilRouting: {reason}");
    }

    // The Trade addon node 17 contains the sender name. Only compare the first
    // token because last names can be abbreviated in the client's UI settings.
    private static string? FirstNameOnly(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var parts = name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? null : parts[0];
    }

    // Closing the Trade addon with its callback enabled is intended to follow
    // the game's own close/cancel path, rather than just hiding the window.
    // This native UI behavior must be verified in-game on API 15.
    private unsafe void CancelMismatchedTrade(AtkUnitBase* trade, string reason)
    {
        // Disarm first so no subsequent framework tick can accept this trade.
        StopReceiver(reason + " Attempting to cancel the active trade.");
        try
        {
            if (trade != null && trade->IsVisible)
            {
                trade->Close(true);
                Log.Warning($"GilRouting: Requested trade cancellation after name mismatch: {reason}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"GilRouting: Failed to request trade cancellation: {ex}");
            ReportPlayerError("GilRouting: Automatic cancellation failed; cancel the trade manually.");
        }
    }

    private unsafe bool ValidateReceiverSender(AtkUnitBase* trade)
    {
        var targetFirstName = FirstNameOnly(TargetManager.Target?.Name.TextValue);
        if (targetFirstName == null || receiverExpectedSenderFirstName == null ||
            !string.Equals(targetFirstName, receiverExpectedSenderFirstName, StringComparison.OrdinalIgnoreCase))
        {
            CancelMismatchedTrade(trade, "Target is missing or changed. Receiver automation stopped; re-target the sender and re-arm.");
            return false;
        }

        var senderNode = trade->GetNodeById(17);
        if (senderNode == null || senderNode->Type != NodeType.Text)
            return false; // UI not ready; do not approve anything.

        var actualFirstName = FirstNameOnly(((AtkTextNode*)senderNode)->NodeText.ToString());
        if (actualFirstName == null) return false;
        if (!string.Equals(actualFirstName, receiverExpectedSenderFirstName, StringComparison.OrdinalIgnoreCase))
        {
            CancelMismatchedTrade(trade, $"Sender mismatch: targeted '{receiverExpectedSenderFirstName}', trade window shows '{actualFirstName}'.");
            return false;
        }
        receiverValidatedSenderFirstName = actualFirstName;
        return true;
    }

    private static bool TryParseOfferedGil(string text, out int amount)
    {
        // The gil label is a separate node (32). Node 31 contains only digits
        // and potentially thousands separators. Reject unexpected text.
        amount = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var normalized = text.Trim().Replace(",", "").Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "");
        return normalized.Length > 0
            && System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^[0-9]+$")
            && int.TryParse(normalized, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out amount);
    }

    private unsafe void UpdateReceiver()
    {
        if (DateTime.UtcNow < receiverNextActionAt) return;
        try
        {
            var trade = GameGui.GetAddonByName<AtkUnitBase>("Trade");
            var visible = trade != null && trade->IsVisible;
            if (receiverStage == ReceiverStage.WaitingForWindowToDisappear)
            {
                if (!visible)
                {
                    if (receiverRemainingGil == 0)
                    {
                        StopReceiver($"Expected total of {receiverExpectedGil:N0} gil completed across {receiverCompletedTrades} trade(s). Receiver automation disabled.");
                        return;
                    }
                    receiverStage = ReceiverStage.WaitingForTrade;
                    receiverSawTrade = false;
                    receiverYesClicked = false;
                    receiverCompletionSeen = false;
                    receiverConfirmationFailures = 0;
                    receiverPendingGil = 0;
                    receiverValidatedSenderFirstName = null;
                    receiverStageAt = DateTime.UtcNow;
                    receiverNextActionAt = DateTime.UtcNow.AddMilliseconds(400);
                    ChatGui.Print("GilRouting: Receiver ready for next trade.");
                }
                else if ((DateTime.UtcNow - receiverStageAt).TotalSeconds > TimeoutSeconds)
                    StopReceiver("Completed trade window did not close; receiver stopped.");
                return;
            }
            if (!visible)
            {
                if (receiverStage == ReceiverStage.WaitingForTrade) return;
                // Never click another trade or unrelated confirmation after the active trade closes.
                if (receiverStage == ReceiverStage.WaitingForClose)
                {
                    if ((DateTime.UtcNow - receiverStageAt).TotalSeconds > TimeoutSeconds)
                        StopReceiver("No trade completion message received; receiver stopped.");
                    return;
                }
                if (receiverSawTrade && (DateTime.UtcNow - receiverStageAt).TotalSeconds > TimeoutSeconds)
                    StopReceiver("Trade window disappeared before confirmed completion.");
                return;
            }
            // Recheck the Trade addon name and current target on every active
            // update, including immediately before the final confirmation.
            if (receiverStage != ReceiverStage.WaitingForClose && !ValidateReceiverSender(trade)) return;
            receiverSawTrade = true;
            if (receiverStage == ReceiverStage.WaitingForTrade)
            {
                receiverPendingGil = CalculateTradeAmount(receiverRemainingGil, MaxTradeAmount);
                receiverStage = ReceiverStage.WaitingForSenderReady;
                receiverStageAt = DateTime.UtcNow;
                receiverNextActionAt = DateTime.UtcNow.AddMilliseconds(350);
                return;
            }
            if ((DateTime.UtcNow - receiverStageAt).TotalSeconds > TimeoutSeconds)
            {
                StopReceiver("Receiver timed out waiting for the sender or confirmation.");
                return;
            }
            if (receiverStage == ReceiverStage.WaitingForSenderReady)
            {
                // Match the sender's installment order (remainder first, then 1M chunks).
                // Do not press Trade until the incoming gil display exactly matches.
                var offeredNode = trade->GetNodeById(31);
                if (offeredNode == null || offeredNode->Type != NodeType.Text) return;
                var offeredText = ((AtkTextNode*)offeredNode)->NodeText.ToString();
                if (!TryParseOfferedGil(offeredText, out var offeredGil)) return;
                if (offeredGil != receiverPendingGil) return;
                if (receiverValidatedSenderFirstName == null) return;
                var agent = GetTradeAgent();
                if (agent == null) return;
                SendAgentEvent(agent, 0, 0, false);
                receiverStage = ReceiverStage.WaitingForConfirmation;
                receiverStageAt = DateTime.UtcNow;
                receiverNextActionAt = DateTime.UtcNow.AddMilliseconds(300);
            }
            else if (receiverStage == ReceiverStage.WaitingForConfirmation && !receiverYesClicked)
            {
                var yes = GameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
                if (yes == null || !yes->AtkUnitBase.IsVisible) return;
                // ECommons can throw if the Yes button's underlying UI component
                // is not initialized yet. Retry briefly rather than stopping
                // the whole multi-trade transfer on a transient null reference.
                if (receiverValidatedSenderFirstName == null) return;
                try
                {
                    new AddonMaster.SelectYesno(yes).Yes();
                    receiverYesClicked = true;
                    receiverConfirmationFailures = 0;
                    receiverStage = ReceiverStage.WaitingForClose;
                    receiverStageAt = DateTime.UtcNow;
                }
                catch (NullReferenceException ex)
                {
                    receiverConfirmationFailures++;
                    receiverNextActionAt = DateTime.UtcNow.AddMilliseconds(500);
                    Log.Error($"Receiver confirmation attempt {receiverConfirmationFailures} failed: {ex}");
                    if (receiverConfirmationFailures >= 6)
                        StopReceiver("Unable to operate the Yes confirmation after 6 attempts. See Dalamud log for the exception stack trace.");
                    else if ((DateTime.UtcNow - receiverLastConfirmationErrorAt).TotalSeconds >= 2)
                    {
                        receiverLastConfirmationErrorAt = DateTime.UtcNow;
                        ReportPlayerError($"GilRouting: Receiver confirmation UI not ready (attempt {receiverConfirmationFailures}/6); retrying.");
                    }
                }
            }
            else if (receiverStage == ReceiverStage.WaitingForClose &&
                     (DateTime.UtcNow - receiverStageAt).TotalSeconds > TimeoutSeconds)
            {
                StopReceiver("No trade completion message received; receiver stopped.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Receiver UI operation failed at stage {receiverStage}: {ex}");
            StopReceiver($"Receiver UI operation failed at stage {receiverStage}: {ex.Message}. See Dalamud log for details.");
        }
    }

    // Use ordinary chat output for player-visible errors, and the plugin log for diagnostics.
    // PrintError alone can be hidden by the user's chat-log filters.
    private static void ReportPlayerError(string message)
    {
        Log.Warning(message);
        ChatGui.Print(message);
    }

    private unsafe bool TryCheckSenderBalance(int installment, int totalRemaining)
    {
        var inventory = InventoryManager.Instance();
        if (inventory == null)
        {
            Log.Error("GilRouting: Cannot read sender gil balance; aborting transfer.");
            ReportPlayerError("GilRouting: Cannot read your gil balance. Transfer cancelled.");
            return false;
        }
        var available = (long)inventory->GetGil();
        if (available < totalRemaining || available < installment)
        {
            var message = $"GilRouting: Insufficient gil: {available:N0} available, {totalRemaining:N0} remaining to send (next trade: {installment:N0}). Transfer cancelled.";
            Log.Warning(message);
            pendingBalanceChatMessage = message;
            return false;
        }
        return true;
    }

    private unsafe void OnCommand(string command, string args)
    {
        if (!int.TryParse(args, out var gilAmount) || gilAmount <= 0)
        {
            ReportPlayerError("GilRouting: Invalid amount. Usage: /gilsend <positive amount>.");
            Log.Warning($"Invalid sender amount: {args}");
            return;
        }
        if (!TryCheckSenderBalance(CalculateTradeAmount(gilAmount, MaxTradeAmount), gilAmount)) return;
        if (isTradeInProgress || receiverArmed) { ReportPlayerError("GilRouting: Stop existing send/receive automation first."); return; }
        tradeTargetName = null;
        remainingGil = gilAmount;
        isTradeInProgress = false;
        Log.Information($"Starting trade sequence for {gilAmount} gil");
        StartNextTrade();
    }
}