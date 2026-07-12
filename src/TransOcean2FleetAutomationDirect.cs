using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TransOcean2FleetAutomation.Direct
{
    public static class Loader
    {
        private static bool initialized;

        public static void Bootstrap()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            Debug.Log("[TO2FA.Direct] Bootstrap reached from patched Assembly-CSharp.dll");

            GameObject host = new GameObject("TransOcean2FleetAutomation.DirectHost");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<FleetAutomationCaptainBehaviour>();
        }
    }

    public sealed class FleetAutomationCaptainBehaviour : MonoBehaviour
    {
        private const string LogPrefix = "[TO2FA.Direct]";
        private const string CaptainPrefsPrefix = "TO2FA.Captain.";
        private const int RouteLookaheadDepth = 3;
        private const int RouteLookaheadBeamWidth = 5;
        private const int CargoPackingSearchLimit = 14;
        private const double RouteFutureLegDiscount = 0.6;
        private const double RouteSameFreightDestinationPenalty = 250000.0;
        private const double RouteOtherFleetDestinationPenalty = 75000.0;
        private const float DispatchSettleSeconds = 20f;
        private const float DispatchSettleMinRealtimeSeconds = 2f;
        private const float PanelMaxWidth = 1180f;
        private const float PanelMaxHeight = 820f;
        private const float ControlHeight = 30f;
        private const float ShipRowHeight = 28f;
        private const long UpgradeTreasuryCushionPerShip = 10000000L;
        private const long UpgradeTreasuryCapTotal = 50000000L;
        private const int UpgradeFleetLeadAllowance = 1;
        private const float RepairTravelSafetyMarginPercent = 10f;
        private const float AutomationTickBaseRealtimeSeconds = 30f;
        private const float AutomationTickMinRealtimeSeconds = 2f;
        private const float BlockingPopupTimeoutRealtimeSeconds = 25f;
        private const float FleetNotificationScanRealtimeSeconds = 4f;
        private const float FleetNotificationGraceRealtimeSeconds = 90f;
        private const int FleetNotificationMaxVisiblePerType = 3;
        private const float MinSailConditionFloor = 1f;
        private const float MinSailConditionCeiling = 99f;
        private const float IdleRepositionDefaultDays = 7f;
        private const float IdleRepositionMinDays = 1f;
        private const float IdleRepositionMaxDays = 30f;
        private const int IdleRepositionMaxHarborsToProbe = 10;

        private static readonly string[] KnownContrabandFreights = new string[]
        {
            "ContaminatedWater",
            "CounterfeitGoods",
            "QuestionableGoods"
        };

        private static readonly int[] UpgradeCandidateIds = new int[]
        {
            7, 8, 9, 10, 11, 12, 13, 14, 15,
            4, 1, 5, 2, 3, 6
        };

        private readonly Dictionary<int, bool> captainEnabledByShipId = new Dictionary<int, bool>();
        private readonly Dictionary<int, float> lastDispatchRealtimeByShipId = new Dictionary<int, float>();
        private readonly Dictionary<int, string> lastSeenShipNameById = new Dictionary<int, string>();
        private readonly Dictionary<int, bool> missingShipWarnedByShipId = new Dictionary<int, bool>();
        private readonly Dictionary<int, DateTime> idleSinceGameDateByShipId = new Dictionary<int, DateTime>();
        private readonly Dictionary<int, string> idleHarborByShipId = new Dictionary<int, string>();
        private readonly Dictionary<int, int> plannedFreightUpgradeByShipId = new Dictionary<int, int>();
        private readonly Dictionary<string, bool> contrabandFreightByName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> blockingPopupFirstSeenRealtimeByName = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, float> newstickerFirstSeenRealtimeById = new Dictionary<int, float>();
        private readonly HashSet<int> newstickerArchivedIds = new HashSet<int>();
        private readonly List<ShipSnapshot> ships = new List<ShipSnapshot>();
        private readonly List<string> decisionLog = new List<string>();

        private object dsqLite;
        private object staticSqLite;
        private object shipFactory;
        private object commissionFactory;
        private MethodInfo getPlayerIdMethod;
        private MethodInfo getPlayerCreditsMethod;
        private MethodInfo getAllPlayerShipsMethod;
        private MethodInfo getPlayerShipMethod;
        private MethodInfo getAllJobsFromPlayerShipMethod;
        private MethodInfo getJobsFromStartHarborMethod;
        private MethodInfo getHarborMethod;
        private MethodInfo getFreightAttributesMethod;
        private MethodInfo getRepairDockHarborsMethod;
        private MethodInfo getUpgradeDockHarborsMethod;
        private MethodInfo getUpgradeExportFromHarborMethod;
        private MethodInfo getBusinessSharesFromRegionMethod;
        private MethodInfo getHarborDistanceMethod;
        private MethodInfo getHarborsFromMethod;
        private MethodInfo getRegionOfHarborMethod;
        private MethodInfo getShipClassesMethod;
        private MethodInfo updateJobPlayerShipMethod;
        private MethodInfo setShipDontLoadJobsMethod;
        private MethodInfo setShipGlobalDestinationMethod;
        private MethodInfo setShipSmugglersWareMethod;
        private MethodInfo updatePlayerShipDestinationHarborMethod;
        private MethodInfo repairPlayerShipMethod;
        private MethodInfo upgradePlayerShipMethod;
        private MethodInfo getUpgradeDockOwnerMethod;
        private MethodInfo sendShipToDestinationMethod;
        private MethodInfo getRemainingConditionOnArrivalMethod;
        private MethodInfo unloadCommissionsFromShipMethod;
        private MethodInfo getShipSinkChanceMethod;
        private MethodInfo getDaysForRepairMethod;
        private MethodInfo getCurrentDateTimeMethod;
        private MethodInfo getCurrentInGameSpeedMethod;
        private MethodInfo updatePlayerShipAiStateMethod;
        private MethodInfo sendEventMethod;
        private MethodInfo strikePopupButtonWaitMethod;
        private MethodInfo closeWindowMethod;
        private MethodInfo getWindowByIdentifierMethod;
        private MethodInfo newstickerMoveToArchiveMethod;
        private Type cargoEventType;
        private Type strikePopupType;
        private Type customsPopupType;
        private Type newstickerItemType;
        private bool runtimeUiHooksResolved;

        private bool showPanel;
        private bool liveActions = true;
        private bool autoRepairs = true;
        private bool autoUpgrades = true;
        private bool evaluateEnabledShipsEveryTick = true;
        private bool allowContrabandCargo;
        private bool autoRepositionIdleShips = true;
        private bool autoPilotAllShips;
        private bool gameSessionActive;
        private float minimumSailCondition = 85f;
        private float repairTargetCondition = 100f;
        private float idleRepositionDays = IdleRepositionDefaultDays;
        private Texture2D panelBackgroundTexture;
        private float nextControllerLookup;
        private float nextRefresh;
        private float nextAutomationTick;
        private float repairSinkDangerCondition;
        private float repairTravelSafetyFloor = RepairTravelSafetyMarginPercent;
        private int repairTravelSafetyPlayerId = -1;
        private float nextFleetNotificationScan;
        private Vector2 scroll;
        private Vector2 logScroll;
        private int playerId;
        private long playerCredits;
        private string statusText = "Waiting for game controllers...";

        private void Awake()
        {
            Debug.Log(LogPrefix + " Fleet automation captain attached. F8 evaluates enabled ships, F9 toggles panel.");
            showPanel = PlayerPrefs.GetInt("TO2FA.ShowPanel", 1) == 1;
            liveActions = PlayerPrefs.GetInt("TO2FA.LiveActions", 1) == 1;
            autoRepairs = PlayerPrefs.GetInt("TO2FA.AutoRepairs", 1) == 1;
            autoUpgrades = PlayerPrefs.GetInt("TO2FA.AutoUpgrades", 1) == 1;
            evaluateEnabledShipsEveryTick = PlayerPrefs.GetInt("TO2FA.TickEnabled", 1) == 1;
            allowContrabandCargo = PlayerPrefs.GetInt("TO2FA.AllowContrabandCargo", 0) == 1;
            autoRepositionIdleShips = PlayerPrefs.GetInt("TO2FA.AutoRepositionIdleShips", 1) == 1;
            autoPilotAllShips = PlayerPrefs.GetInt("TO2FA.AutoPilotAllShips", 0) == 1;
            minimumSailCondition = PlayerPrefs.GetFloat("TO2FA.MinimumSailCondition", 85f);
            minimumSailCondition = Mathf.Round(Mathf.Clamp(minimumSailCondition, MinSailConditionFloor, MinSailConditionCeiling));
            repairTargetCondition = PlayerPrefs.GetFloat("TO2FA.RepairTargetCondition", 100f);
            repairTargetCondition = Mathf.Clamp(repairTargetCondition, minimumSailCondition, 100f);
            idleRepositionDays = PlayerPrefs.GetFloat("TO2FA.IdleRepositionDays", IdleRepositionDefaultDays);
            idleRepositionDays = Mathf.Round(Mathf.Clamp(idleRepositionDays, IdleRepositionMinDays, IdleRepositionMaxDays));
            SeedKnownContrabandFreights();
            AddDecisionLog("Captain UI attached. Live actions are " + (liveActions ? "ON." : "OFF.") + " Cargo policy: " + GetCargoPolicyLabel() + ".");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                showPanel = !showPanel;
                PlayerPrefs.SetInt("TO2FA.ShowPanel", showPanel ? 1 : 0);
                PlayerPrefs.Save();
                Debug.Log(LogPrefix + " Captain panel " + (showPanel ? "shown" : "hidden") + ".");
            }

            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (IsGameSessionActive())
                {
                    EvaluateEnabledShips("manual F8");
                }
            }

            if (Input.GetKeyDown(KeyCode.F10))
            {
                RefreshFleet(true);
            }

            if (Time.realtimeSinceStartup >= nextControllerLookup && !ControllersReady)
            {
                nextControllerLookup = Time.realtimeSinceStartup + 2f;
                RefreshControllers();
            }

            if (showPanel && Time.realtimeSinceStartup >= nextRefresh)
            {
                RefreshFleet(false);
            }

            if (evaluateEnabledShipsEveryTick)
            {
                float tickDelay = GetAutomationTickDelaySeconds();
                float now = Time.realtimeSinceStartup;
                if (nextAutomationTick <= 0f || nextAutomationTick - now > tickDelay)
                {
                    nextAutomationTick = now + tickDelay;
                }

                if (now >= nextAutomationTick)
                {
                    nextAutomationTick = now + tickDelay;
                    if (IsGameSessionActive() && HasAnyCaptainEnabled())
                    {
                        EvaluateEnabledShips("scheduled automation tick");
                    }
                }
            }

            if (IsGameSessionActive())
            {
                HandleRuntimeUiWatchdogs();
            }
            else
            {
                ResetRuntimeUiWatchdogs();
            }
        }

        private void OnGUI()
        {
            if (!showPanel || !IsGameSessionActive())
            {
                return;
            }

            float width = Mathf.Min(PanelMaxWidth, Mathf.Max(320f, Screen.width - 32f));
            float height = Mathf.Min(PanelMaxHeight, Mathf.Max(360f, Screen.height - 48f));
            float left = Mathf.Max(8f, (Screen.width - width) * 0.5f);
            float top = Mathf.Max(8f, (Screen.height - height) * 0.5f);
            Rect panelRect = new Rect(left, top, width, height);

            // Paint a near-opaque backing so the game view does not bleed through the panel.
            GUI.DrawTexture(panelRect, GetPanelBackgroundTexture(), ScaleMode.StretchToFill, false);
            GUILayout.BeginArea(panelRect, "TO2 Fleet Captain", GUI.skin.window);

            GUILayout.Label(statusText);
            GUILayout.Label(string.Format("Player {0} treasury: {1:n0}", playerId, playerCredits));
            GUILayout.Label(string.Format(
                "Repair travel floor: {0:0}% arrival condition (sink risk starts near {1:0}%)",
                repairTravelSafetyFloor,
                repairSinkDangerCondition));
            GUILayout.Label(string.Format(
                "Automation tick: {0:0.#} real seconds at {1:0.#}x game speed",
                GetAutomationTickDelaySeconds(),
                GetCurrentInGameSpeed()));
            GUILayout.Label(string.Format(
                "Upgrade cushion: {0:n0} / {1:n0} (lesser of {2:n0} per ship or {3:n0} total)",
                playerCredits,
                GetUpgradeTreasuryCushion(),
                UpgradeTreasuryCushionPerShip,
                UpgradeTreasuryCapTotal));

            GUILayout.BeginHorizontal();
            bool newLiveActions = GUILayout.Toggle(liveActions, "Live actions", GUILayout.Width(150f), GUILayout.Height(ControlHeight));
            if (newLiveActions != liveActions)
            {
                liveActions = newLiveActions;
                PlayerPrefs.SetInt("TO2FA.LiveActions", liveActions ? 1 : 0);
                PlayerPrefs.Save();
                AddDecisionLog("Live actions " + (liveActions ? "enabled." : "disabled; restoring manual control for enabled ships."));
                SyncNativeAiStateForEnabledShips(liveActions);
            }

            bool newAutoRepairs = GUILayout.Toggle(autoRepairs, "Auto repairs", GUILayout.Width(145f), GUILayout.Height(ControlHeight));
            if (newAutoRepairs != autoRepairs)
            {
                autoRepairs = newAutoRepairs;
                PlayerPrefs.SetInt("TO2FA.AutoRepairs", autoRepairs ? 1 : 0);
                PlayerPrefs.Save();
                AddDecisionLog("Auto repairs " + (autoRepairs ? "enabled." : "disabled."));
            }

            bool newAutoUpgrades = GUILayout.Toggle(autoUpgrades, "Auto upgrades", GUILayout.Width(155f), GUILayout.Height(ControlHeight));
            if (newAutoUpgrades != autoUpgrades)
            {
                autoUpgrades = newAutoUpgrades;
                PlayerPrefs.SetInt("TO2FA.AutoUpgrades", autoUpgrades ? 1 : 0);
                PlayerPrefs.Save();
                AddDecisionLog("Auto upgrades " + (autoUpgrades ? "enabled." : "disabled."));
            }

            bool newAllowContrabandCargo = GUILayout.Toggle(allowContrabandCargo, "Allow contraband", GUILayout.Width(170f), GUILayout.Height(ControlHeight));
            if (newAllowContrabandCargo != allowContrabandCargo)
            {
                allowContrabandCargo = newAllowContrabandCargo;
                PlayerPrefs.SetInt("TO2FA.AllowContrabandCargo", allowContrabandCargo ? 1 : 0);
                PlayerPrefs.Save();
                AddDecisionLog("Cargo policy changed: " + GetCargoPolicyLabel() + ".");
                if (liveActions)
                {
                    SyncNativeAiStateForEnabledShips(true);
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool newTick = GUILayout.Toggle(evaluateEnabledShipsEveryTick, "Evaluate every 30s", GUILayout.Width(190f), GUILayout.Height(ControlHeight));
            if (newTick != evaluateEnabledShipsEveryTick)
            {
                evaluateEnabledShipsEveryTick = newTick;
                PlayerPrefs.SetInt("TO2FA.TickEnabled", evaluateEnabledShipsEveryTick ? 1 : 0);
                PlayerPrefs.Save();
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Refresh", GUILayout.Width(115f), GUILayout.Height(ControlHeight)))
            {
                RefreshFleet(true);
            }

            if (GUILayout.Button("Run", GUILayout.Width(105f), GUILayout.Height(ControlHeight)))
            {
                EvaluateEnabledShips("panel button");
            }

            bool newAutoPilotAll = GUILayout.Toggle(
                autoPilotAllShips,
                autoPilotAllShips ? "Auto-pilot all + new: ON" : "Auto-pilot all + new: OFF",
                GUI.skin.button,
                GUILayout.Width(230f),
                GUILayout.Height(ControlHeight));
            if (newAutoPilotAll != autoPilotAllShips)
            {
                autoPilotAllShips = newAutoPilotAll;
                PlayerPrefs.SetInt("TO2FA.AutoPilotAllShips", autoPilotAllShips ? 1 : 0);
                PlayerPrefs.Save();
                AddDecisionLog("Auto-pilot all ships (including new arrivals) " + (autoPilotAllShips ? "enabled." : "disabled."));
                SetAllVisibleShips(autoPilotAllShips);
                RefreshFleet(true);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format("Repair threshold (min condition to sail): {0:0}%", minimumSailCondition), GUILayout.Width(335f));
            float newMinimumSailCondition = GUILayout.HorizontalSlider(minimumSailCondition, MinSailConditionFloor, MinSailConditionCeiling, GUILayout.MinWidth(260f), GUILayout.ExpandWidth(true));
            newMinimumSailCondition = Mathf.Round(newMinimumSailCondition);
            if (Mathf.Abs(newMinimumSailCondition - minimumSailCondition) > 0.1f)
            {
                minimumSailCondition = newMinimumSailCondition;
                PlayerPrefs.SetFloat("TO2FA.MinimumSailCondition", minimumSailCondition);
                PlayerPrefs.Save();
                AddDecisionLog(string.Format("Minimum condition to sail set to {0:0}%.", minimumSailCondition));
            }
            if (GUILayout.Button("85%", GUILayout.Width(65f), GUILayout.Height(ControlHeight)))
            {
                SetMinimumSailCondition(85f);
            }
            if (GUILayout.Button("90%", GUILayout.Width(65f), GUILayout.Height(ControlHeight)))
            {
                SetMinimumSailCondition(90f);
            }
            if (GUILayout.Button("95%", GUILayout.Width(65f), GUILayout.Height(ControlHeight)))
            {
                SetMinimumSailCondition(95f);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format("Repair target condition: {0:0}%", repairTargetCondition), GUILayout.Width(265f));
            float newRepairTargetCondition = GUILayout.HorizontalSlider(repairTargetCondition, minimumSailCondition, 100f, GUILayout.MinWidth(260f), GUILayout.ExpandWidth(true));
            newRepairTargetCondition = Mathf.Round(newRepairTargetCondition);
            if (Mathf.Abs(newRepairTargetCondition - repairTargetCondition) > 0.1f)
            {
                SetRepairTargetCondition(newRepairTargetCondition);
            }
            if (GUILayout.Button("90%", GUILayout.Width(65f), GUILayout.Height(ControlHeight)))
            {
                SetRepairTargetCondition(90f);
            }
            if (GUILayout.Button("95%", GUILayout.Width(65f), GUILayout.Height(ControlHeight)))
            {
                SetRepairTargetCondition(95f);
            }
            if (GUILayout.Button("100%", GUILayout.Width(65f), GUILayout.Height(ControlHeight)))
            {
                SetRepairTargetCondition(100f);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            bool newAutoReposition = GUILayout.Toggle(autoRepositionIdleShips, "Move idle ships to work", GUILayout.Width(220f), GUILayout.Height(ControlHeight));
            if (newAutoReposition != autoRepositionIdleShips)
            {
                autoRepositionIdleShips = newAutoReposition;
                PlayerPrefs.SetInt("TO2FA.AutoRepositionIdleShips", autoRepositionIdleShips ? 1 : 0);
                PlayerPrefs.Save();
                AddDecisionLog("Idle-ship repositioning " + (autoRepositionIdleShips ? "enabled." : "disabled."));
            }

            GUILayout.Label(string.Format("Reposition after idle for: {0:0} day(s)", idleRepositionDays), GUILayout.Width(255f));
            float newIdleDays = GUILayout.HorizontalSlider(idleRepositionDays, IdleRepositionMinDays, IdleRepositionMaxDays, GUILayout.MinWidth(200f), GUILayout.ExpandWidth(true));
            newIdleDays = Mathf.Round(newIdleDays);
            if (Mathf.Abs(newIdleDays - idleRepositionDays) > 0.1f)
            {
                idleRepositionDays = Mathf.Clamp(newIdleDays, IdleRepositionMinDays, IdleRepositionMaxDays);
                PlayerPrefs.SetFloat("TO2FA.IdleRepositionDays", idleRepositionDays);
                PlayerPrefs.Save();
                AddDecisionLog(string.Format("Idle ships will reposition after {0:0} idle day(s).", idleRepositionDays));
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Auto", GUILayout.Width(70f));
            GUILayout.Label("Ship", GUILayout.Width(285f));
            GUILayout.Label("Status", GUILayout.Width(125f));
            GUILayout.Label("Harbor", GUILayout.Width(185f));
            GUILayout.Label("Dest", GUILayout.Width(185f));
            GUILayout.Label("Cond", GUILayout.Width(75f));
            GUILayout.Label("Fuel", GUILayout.Width(75f));
            GUILayout.Label("Plan", GUILayout.Width(90f));
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Max(220f, height * 0.40f)));
            for (int i = 0; i < ships.Count; i++)
            {
                DrawShipRow(ships[i]);
            }
            GUILayout.EndScrollView();

            GUILayout.Label("Decision log");
            logScroll = GUILayout.BeginScrollView(logScroll, GUILayout.Height(Mathf.Max(120f, height * 0.22f)));
            for (int i = 0; i < decisionLog.Count; i++)
            {
                GUILayout.Label(decisionLog[i]);
            }
            GUILayout.EndScrollView();

            GUILayout.Label("F8 run enabled ships. F9 hide panel. F10 refresh fleet.");
            GUILayout.EndArea();

            BlockClickThrough(panelRect);
        }

        private Texture2D GetPanelBackgroundTexture()
        {
            if (panelBackgroundTexture == null)
            {
                panelBackgroundTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                panelBackgroundTexture.hideFlags = HideFlags.HideAndDontSave;
                panelBackgroundTexture.wrapMode = TextureWrapMode.Clamp;
                panelBackgroundTexture.SetPixel(0, 0, new Color(0.07f, 0.08f, 0.11f, 0.97f));
                panelBackgroundTexture.Apply();
            }

            return panelBackgroundTexture;
        }

        // Consume mouse events that land on the panel so they do not fall through to the game world.
        private void BlockClickThrough(Rect panelRect)
        {
            Event current = Event.current;
            if (current == null || !panelRect.Contains(current.mousePosition))
            {
                return;
            }

            EventType type = current.type;
            if (type == EventType.MouseDown
                || type == EventType.MouseUp
                || type == EventType.MouseDrag
                || type == EventType.ScrollWheel)
            {
                current.Use();
            }
        }

        private bool ControllersReady
        {
            get { return dsqLite != null && staticSqLite != null && shipFactory != null; }
        }

        private bool IsGameSessionActive()
        {
            return gameSessionActive && playerId > 0 && ships.Count > 0;
        }

        private void ResolveRuntimeUiHooks()
        {
            if (runtimeUiHooksResolved)
            {
                return;
            }

            runtimeUiHooksResolved = true;
            strikePopupType = FindType("Cargo.StrikePopup");
            if (strikePopupType != null)
            {
                strikePopupButtonWaitMethod = strikePopupType.GetMethod(
                    "ButtonWait",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            customsPopupType = FindType("Cargo.MenuRandomEventsCustomsControl");

            Type windowManagerType = FindType("Deck13HH.UI.WindowManager");
            if (windowManagerType != null)
            {
                closeWindowMethod = windowManagerType.GetMethod(
                    "CloseWindow",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(string) },
                    null);
                getWindowByIdentifierMethod = windowManagerType.GetMethod(
                    "GetWindowByIdentifier",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(string) },
                    null);
            }

            newstickerItemType = FindType("Cargo.NewstickerItem");
            if (newstickerItemType != null)
            {
                newstickerMoveToArchiveMethod = newstickerItemType.GetMethod(
                    "MoveToArchive",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (strikePopupButtonWaitMethod == null || closeWindowMethod == null || getWindowByIdentifierMethod == null || newstickerMoveToArchiveMethod == null)
            {
                Debug.Log(LogPrefix + " Runtime UI hooks partially available: StrikePopup="
                    + (strikePopupButtonWaitMethod != null)
                    + ", CloseWindow="
                    + (closeWindowMethod != null)
                    + ", GetWindow="
                    + (getWindowByIdentifierMethod != null)
                    + ", NewstickerArchive="
                    + (newstickerMoveToArchiveMethod != null));
            }
        }

        private void HandleRuntimeUiWatchdogs()
        {
            ResolveRuntimeUiHooks();
            HandleBlockingPopupTimeouts();
            HandleFleetNotificationConsolidation();
        }

        private void ResetRuntimeUiWatchdogs()
        {
            blockingPopupFirstSeenRealtimeByName.Clear();
            newstickerFirstSeenRealtimeById.Clear();
            newstickerArchivedIds.Clear();
            nextFleetNotificationScan = 0f;
        }

        private void HandleBlockingPopupTimeouts()
        {
            bool strikePopupOpen = IsNativeWindowOpen("StrikePopup");
            object strikePopup = strikePopupOpen ? GetNativeWindowComponent("StrikePopup", strikePopupType) : null;
            if (TrackPopupAndCheckTimeout("StrikePopup", strikePopupOpen && strikePopup != null, BlockingPopupTimeoutRealtimeSeconds))
            {
                DismissTugboatStrikePopup(strikePopup);
            }

            bool customsPopupOpen = IsNativeWindowOpen("MenuRandomEventsCustomsControl");
            if (TrackPopupAndCheckTimeout("MenuRandomEventsCustomsControl", customsPopupOpen, BlockingPopupTimeoutRealtimeSeconds))
            {
                DismissCustomsControlPopup();
            }
        }

        private bool TrackPopupAndCheckTimeout(string popupName, bool popupOpen, float timeoutSeconds)
        {
            if (!popupOpen)
            {
                blockingPopupFirstSeenRealtimeByName.Remove(popupName);
                return false;
            }

            float now = Time.realtimeSinceStartup;
            float firstSeen;
            if (!blockingPopupFirstSeenRealtimeByName.TryGetValue(popupName, out firstSeen) || firstSeen > now)
            {
                blockingPopupFirstSeenRealtimeByName[popupName] = now;
                return false;
            }

            return now - firstSeen >= timeoutSeconds;
        }

        private void DismissTugboatStrikePopup(object strikePopup)
        {
            if (strikePopupButtonWaitMethod == null)
            {
                blockingPopupFirstSeenRealtimeByName["StrikePopup"] = Time.realtimeSinceStartup;
                AddDecisionLog("Tugboat strike popup reached its timeout, but the native wait button hook is unavailable.");
                return;
            }

            try
            {
                strikePopupButtonWaitMethod.Invoke(strikePopup, null);
                blockingPopupFirstSeenRealtimeByName.Remove("StrikePopup");
                AddDecisionLog(string.Format(
                    "Tugboat strike popup timed out after {0:0} seconds; chose Wait so the game can continue.",
                    BlockingPopupTimeoutRealtimeSeconds));
            }
            catch (Exception ex)
            {
                blockingPopupFirstSeenRealtimeByName["StrikePopup"] = Time.realtimeSinceStartup;
                AddDecisionLog("Tugboat strike timeout failed: " + UnwrapMessage(ex));
            }
        }

        private void DismissCustomsControlPopup()
        {
            if (closeWindowMethod == null)
            {
                blockingPopupFirstSeenRealtimeByName["MenuRandomEventsCustomsControl"] = Time.realtimeSinceStartup;
                AddDecisionLog("Customs search popup reached its timeout, but the native close hook is unavailable.");
                return;
            }

            try
            {
                closeWindowMethod.Invoke(null, new object[] { "MenuRandomEventsCustomsControl" });
                blockingPopupFirstSeenRealtimeByName.Remove("MenuRandomEventsCustomsControl");
                AddDecisionLog(string.Format(
                    "Customs search popup timed out after {0:0} seconds; closed notification so the game can continue.",
                    BlockingPopupTimeoutRealtimeSeconds));
            }
            catch (Exception ex)
            {
                blockingPopupFirstSeenRealtimeByName["MenuRandomEventsCustomsControl"] = Time.realtimeSinceStartup;
                AddDecisionLog("Customs search timeout failed: " + UnwrapMessage(ex));
            }
        }

        private void HandleFleetNotificationConsolidation()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextFleetNotificationScan)
            {
                return;
            }

            nextFleetNotificationScan = now + FleetNotificationScanRealtimeSeconds;
            if (newstickerItemType == null || newstickerMoveToArchiveMethod == null)
            {
                return;
            }

            UnityEngine.Object[] items = FindRuntimeObjectsOfType(newstickerItemType);
            Dictionary<int, bool> visibleIds = new Dictionary<int, bool>();
            Dictionary<int, List<FleetNoticeSnapshot>> noticesByType = new Dictionary<int, List<FleetNoticeSnapshot>>();

            for (int i = 0; i < items.Length; i++)
            {
                FleetNoticeSnapshot notice = ReadFleetNoticeSnapshot(items[i], now);
                if (notice == null)
                {
                    continue;
                }

                visibleIds[notice.Id] = true;
                if (!IsConsolidatableFleetNewsType(notice.NewsType) || newstickerArchivedIds.Contains(notice.Id))
                {
                    continue;
                }

                List<FleetNoticeSnapshot> list;
                if (!noticesByType.TryGetValue(notice.NewsType, out list))
                {
                    list = new List<FleetNoticeSnapshot>();
                    noticesByType[notice.NewsType] = list;
                }

                list.Add(notice);
            }

            PruneFleetNoticeTracking(visibleIds);

            foreach (KeyValuePair<int, List<FleetNoticeSnapshot>> pair in noticesByType)
            {
                CondenseFleetNoticeGroup(pair.Key, pair.Value, now);
            }
        }

        private FleetNoticeSnapshot ReadFleetNoticeSnapshot(UnityEngine.Object item, float now)
        {
            if (item == null || !IsRuntimeObjectActive(item))
            {
                return null;
            }

            object itemData = ReadField(item, "itemData");
            if (itemData == null)
            {
                return null;
            }

            int id = ReadInt(itemData, "id");
            if (id <= 0)
            {
                return null;
            }

            float firstSeen;
            if (!newstickerFirstSeenRealtimeById.TryGetValue(id, out firstSeen) || firstSeen > now)
            {
                firstSeen = now;
                newstickerFirstSeenRealtimeById[id] = firstSeen;
            }

            FleetNoticeSnapshot notice = new FleetNoticeSnapshot();
            notice.Item = item;
            notice.Id = id;
            notice.NewsType = ReadInt(itemData, "newsType");
            notice.Headline = ReadString(itemData, "headline");
            notice.FirstSeenRealtime = firstSeen;
            return notice;
        }

        private void CondenseFleetNoticeGroup(int newsType, List<FleetNoticeSnapshot> notices, float now)
        {
            int overflow = notices.Count - FleetNotificationMaxVisiblePerType;
            if (overflow <= 0)
            {
                return;
            }

            notices.Sort(delegate(FleetNoticeSnapshot left, FleetNoticeSnapshot right)
            {
                return left.FirstSeenRealtime.CompareTo(right.FirstSeenRealtime);
            });

            int archived = 0;
            for (int i = 0; i < notices.Count && archived < overflow; i++)
            {
                FleetNoticeSnapshot notice = notices[i];
                if (now - notice.FirstSeenRealtime < FleetNotificationGraceRealtimeSeconds)
                {
                    continue;
                }

                if (TryArchiveFleetNotice(notice))
                {
                    archived++;
                }
            }

            if (archived > 0)
            {
                AddDecisionLog(string.Format(
                    "Condensed {0} older {1} notice(s); keeping the newest {2} visible.",
                    archived,
                    GetFleetNoticeLabel(newsType),
                    FleetNotificationMaxVisiblePerType));
            }
        }

        private bool TryArchiveFleetNotice(FleetNoticeSnapshot notice)
        {
            if (notice == null || notice.Item == null || newstickerMoveToArchiveMethod == null)
            {
                return false;
            }

            try
            {
                newstickerMoveToArchiveMethod.Invoke(notice.Item, null);
                newstickerArchivedIds.Add(notice.Id);
                newstickerFirstSeenRealtimeById.Remove(notice.Id);
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog("Failed to condense " + GetFleetNoticeLabel(notice.NewsType) + " notice: " + UnwrapMessage(ex));
                return false;
            }
        }

        private void PruneFleetNoticeTracking(Dictionary<int, bool> visibleIds)
        {
            List<int> staleIds = new List<int>();
            foreach (KeyValuePair<int, float> pair in newstickerFirstSeenRealtimeById)
            {
                if (!visibleIds.ContainsKey(pair.Key))
                {
                    staleIds.Add(pair.Key);
                }
            }

            for (int i = 0; i < staleIds.Count; i++)
            {
                newstickerFirstSeenRealtimeById.Remove(staleIds[i]);
            }

            staleIds.Clear();
            foreach (int id in newstickerArchivedIds)
            {
                if (!visibleIds.ContainsKey(id))
                {
                    staleIds.Add(id);
                }
            }

            for (int i = 0; i < staleIds.Count; i++)
            {
                newstickerArchivedIds.Remove(staleIds[i]);
            }
        }

        private static bool IsConsolidatableFleetNewsType(int newsType)
        {
            return newsType == 0 || newsType == 2 || newsType == 3 || newsType == 4;
        }

        private static string GetFleetNoticeLabel(int newsType)
        {
            switch (newsType)
            {
                case 0:
                    return "ship arrival/job feedback";
                case 2:
                    return "contraband scanner";
                case 3:
                    return "repair complete";
                case 4:
                    return "upgrade complete";
                default:
                    return "fleet";
            }
        }

        private object GetNativeWindow(string identifier)
        {
            if (getWindowByIdentifierMethod == null || string.IsNullOrEmpty(identifier))
            {
                return null;
            }

            try
            {
                return getWindowByIdentifierMethod.Invoke(null, new object[] { identifier });
            }
            catch
            {
                return null;
            }
        }

        private bool IsNativeWindowOpen(string identifier)
        {
            object window = GetNativeWindow(identifier);
            if (window == null)
            {
                return false;
            }

            try
            {
                PropertyInfo property = window.GetType().GetProperty(
                    "IsOpen",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null)
                {
                    return Convert.ToBoolean(property.GetValue(window, null));
                }

                MethodInfo getter = window.GetType().GetMethod(
                    "get_IsOpen",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (getter != null)
                {
                    return Convert.ToBoolean(getter.Invoke(window, null));
                }
            }
            catch
            {
            }

            return IsRuntimeObjectActive(window);
        }

        private object GetNativeWindowComponent(string identifier, Type componentType)
        {
            object window = GetNativeWindow(identifier);
            Component component = window as Component;
            if (component != null && componentType != null)
            {
                try
                {
                    Component found = component.GetComponent(componentType);
                    if (found != null)
                    {
                        return found;
                    }
                }
                catch
                {
                }
            }

            return FindActiveRuntimeObject(componentType);
        }

        private static object FindActiveRuntimeObject(Type type)
        {
            if (type == null)
            {
                return null;
            }

            try
            {
                object instance = UnityEngine.Object.FindObjectOfType(type);
                return IsRuntimeObjectActive(instance) ? instance : null;
            }
            catch
            {
                return null;
            }
        }

        private static UnityEngine.Object[] FindRuntimeObjectsOfType(Type type)
        {
            if (type == null)
            {
                return new UnityEngine.Object[0];
            }

            try
            {
                UnityEngine.Object[] items = UnityEngine.Object.FindObjectsOfType(type);
                return items == null ? new UnityEngine.Object[0] : items;
            }
            catch
            {
                return new UnityEngine.Object[0];
            }
        }

        private static bool IsRuntimeObjectActive(object instance)
        {
            UnityEngine.Object unityObject = instance as UnityEngine.Object;
            if (unityObject == null)
            {
                return false;
            }

            Component component = instance as Component;
            if (component != null)
            {
                GameObject gameObject = component.gameObject;
                return gameObject != null && gameObject.activeInHierarchy;
            }

            GameObject directGameObject = instance as GameObject;
            return directGameObject != null && directGameObject.activeInHierarchy;
        }

        private void DrawShipRow(ShipSnapshot ship)
        {
            GUILayout.BeginHorizontal();
            bool enabled = IsCaptainEnabled(ship.PlayerShipId);
            bool next = GUILayout.Toggle(enabled, enabled ? "On" : "Off", GUILayout.Width(70f), GUILayout.Height(ShipRowHeight));
            if (next != enabled)
            {
                SetCaptainEnabled(ship.PlayerShipId, next);
            }

            GUILayout.Label(TrimForUi("#" + ship.PlayerShipId + " " + ship.Name, 36), GUILayout.Width(285f), GUILayout.Height(ShipRowHeight));
            GUILayout.Label(TrimForUi(GetDisplayStatus(ship), 18), GUILayout.Width(125f), GUILayout.Height(ShipRowHeight));
            GUILayout.Label(TrimForUi(ship.CurrentHarbor, 24), GUILayout.Width(185f), GUILayout.Height(ShipRowHeight));
            GUILayout.Label(TrimForUi(ship.DestinationHarbor, 24), GUILayout.Width(185f), GUILayout.Height(ShipRowHeight));
            GUILayout.Label(string.Format("{0:0}%", ship.Condition), GUILayout.Width(75f), GUILayout.Height(ShipRowHeight));
            GUILayout.Label(string.Format("{0:0}", ship.FuelLoaded), GUILayout.Width(75f), GUILayout.Height(ShipRowHeight));

            if (GUILayout.Button("Plan", GUILayout.Width(90f), GUILayout.Height(ShipRowHeight)))
            {
                EvaluateShip(ship, "single ship");
            }
            GUILayout.EndHorizontal();
        }

        private void RefreshControllers()
        {
            dsqLite = FindController("Cargo.DynamicSQLiteController", "Database");
            staticSqLite = FindController("Cargo.StaticSQLiteController", "Database");
            shipFactory = FindController("Cargo.ShipFactory", "ShipFactory");
            commissionFactory = FindController("Cargo.Commissions.CommissionFactory", null);

            if (!ControllersReady)
            {
                statusText = "Waiting for game controllers...";
                return;
            }

            Type dsqLiteType = dsqLite.GetType();
            getPlayerIdMethod = dsqLiteType.GetMethod("GetPlayerID", Type.EmptyTypes);
            getPlayerCreditsMethod = dsqLiteType.GetMethod("GetPlayerCredits", new Type[] { typeof(int) });
            getAllPlayerShipsMethod = dsqLiteType.GetMethod("GetALLPlayerShips", new Type[] { typeof(int) });
            getPlayerShipMethod = dsqLiteType.GetMethod("GetPlayerShip", new Type[] { typeof(int) });
            getAllJobsFromPlayerShipMethod = dsqLiteType.GetMethod("GetAllJobsFromPlayerShip", new Type[] { typeof(int) });
            updatePlayerShipAiStateMethod = dsqLiteType.GetMethod("UpdatePlayerShipAiState", new Type[] { typeof(int), typeof(bool) });
            updateJobPlayerShipMethod = dsqLiteType.GetMethod("UpdatePlayerShipIDFromJob", new Type[] { typeof(int), typeof(int) });
            setShipDontLoadJobsMethod = dsqLiteType.GetMethod("SetPlayerShipAIStateDontLoadJobs", new Type[] { typeof(int), typeof(bool) });
            setShipGlobalDestinationMethod = dsqLiteType.GetMethod("SetPlayerShipAIStateGlobalDestination", new Type[] { typeof(int), typeof(string) });
            setShipSmugglersWareMethod = dsqLiteType.GetMethod("SetShipSmugglersWare", new Type[] { typeof(int), typeof(bool) });
            updatePlayerShipDestinationHarborMethod = dsqLiteType.GetMethod("UpdatePlayerShipDestinationHarbor", new Type[] { typeof(int), typeof(string) });
            getJobsFromStartHarborMethod = dsqLiteType.GetMethod(
                "GetAllJobsFromStartHarbor",
                new Type[] { typeof(string), typeof(string), typeof(int) });
            Type staticSqLiteType = staticSqLite.GetType();
            getHarborMethod = staticSqLiteType.GetMethod("GetHarbor", new Type[] { typeof(string) });
            getFreightAttributesMethod = staticSqLiteType.GetMethod("GetFreightAttributes", Type.EmptyTypes);
            getRepairDockHarborsMethod = staticSqLiteType.GetMethod("GetRepairDockHarbors", Type.EmptyTypes);
            getUpgradeDockHarborsMethod = staticSqLiteType.GetMethod("GetUpgradeDockHarbors", Type.EmptyTypes);
            getUpgradeExportFromHarborMethod = staticSqLiteType.GetMethod("GetUpgradeExportFromHarbor", new Type[] { typeof(string) });
            getBusinessSharesFromRegionMethod = staticSqLiteType.GetMethod("GetBusinessSharesFromRegion", new Type[] { typeof(int) });
            getHarborDistanceMethod = staticSqLiteType.GetMethod("GetHarborDistance", new Type[] { typeof(string), typeof(string), typeof(int), typeof(bool) });
            getHarborsFromMethod = staticSqLiteType.GetMethod("GetHarborsFrom", new Type[] { typeof(string), typeof(float), typeof(float), typeof(int) });
            getRegionOfHarborMethod = staticSqLiteType.GetMethod("GetRegionOfHarbor", new Type[] { typeof(string) });
            getShipClassesMethod = staticSqLiteType.GetMethod("GetShipClasses", new Type[] { typeof(int) });
            Type playerShipsType = FindType("DynamicTablePlayerShips");
            Type shipFactoryType = shipFactory.GetType();
            if (playerShipsType != null)
            {
                repairPlayerShipMethod = shipFactoryType.GetMethod(
                    "RepairPlayerShip",
                    new Type[] { playerShipsType, typeof(int), typeof(long), typeof(float), typeof(bool), typeof(bool) });
                getRemainingConditionOnArrivalMethod = shipFactoryType.GetMethod(
                    "GetRemainingConditionOnArrival",
                    new Type[] { playerShipsType, typeof(string), typeof(string) });
            }
            sendShipToDestinationMethod = shipFactoryType.GetMethod("SendShipToDestination", new Type[] { typeof(int), typeof(string) });
            unloadCommissionsFromShipMethod = null;
            if (commissionFactory != null)
            {
                unloadCommissionsFromShipMethod = commissionFactory.GetType().GetMethod("UnloadCommissionsFromShip", new Type[] { typeof(int) });
            }
            upgradePlayerShipMethod = shipFactoryType.GetMethod("UpgradePlayerShip", new Type[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(long) });
            getUpgradeDockOwnerMethod = shipFactoryType.GetMethod("GetUpgradeDockOwner", new Type[] { typeof(int) });
            getShipSinkChanceMethod = shipFactoryType.GetMethod("GetShipSinkChance", new Type[] { typeof(int), typeof(float) });
            getDaysForRepairMethod = shipFactoryType.GetMethod("GetDaysForRepair", new Type[] { typeof(float), typeof(int) });
            Type timerType = FindType("Deck13HH.Timer");
            if (timerType != null)
            {
                getCurrentDateTimeMethod = timerType.GetMethod("get_CurrentDateTime", BindingFlags.Public | BindingFlags.Static);
                getCurrentInGameSpeedMethod = timerType.GetMethod("get_CurrentInGameSpeed", BindingFlags.Public | BindingFlags.Static);
            }
            cargoEventType = FindType("Deck13HH.EventManagement.CargoEventType");
            Type eventManagerType = FindType("Deck13HH.EventManagement.EventManager");
            if (eventManagerType != null && cargoEventType != null)
            {
                sendEventMethod = eventManagerType.GetMethod(
                    "SendEvent",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(object), cargoEventType, typeof(object) },
                    null);
            }
            ResolveRuntimeUiHooks();

            statusText = "Controllers ready. Toggle ships into TO2 Captain mode.";
            Debug.Log(LogPrefix + " Controllers ready for fleet automation.");
            RefreshContrabandFreightCache();
            RefreshFleet(true);
        }

        private void RefreshFleet(bool logResult)
        {
            nextRefresh = Time.realtimeSinceStartup + 5f;
            if (!ControllersReady)
            {
                RefreshControllers();
                return;
            }

            try
            {
                ships.Clear();
                playerId = ToInt(getPlayerIdMethod.Invoke(dsqLite, null));
                playerCredits = ToLong(getPlayerCreditsMethod.Invoke(dsqLite, new object[] { playerId }));
                RefreshRepairTravelSafetyFloor();

                Dictionary<int, bool> visibleShipIds = new Dictionary<int, bool>();
                object rawShips = getAllPlayerShipsMethod.Invoke(dsqLite, new object[] { playerId });
                IEnumerable enumerableShips = rawShips as IEnumerable;
                if (enumerableShips != null)
                {
                    foreach (object rawShip in enumerableShips)
                    {
                        ShipSnapshot ship = ShipSnapshot.From(rawShip);
                        ships.Add(ship);
                        bool isNewShip = !PlayerPrefs.HasKey(CaptainPrefsPrefix + ship.PlayerShipId);
                        EnsureCaptainPreferenceLoaded(ship.PlayerShipId);
                        if (isNewShip && autoPilotAllShips && !IsCaptainEnabled(ship.PlayerShipId))
                        {
                            AutoEnrollNewShip(ship);
                        }
                        visibleShipIds[ship.PlayerShipId] = true;
                        lastSeenShipNameById[ship.PlayerShipId] = ship.Name;
                        if (missingShipWarnedByShipId.ContainsKey(ship.PlayerShipId))
                        {
                            missingShipWarnedByShipId.Remove(ship.PlayerShipId);
                        }
                    }
                }

                gameSessionActive = playerId > 0 && ships.Count > 0;
                if (gameSessionActive)
                {
                    WarnForMissingEnabledShips(visibleShipIds);
                }

                statusText = gameSessionActive
                    ? string.Format("Controllers ready. {0} player ships visible.", ships.Count)
                    : "Waiting for an active game fleet...";
                if (logResult)
                {
                    if (gameSessionActive)
                    {
                        AddDecisionLog(string.Format("Refreshed {0} ships. Treasury {1:n0}.", ships.Count, playerCredits));
                    }
                    else
                    {
                        Debug.Log(LogPrefix + " Fleet refresh skipped panel availability; no active player fleet yet.");
                    }
                }
            }
            catch (Exception ex)
            {
                gameSessionActive = false;
                statusText = "Refresh failed: " + ex.Message;
                Debug.LogError(LogPrefix + " Refresh failed: " + ex);
            }
        }

        private void EvaluateEnabledShips(string reason)
        {
            RefreshFleet(false);
            if (!ControllersReady)
            {
                AddDecisionLog("Cannot evaluate yet; controllers are not ready.");
                return;
            }

            int enabledCount = 0;
            for (int i = 0; i < ships.Count; i++)
            {
                if (IsCaptainEnabled(ships[i].PlayerShipId))
                {
                    enabledCount++;
                    EvaluateShip(ships[i], reason);
                }
            }

            if (enabledCount == 0)
            {
                AddDecisionLog("No ships are enabled for TO2 Captain mode.");
            }
        }

        private void EvaluateShip(ShipSnapshot ship, string reason)
        {
            if (!ControllersReady)
            {
                AddDecisionLog("Cannot evaluate " + ship.Name + "; controllers are not ready.");
                return;
            }

            string prefix = string.Format("#{0} {1}: ", ship.PlayerShipId, ship.Name);
            long reserve = Math.Max(500000L, Math.Abs(playerCredits) / 5L);
            long upgradeReserve = GetUpgradeTreasuryCushion();

            if (IsRepairPending(ship))
            {
                SetNativeAiState(ship, false);
                AddDecisionLog(prefix + "repair is already in progress; keeping automation on hold.");
                return;
            }

            if (IsUpgradePending(ship))
            {
                SetNativeAiState(ship, false);
                AddDecisionLog(prefix + "upgrade is already in progress; keeping automation on hold.");
                return;
            }

            if (ship.Condition < minimumSailCondition)
            {
                HandleLowConditionShip(ship, prefix, reserve);
                return;
            }

            if (IsDispatchSettling(ship))
            {
                AddDecisionLog(prefix + "waiting for recent dispatch state to settle before taking another action.");
                return;
            }

            if (!IsShipIdleInHarbor(ship))
            {
                AddDecisionLog(prefix + "waiting; ship is not idle in a harbor" + " (" + DescribeShipLocationState(ship) + ").");
                return;
            }

            if (liveActions)
            {
                SetNativeAiState(ship, false);
            }

            if (TryResolveOnboardCargoBeforeAutomation(ship, prefix))
            {
                return;
            }

            if (ship.Condition < minimumSailCondition + 5f && playerCredits > reserve * 2L)
            {
                AddDecisionLog(prefix + string.Format("maintenance soon: condition {0:0}% is close to sail minimum {1:0}%.", ship.Condition, minimumSailCondition));
            }

            if (liveActions && autoUpgrades && TryHandleUpgradeNeed(ship, prefix, upgradeReserve))
            {
                return;
            }

            JobPlan bestPlan = FindBestJobPlan(ship);
            if (bestPlan == null)
            {
                AddDecisionLog(prefix + "no matching " + (allowContrabandCargo ? string.Empty : "legal ") + "available jobs found at " + ship.CurrentHarbor + ".");
            }
            else
            {
                string contrabandNote = bestPlan.HasContraband ? " Includes contraband." : string.Empty;
                string skippedNote = bestPlan.ContrabandSkipped > 0 && !allowContrabandCargo
                    ? string.Format(" Skipped {0} contraband job(s).", bestPlan.ContrabandSkipped)
                    : string.Empty;
                AddDecisionLog(prefix + string.Format(
                    "best route {0} -> {1}, {2} job(s), pay {3:n0}, eta {4}d, score {5:0}, total {6:0}.{7}{8}{9}",
                    bestPlan.Start,
                    bestPlan.End,
                    bestPlan.Jobs.Count,
                    bestPlan.Payment,
                    bestPlan.DistanceInDays,
                    bestPlan.Score,
                    bestPlan.TotalScore,
                    bestPlan.GetChainSummary(),
                    contrabandNote,
                    skippedNote));
            }

            if (bestPlan != null)
            {
                ClearIdleTracking(ship);
                if (liveActions)
                {
                    DispatchJobPlan(ship, bestPlan, reason);
                }
                else
                {
                    AddDecisionLog(prefix + "live actions are OFF; recommendation only.");
                }

                return;
            }

            if (!liveActions)
            {
                AddDecisionLog(prefix + "live actions are OFF; recommendation only.");
                return;
            }

            SetNativeAiState(ship, false);
            ClearNativeCargoGuard(ship);

            if (autoRepositionIdleShips && TryRepositionIdleShip(ship, prefix))
            {
                return;
            }

            AddDecisionLog(prefix + "live dispatch held so native AI cannot choose blocked cargo.");
        }

        private bool TryResolveOnboardCargoBeforeAutomation(ShipSnapshot ship, string prefix)
        {
            List<OnboardJobInfo> onboardJobs = GetOnboardJobs(ship);
            if (onboardJobs.Count == 0)
            {
                return false;
            }

            int jobsAtCurrentHarbor = 0;
            for (int i = 0; i < onboardJobs.Count; i++)
            {
                if (SameHarbor(onboardJobs[i].End, ship.CurrentHarbor))
                {
                    jobsAtCurrentHarbor++;
                }
            }

            if (jobsAtCurrentHarbor > 0)
            {
                TryUnloadOnboardCargoAtCurrentHarbor(ship, prefix, onboardJobs, jobsAtCurrentHarbor);
                return true;
            }

            OnboardDestinationSummary destination = ChooseOnboardCargoDestination(ship, onboardJobs);
            if (destination == null || IsNoneOrEmpty(destination.Harbor))
            {
                AddDecisionLog(prefix + string.Format(
                    "holding; {0} assigned onboard job(s) are present but no valid destination could be resolved.",
                    onboardJobs.Count));
                return true;
            }

            if (!liveActions)
            {
                AddDecisionLog(prefix + string.Format(
                    "onboard cargo guard: {0} assigned job(s) still need {1}; live actions are OFF.",
                    onboardJobs.Count,
                    destination.Harbor));
                return true;
            }

            if (sendShipToDestinationMethod == null)
            {
                AddDecisionLog(prefix + string.Format(
                    "holding; {0} onboard job(s) still need {1}, but the native movement bridge is unavailable.",
                    onboardJobs.Count,
                    destination.Harbor));
                return true;
            }

            JobPlan topUpPlan = FindTopUpPlanForOnboardDestination(ship, destination, onboardJobs);
            if (topUpPlan != null)
            {
                AddDecisionLog(prefix + string.Format(
                    "onboard cargo top-up: adding {0} job(s), {1:n0} pay, toward {2} before finishing existing cargo.",
                    topUpPlan.Jobs.Count,
                    topUpPlan.Payment,
                    destination.Harbor));
                if (DispatchJobPlan(ship, topUpPlan, "onboard cargo top-up"))
                {
                    return true;
                }

                AddDecisionLog(prefix + "onboard cargo top-up failed; routing existing onboard cargo only.");
            }

            try
            {
                SetNativeAiState(ship, false);
                SetNativeCargoGuard(ship, true, destination.Harbor);
                SetNativeDestinationHarbor(ship, destination.Harbor);
                sendShipToDestinationMethod.Invoke(shipFactory, new object[] { ship.PlayerShipId, destination.Harbor });
                ship.DestinationHarbor = destination.Harbor;
                ClearPlannedFreightUpgrade(ship);
                MarkShipDispatched(ship);
                AddDecisionLog(prefix + string.Format(
                    "onboard cargo guard: sailing to {0} to finish {1} assigned job(s), {2:n0} volume, {3:n0} weight, before accepting new work.",
                    destination.Harbor,
                    destination.JobCount,
                    destination.Volume,
                    destination.Weight));
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog(prefix + string.Format(
                    "holding; failed to route onboard cargo to {0}: {1}",
                    destination.Harbor,
                    UnwrapMessage(ex)));
                return true;
            }
        }

        private JobPlan FindTopUpPlanForOnboardDestination(ShipSnapshot ship, OnboardDestinationSummary destination, List<OnboardJobInfo> onboardJobs)
        {
            if (ship == null || destination == null || onboardJobs == null || IsNoneOrEmpty(ship.CurrentHarbor) || IsNoneOrEmpty(destination.Harbor))
            {
                return null;
            }

            int reservedVolume = GetOnboardVolume(onboardJobs);
            int reservedWeight = GetOnboardWeight(onboardJobs);
            if ((ship.Volume > 0 && reservedVolume >= ship.Volume) || (ship.DeadweightTons > 0 && reservedWeight >= ship.DeadweightTons))
            {
                return null;
            }

            int contrabandSkipped = 0;
            int candidateCount = 0;
            Dictionary<string, List<JobCandidate>> candidatesByDestination = CollectJobCandidatesFromHarbor(ship, ship.CurrentHarbor, out contrabandSkipped, out candidateCount);
            if (candidatesByDestination == null)
            {
                return null;
            }

            List<JobCandidate> candidates;
            if (!candidatesByDestination.TryGetValue(destination.Harbor, out candidates))
            {
                return null;
            }

            JobPlan plan = BuildJobPlanForDestination(ship, ship.CurrentHarbor, destination.Harbor, candidates, reservedVolume, reservedWeight);
            if (plan != null)
            {
                plan.ContrabandSkipped = contrabandSkipped;
                plan.CandidateCount = candidateCount;
            }

            return plan;
        }

        private List<OnboardJobInfo> GetOnboardJobs(ShipSnapshot ship)
        {
            List<OnboardJobInfo> jobs = new List<OnboardJobInfo>();
            if (ship == null || getAllJobsFromPlayerShipMethod == null)
            {
                return jobs;
            }

            object rawJobs = null;
            try
            {
                rawJobs = getAllJobsFromPlayerShipMethod.Invoke(dsqLite, new object[] { ship.PlayerShipId });
            }
            catch (Exception ex)
            {
                AddDecisionLog(string.Format("#{0} {1}: onboard cargo lookup failed: {2}", ship.PlayerShipId, ship.Name, UnwrapMessage(ex)));
                return jobs;
            }

            IEnumerable enumerable = rawJobs as IEnumerable;
            if (enumerable == null)
            {
                return jobs;
            }

            foreach (object rawJob in enumerable)
            {
                if (rawJob == null || ReadInt(rawJob, "PlayerShipID") != ship.PlayerShipId)
                {
                    continue;
                }

                OnboardJobInfo job = new OnboardJobInfo();
                job.JobId = ReadInt(rawJob, "JobID");
                job.Start = ReadString(rawJob, "Start");
                job.End = ReadString(rawJob, "End");
                job.Freight = ReadString(rawJob, "Freight");
                job.Volume = ReadInt(rawJob, "Volume");
                job.Weight = ReadInt(rawJob, "Weight");
                job.Payment = ReadLong(rawJob, "Payment");
                job.RawJob = rawJob;
                if (job.JobId > 0)
                {
                    jobs.Add(job);
                }
            }

            return jobs;
        }

        private bool TryUnloadOnboardCargoAtCurrentHarbor(ShipSnapshot ship, string prefix, List<OnboardJobInfo> onboardJobs, int deliverableJobCount)
        {
            if (unloadCommissionsFromShipMethod == null || commissionFactory == null)
            {
                AddDecisionLog(prefix + string.Format(
                    "holding; {0} onboard job(s) are deliverable at {1}, but the native unload bridge is unavailable.",
                    deliverableJobCount,
                    ship.CurrentHarbor));
                return false;
            }

            try
            {
                unloadCommissionsFromShipMethod.Invoke(commissionFactory, new object[] { ship.PlayerShipId });
                ClearNativeCargoGuard(ship);
                if (setShipSmugglersWareMethod != null)
                {
                    setShipSmugglersWareMethod.Invoke(dsqLite, new object[] { ship.PlayerShipId, false });
                    WriteField(ship.RawShip, "HasSmugglerswareLoaded", false);
                }

                AddDecisionLog(prefix + string.Format(
                    "onboard cargo guard: forced native unload of {0} deliverable job(s) at {1} before taking new work.",
                    deliverableJobCount,
                    ship.CurrentHarbor));
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog(prefix + "holding; native unload of onboard cargo failed: " + UnwrapMessage(ex));
                return false;
            }
        }

        private static OnboardDestinationSummary ChooseOnboardCargoDestination(ShipSnapshot ship, List<OnboardJobInfo> jobs)
        {
            Dictionary<string, OnboardDestinationSummary> summaries = new Dictionary<string, OnboardDestinationSummary>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < jobs.Count; i++)
            {
                OnboardJobInfo job = jobs[i];
                if (IsNoneOrEmpty(job.End))
                {
                    continue;
                }

                if (ship != null && SameHarbor(job.End, ship.CurrentHarbor))
                {
                    continue;
                }

                OnboardDestinationSummary summary;
                if (!summaries.TryGetValue(job.End, out summary))
                {
                    summary = new OnboardDestinationSummary();
                    summary.Harbor = job.End;
                    summaries[job.End] = summary;
                }

                summary.JobCount++;
                summary.Volume += Math.Max(0, job.Volume);
                summary.Weight += Math.Max(0, job.Weight);
                summary.Payment += Math.Max(0L, job.Payment);
            }

            OnboardDestinationSummary best = null;
            foreach (KeyValuePair<string, OnboardDestinationSummary> pair in summaries)
            {
                OnboardDestinationSummary summary = pair.Value;
                if (best == null
                    || summary.JobCount > best.JobCount
                    || (summary.JobCount == best.JobCount && summary.Payment > best.Payment))
                {
                    best = summary;
                }
            }

            return best;
        }

        private static int GetOnboardVolume(List<OnboardJobInfo> jobs)
        {
            int volume = 0;
            if (jobs == null)
            {
                return volume;
            }

            for (int i = 0; i < jobs.Count; i++)
            {
                volume += Math.Max(0, jobs[i].Volume);
            }

            return volume;
        }

        private static int GetOnboardWeight(List<OnboardJobInfo> jobs)
        {
            int weight = 0;
            if (jobs == null)
            {
                return weight;
            }

            for (int i = 0; i < jobs.Count; i++)
            {
                weight += Math.Max(0, jobs[i].Weight);
            }

            return weight;
        }

        private static bool SameHarbor(string left, string right)
        {
            return !IsNoneOrEmpty(left)
                && !IsNoneOrEmpty(right)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private void HandleLowConditionShip(ShipSnapshot ship, string prefix, long reserve)
        {
            SetNativeAiState(ship, false);

            if (liveActions && autoRepairs)
            {
                if (HasKnownCurrentHarbor(ship))
                {
                    TryHandleRepairNeed(ship, prefix, reserve);
                }
                else if (IsHeadingToRepairDock(ship))
                {
                    AddDecisionLog(prefix + string.Format("heading to repair dock at {0}; condition {1:0}% is below sail minimum {2:0}%.", ship.DestinationHarbor, ship.Condition, minimumSailCondition));
                }
                else
                {
                    AddDecisionLog(prefix + string.Format("held for maintenance while underway; condition {0:0}% is below sail minimum {1:0}%. Repair routing will run when it reaches a harbor.", ship.Condition, minimumSailCondition));
                }

                return;
            }

            if (playerCredits > reserve)
            {
                AddDecisionLog(prefix + string.Format("held for maintenance: {0:0}% condition is below sail minimum {1:0}%. Repair before automation sends it out.", ship.Condition, minimumSailCondition));
            }
            else
            {
                AddDecisionLog(prefix + string.Format("held for maintenance: {0:0}% condition is below sail minimum {1:0}%, but treasury is near reserve {2:n0}.", ship.Condition, minimumSailCondition, reserve));
            }
        }

        private void TryHandleRepairNeed(ShipSnapshot ship, string prefix, long reserve)
        {
            if (IsHarborRepairDock(ship.CurrentHarbor))
            {
                TryStartRepair(ship, prefix, reserve);
                return;
            }

            TrySendToRepairDock(ship, prefix);
        }

        private bool TryStartRepair(ShipSnapshot ship, string prefix, long reserve)
        {
            RepairPlan plan = BuildRepairPlan(ship, reserve);
            if (!plan.CanStart)
            {
                AddDecisionLog(prefix + plan.Message);
                return false;
            }

            object rawShip = GetLatestRawShip(ship);
            if (rawShip == null)
            {
                AddDecisionLog(prefix + "repair deferred; native ship record is unavailable.");
                return false;
            }

            try
            {
                repairPlayerShipMethod.Invoke(
                    shipFactory,
                    new object[] { rawShip, plan.Region, plan.RepairDockBasePrice, plan.TargetCondition, false, false });
                ship.Condition = plan.TargetCondition;
                ship.Repaired = false;
                ship.RepairFinish = GetCurrentGameDateTime().AddDays(plan.RepairDays);
                SendCargoEvent("SHIP_WILL_NOW_REPAIRED", ship.PlayerShipId);

                string belowMinimumNote = plan.TargetCondition < minimumSailCondition
                    ? " It will remain held below the sail minimum."
                    : string.Empty;
                AddDecisionLog(prefix + string.Format(
                    "repair started at {0}: {1:0}% -> {2:0}% for about {3:n0} credits, {4} day(s).{5}",
                    ship.CurrentHarbor,
                    plan.StartCondition,
                    plan.TargetCondition,
                    plan.EstimatedGrossCost,
                    plan.RepairDays,
                    belowMinimumNote));
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog(prefix + "repair start failed: " + UnwrapMessage(ex));
                return false;
            }
        }

        private RepairPlan BuildRepairPlan(ShipSnapshot ship, long reserve)
        {
            RepairPlan plan = new RepairPlan();
            plan.StartCondition = ship.Condition;

            if (repairPlayerShipMethod == null || getHarborMethod == null || getRegionOfHarborMethod == null || getShipClassesMethod == null)
            {
                plan.Message = "repair deferred; native repair bridge is unavailable.";
                return plan;
            }

            object harbor = GetHarbor(ship.CurrentHarbor);
            if (harbor == null || !ReadBool(harbor, "RepairDock"))
            {
                plan.Message = "held for maintenance; " + ship.CurrentHarbor + " has no repair dock.";
                return plan;
            }

            long repairDockBasePrice = ReadLong(harbor, "RepairDockBasePrice");
            if (repairDockBasePrice <= 0L)
            {
                plan.Message = "repair deferred; repair dock price is unavailable at " + ship.CurrentHarbor + ".";
                return plan;
            }

            object shipClass = null;
            try
            {
                shipClass = getShipClassesMethod.Invoke(staticSqLite, new object[] { ship.Class });
            }
            catch (Exception ex)
            {
                plan.Message = "repair price lookup failed: " + UnwrapMessage(ex);
                return plan;
            }

            float repairBaseFactor = ReadFloat(shipClass, "RepairBaseFactor");
            if (repairBaseFactor <= 0f)
            {
                plan.Message = "repair deferred; class repair factor is unavailable.";
                return plan;
            }

            long grossPricePerPercent = (long)((double)repairDockBasePrice * (double)repairBaseFactor);
            if (grossPricePerPercent <= 0L)
            {
                plan.Message = "repair deferred; repair price is unavailable.";
                return plan;
            }

            float desiredTarget = Mathf.Clamp(repairTargetCondition, minimumSailCondition, 100f);
            long spendable = playerCredits - reserve;
            if (spendable < 0L)
            {
                spendable = 0L;
            }

            float chosenTarget = desiredTarget;
            long desiredCost = EstimateRepairCost(ship.Condition, desiredTarget, grossPricePerPercent);
            if (desiredCost > spendable)
            {
                long affordablePercent = spendable / grossPricePerPercent;
                if (affordablePercent <= 0L)
                {
                    plan.Message = string.Format(
                        "held for maintenance; estimated repair to {0:0}% costs {1:n0}, spendable after reserve is {2:n0}.",
                        desiredTarget,
                        desiredCost,
                        spendable);
                    return plan;
                }

                chosenTarget = Mathf.Min(desiredTarget, ship.Condition + affordablePercent);
            }

            if (chosenTarget <= ship.Condition)
            {
                plan.Message = "repair skipped; target condition is not above current condition.";
                return plan;
            }

            try
            {
                plan.Region = ToInt(getRegionOfHarborMethod.Invoke(staticSqLite, new object[] { ship.CurrentHarbor }));
            }
            catch (Exception ex)
            {
                plan.Message = "repair region lookup failed: " + UnwrapMessage(ex);
                return plan;
            }

            plan.RepairDockBasePrice = repairDockBasePrice;
            plan.TargetCondition = Mathf.Clamp(chosenTarget, ship.Condition, 100f);
            plan.EstimatedGrossCost = EstimateRepairCost(ship.Condition, plan.TargetCondition, grossPricePerPercent);
            plan.RepairDays = GetRepairDays(plan.TargetCondition - ship.Condition, ship.PlayerShipId);
            plan.CanStart = true;
            return plan;
        }

        private bool TrySendToRepairDock(ShipSnapshot ship, string prefix)
        {
            if (sendShipToDestinationMethod == null)
            {
                AddDecisionLog(prefix + "held for maintenance; native movement bridge is unavailable.");
                return false;
            }

            RepairDockCandidate destination = FindBestRepairDock(ship);
            if (destination == null)
            {
                AddDecisionLog(prefix + string.Format(
                    "held for maintenance; no repair dock is reachable with arrival condition at or above {0:0}% (sink risk starts near {1:0}%).",
                    repairTravelSafetyFloor,
                    repairSinkDangerCondition));
                return false;
            }

            return TrySendToServiceDock(ship, prefix, "repair", destination.Harbor, destination.Distance, destination.ArrivalCondition);
        }

        private bool TrySendToServiceDock(ShipSnapshot ship, string prefix, string serviceName, string harbor, float distance, float arrivalCondition)
        {
            JobPlan offsetPlan = FindJobPlanToDestination(ship, ship.CurrentHarbor, harbor);
            if (offsetPlan != null)
            {
                AddDecisionLog(prefix + string.Format(
                    "{0} trip has {1} legal offset job(s) to {2}, pay {3:n0}.",
                    Capitalize(serviceName),
                    offsetPlan.Jobs.Count,
                    harbor,
                    offsetPlan.Payment));

                if (DispatchJobPlan(ship, offsetPlan, serviceName + " dock routing"))
                {
                    return true;
                }

                AddDecisionLog(prefix + serviceName + " cargo offset dispatch failed; trying direct service routing.");
            }

            try
            {
                SetNativeAiState(ship, false);
                SetNativeDestinationHarbor(ship, harbor);
                sendShipToDestinationMethod.Invoke(shipFactory, new object[] { ship.PlayerShipId, harbor });
                ship.DestinationHarbor = harbor;
                if (!string.Equals(serviceName, "upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    ClearPlannedFreightUpgrade(ship);
                }

                MarkShipDispatched(ship);
                AddDecisionLog(prefix + string.Format(
                    "routing to {0} dock at {1} ({2:0} distance units, arrival condition about {3:0}%) before taking more work.",
                    serviceName,
                    harbor,
                    distance,
                    arrivalCondition));
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog(prefix + "failed to route to " + serviceName + " dock: " + UnwrapMessage(ex));
                return false;
            }
        }

        private RepairDockCandidate FindBestRepairDock(ShipSnapshot ship)
        {
            if (getRepairDockHarborsMethod == null || getHarborDistanceMethod == null)
            {
                return null;
            }

            IEnumerable harbors = null;
            try
            {
                harbors = getRepairDockHarborsMethod.Invoke(staticSqLite, null) as IEnumerable;
            }
            catch
            {
                return null;
            }

            if (harbors == null)
            {
                return null;
            }

            bool hasWaypointUpgrade = HasWaypointUpgrade(ship);
            object rawShip = GetLatestRawShip(ship);
            float safeArrivalFloor = repairTravelSafetyFloor;
            RepairDockCandidate best = null;
            foreach (object harbor in harbors)
            {
                string city = ReadString(harbor, "City");
                if (string.IsNullOrEmpty(city) || city == ship.CurrentHarbor)
                {
                    continue;
                }

                int harborClass = ReadInt(harbor, "HarborClass");
                if (harborClass > 0 && ship.Class > harborClass)
                {
                    continue;
                }

                float distance = 0f;
                try
                {
                    distance = Convert.ToSingle(getHarborDistanceMethod.Invoke(staticSqLite, new object[] { ship.CurrentHarbor, city, ship.Class, hasWaypointUpgrade }));
                }
                catch
                {
                    distance = 0f;
                }

                if (distance <= 0f)
                {
                    continue;
                }

                float arrivalCondition = GetRemainingConditionOnArrival(rawShip, ship, city);
                if (arrivalCondition < safeArrivalFloor)
                {
                    continue;
                }

                int sinkChance = GetShipSinkChance(arrivalCondition);
                if (sinkChance > 0)
                {
                    continue;
                }

                if (best == null || distance < best.Distance)
                {
                    best = new RepairDockCandidate();
                    best.Harbor = city;
                    best.Distance = distance;
                    best.ArrivalCondition = arrivalCondition;
                }
            }

            return best;
        }

        // Called when a ship is idle in a harbor with no legal work. After the ship has waited past the
        // configured idle threshold, it deadheads to the nearest class-reachable harbor that does have
        // legal work, so contraband-only or dried-up ports no longer strand the fleet indefinitely.
        private bool TryRepositionIdleShip(ShipSnapshot ship, string prefix)
        {
            if (!HasKnownCurrentHarbor(ship))
            {
                return false;
            }

            DateTime now = GetCurrentGameDateTime();
            DateTime idleSince;
            string idleHarbor;
            bool tracked = idleSinceGameDateByShipId.TryGetValue(ship.PlayerShipId, out idleSince)
                && idleHarborByShipId.TryGetValue(ship.PlayerShipId, out idleHarbor)
                && idleHarbor == ship.CurrentHarbor;
            if (!tracked)
            {
                // First idle tick at this harbor (or the ship moved since we last saw it); start the timer.
                idleSinceGameDateByShipId[ship.PlayerShipId] = now;
                idleHarborByShipId[ship.PlayerShipId] = ship.CurrentHarbor;
                AddDecisionLog(prefix + string.Format(
                    "no work at {0}; will look for another port if still idle after {1:0} day(s).",
                    ship.CurrentHarbor,
                    idleRepositionDays));
                return false;
            }

            double idleDays = (now - idleSince).TotalDays;
            if (idleDays < 0.0)
            {
                // Game clock moved backwards (save reload); restart the idle timer.
                idleSinceGameDateByShipId[ship.PlayerShipId] = now;
                return false;
            }

            if (idleDays < idleRepositionDays)
            {
                AddDecisionLog(prefix + string.Format(
                    "idle {0:0.#} of {1:0} day(s) at {2}; holding before repositioning.",
                    idleDays,
                    idleRepositionDays,
                    ship.CurrentHarbor));
                return false;
            }

            RepairDockCandidate destination = FindBestRepositionHarbor(ship);
            if (destination == null)
            {
                AddDecisionLog(prefix + string.Format(
                    "idle {0:0.#} day(s) at {1} but no reachable harbor with legal work was found; staying put.",
                    idleDays,
                    ship.CurrentHarbor));
                return false;
            }

            AddDecisionLog(prefix + string.Format(
                "idle {0:0.#} day(s) at {1}; repositioning to {2} ({3:0} distance units) which has legal work available.",
                idleDays,
                ship.CurrentHarbor,
                destination.Harbor,
                destination.Distance));

            return TryDeadheadToHarbor(ship, prefix, destination.Harbor, destination.Distance, destination.ArrivalCondition);
        }

        private RepairDockCandidate FindBestRepositionHarbor(ShipSnapshot ship)
        {
            if (getHarborsFromMethod == null || getHarborDistanceMethod == null)
            {
                return null;
            }

            IEnumerable cities;
            try
            {
                cities = getHarborsFromMethod.Invoke(
                    staticSqLite,
                    new object[] { ship.CurrentHarbor, 0f, float.MaxValue, ship.Class }) as IEnumerable;
            }
            catch
            {
                return null;
            }

            if (cities == null)
            {
                return null;
            }

            bool hasWaypointUpgrade = HasWaypointUpgrade(ship);
            List<RepairDockCandidate> reachable = new List<RepairDockCandidate>();
            foreach (object cityObj in cities)
            {
                string city = cityObj as string;
                if (string.IsNullOrEmpty(city) || city == ship.CurrentHarbor)
                {
                    continue;
                }

                float distance;
                try
                {
                    distance = Convert.ToSingle(getHarborDistanceMethod.Invoke(
                        staticSqLite,
                        new object[] { ship.CurrentHarbor, city, ship.Class, hasWaypointUpgrade }));
                }
                catch
                {
                    distance = 0f;
                }

                if (distance <= 0f)
                {
                    continue;
                }

                RepairDockCandidate candidate = new RepairDockCandidate();
                candidate.Harbor = city;
                candidate.Distance = distance;
                reachable.Add(candidate);
            }

            if (reachable.Count == 0)
            {
                return null;
            }

            reachable.Sort(delegate(RepairDockCandidate left, RepairDockCandidate right)
            {
                return left.Distance.CompareTo(right.Distance);
            });

            // Probe the nearest harbors first, bounded so one idle ship cannot fan out job queries fleet-wide.
            object rawShip = GetLatestRawShip(ship);
            int probed = 0;
            for (int i = 0; i < reachable.Count && probed < IdleRepositionMaxHarborsToProbe; i++)
            {
                RepairDockCandidate candidate = reachable[i];
                float arrivalCondition = GetRemainingConditionOnArrival(rawShip, ship, candidate.Harbor);
                if (GetShipSinkChance(arrivalCondition) > 0)
                {
                    continue;
                }

                probed++;
                JobPlan plan = FindBestJobPlanFromHarbor(ship, candidate.Harbor, false);
                if (plan != null)
                {
                    candidate.ArrivalCondition = arrivalCondition;
                    return candidate;
                }
            }

            return null;
        }

        private bool TryDeadheadToHarbor(ShipSnapshot ship, string prefix, string harbor, float distance, float arrivalCondition)
        {
            if (sendShipToDestinationMethod == null)
            {
                AddDecisionLog(prefix + "cannot reposition; native movement bridge is unavailable.");
                return false;
            }

            try
            {
                SetNativeAiState(ship, false);
                ClearNativeCargoGuard(ship);
                SetNativeDestinationHarbor(ship, harbor);
                sendShipToDestinationMethod.Invoke(shipFactory, new object[] { ship.PlayerShipId, harbor });
                ship.DestinationHarbor = harbor;
                ClearPlannedFreightUpgrade(ship);
                MarkShipDispatched(ship);
                AddDecisionLog(prefix + string.Format(
                    "repositioning to {0} (arrival condition about {1:0}%); it will load legal work on arrival.",
                    harbor,
                    arrivalCondition));
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog(prefix + "failed to reposition to " + harbor + ": " + UnwrapMessage(ex));
                return false;
            }
        }

        private bool TryHandleUpgradeNeed(ShipSnapshot ship, string prefix, long reserve)
        {
            if (upgradePlayerShipMethod == null || getUpgradeDockHarborsMethod == null || getRegionOfHarborMethod == null)
            {
                return false;
            }

            if (!HasUpgradeTreasuryCushion())
            {
                return false;
            }

            if (!HasFleetUpgradeTurn(ship))
            {
                AddDecisionLog(prefix + string.Format(
                    "upgrade deferred for fleet balance; ship has {0} upgrade(s), fleet low-water mark allows up to {1}.",
                    ship.UpgradeCount(),
                    GetFleetUpgradeAllowedCount()));
                return false;
            }

            UpgradePlan currentPlan = BuildUpgradePlan(ship, ship.CurrentHarbor, reserve);
            if (currentPlan != null && currentPlan.CanStart)
            {
                return TryStartUpgrade(ship, prefix, currentPlan);
            }

            UpgradeDockCandidate destination = FindBestUpgradeDock(ship, reserve);
            if (destination == null)
            {
                return false;
            }

            SetNativeAiState(ship, false);
            ClearNativeCargoGuard(ship);
            SetPlannedFreightUpgrade(ship, destination.Plan.UpgradeId);
            bool routed = TrySendToServiceDock(ship, prefix, "upgrade", destination.Harbor, destination.Distance, destination.ArrivalCondition);
            if (!routed)
            {
                ClearPlannedFreightUpgrade(ship);
            }

            return routed;
        }

        private bool HasFleetUpgradeTurn(ShipSnapshot ship)
        {
            return ship.UpgradeCount() <= GetFleetUpgradeAllowedCount();
        }

        private int GetFleetUpgradeAllowedCount()
        {
            int minimumUpgradeCount = int.MaxValue;
            for (int i = 0; i < ships.Count; i++)
            {
                ShipSnapshot fleetShip = ships[i];
                if (!IsCaptainEnabled(fleetShip.PlayerShipId))
                {
                    continue;
                }

                minimumUpgradeCount = Math.Min(minimumUpgradeCount, fleetShip.UpgradeCount());
            }

            if (minimumUpgradeCount == int.MaxValue)
            {
                return int.MaxValue;
            }

            return minimumUpgradeCount + UpgradeFleetLeadAllowance;
        }

        private long GetUpgradeTreasuryCushion()
        {
            long perShipReserve = UpgradeTreasuryCushionPerShip * Math.Max(1, ships.Count);
            return Math.Min(perShipReserve, UpgradeTreasuryCapTotal);
        }

        private bool HasUpgradeTreasuryCushion()
        {
            return playerCredits >= GetUpgradeTreasuryCushion();
        }

        private bool TryStartUpgrade(ShipSnapshot ship, string prefix, UpgradePlan plan)
        {
            object rawShip = GetLatestRawShip(ship);
            if (rawShip == null)
            {
                AddDecisionLog(prefix + "upgrade deferred; native ship record is unavailable.");
                return false;
            }

            try
            {
                SetNativeAiState(ship, false);
                ClearNativeCargoGuard(ship);
                upgradePlayerShipMethod.Invoke(
                    shipFactory,
                    new object[] { ship.PlayerShipId, plan.UpgradeId, plan.Slot, plan.UpgradeDockOwner, plan.UpgradeDockOwnerShares });

                ship.SetUpgrade(plan.Slot, plan.UpgradeId);
                ship.Upgraded = false;
                WriteField(rawShip, "Upgraded", false);
                WriteField(rawShip, "Upgrade" + plan.Slot, plan.UpgradeId);
                ClearPlannedFreightUpgrade(ship);

                AddDecisionLog(prefix + string.Format(
                    "upgrade started at {0}: {1} (ID {2}, slot {3}) for about {4:n0} credits.",
                    ship.CurrentHarbor,
                    plan.Name,
                    plan.UpgradeId,
                    plan.Slot,
                    plan.EstimatedCost));
                return true;
            }
            catch (Exception ex)
            {
                ClearPlannedFreightUpgrade(ship);
                AddDecisionLog(prefix + "upgrade start failed: " + UnwrapMessage(ex));
                return false;
            }
        }

        private UpgradeDockCandidate FindBestUpgradeDock(ShipSnapshot ship, long reserve)
        {
            if (getUpgradeDockHarborsMethod == null || getHarborDistanceMethod == null)
            {
                return null;
            }

            IEnumerable harbors = null;
            try
            {
                harbors = getUpgradeDockHarborsMethod.Invoke(staticSqLite, null) as IEnumerable;
            }
            catch
            {
                return null;
            }

            if (harbors == null)
            {
                return null;
            }

            bool hasWaypointUpgrade = HasWaypointUpgrade(ship);
            object rawShip = GetLatestRawShip(ship);
            UpgradeDockCandidate best = null;
            foreach (object harbor in harbors)
            {
                string city = ReadString(harbor, "City");
                if (string.IsNullOrEmpty(city) || city == ship.CurrentHarbor)
                {
                    continue;
                }

                int harborClass = ReadInt(harbor, "HarborClass");
                if (harborClass > 0 && ship.Class > harborClass)
                {
                    continue;
                }

                UpgradePlan plan = BuildUpgradePlan(ship, city, reserve);
                if (plan == null || !plan.CanStart)
                {
                    continue;
                }

                float distance = 0f;
                try
                {
                    distance = Convert.ToSingle(getHarborDistanceMethod.Invoke(staticSqLite, new object[] { ship.CurrentHarbor, city, ship.Class, hasWaypointUpgrade }));
                }
                catch
                {
                    distance = 0f;
                }

                if (distance <= 0f)
                {
                    continue;
                }

                float arrivalCondition = GetRemainingConditionOnArrival(rawShip, ship, city);
                int sinkChance = GetShipSinkChance(arrivalCondition);
                if (sinkChance > 0)
                {
                    continue;
                }

                if (best == null || plan.Score > best.Plan.Score || (Math.Abs(plan.Score - best.Plan.Score) < 0.1 && distance < best.Distance))
                {
                    best = new UpgradeDockCandidate();
                    best.Harbor = city;
                    best.Distance = distance;
                    best.ArrivalCondition = arrivalCondition;
                    best.Plan = plan;
                }
            }

            return best;
        }

        private UpgradePlan BuildUpgradePlan(ShipSnapshot ship, string harborName, long reserve)
        {
            if (!IsHarborUpgradeDock(harborName))
            {
                return null;
            }

            long spendable = playerCredits - reserve;
            if (spendable <= 0L)
            {
                return null;
            }

            UpgradePlan best = null;
            for (int i = 0; i < UpgradeCandidateIds.Length; i++)
            {
                int upgradeId = UpgradeCandidateIds[i];
                if (!IsUpgradeCompatibleWithShip(ship, upgradeId) || !IsUpgradeAvailableAtHarbor(ship, harborName, upgradeId))
                {
                    continue;
                }

                int slot = GetUpgradeSlotForShip(ship, upgradeId);
                if (slot == 0)
                {
                    continue;
                }

                long cost = EstimateUpgradeCost(ship, upgradeId);
                if (cost <= 0L || cost > spendable)
                {
                    continue;
                }

                UpgradePlan plan = new UpgradePlan();
                plan.CanStart = true;
                plan.Harbor = harborName;
                plan.UpgradeId = upgradeId;
                plan.Slot = slot;
                plan.Name = GetUpgradeName(upgradeId);
                plan.EstimatedCost = cost;
                plan.Score = ScoreUpgradeCandidate(ship, harborName, upgradeId, cost);
                plan.Region = GetRegionOfHarbor(harborName);
                plan.UpgradeDockOwner = GetUpgradeDockOwner(plan.Region);
                plan.UpgradeDockOwnerShares = EstimateUpgradeDockOwnerShares(plan.Region, cost);

                if (best == null || plan.Score > best.Score || (Math.Abs(plan.Score - best.Score) < 0.1 && plan.EstimatedCost < best.EstimatedCost))
                {
                    best = plan;
                }
            }

            return best;
        }

        private bool IsHarborUpgradeDock(string harborName)
        {
            object harbor = GetHarbor(harborName);
            return harbor != null && ReadBool(harbor, "UpgradeDock");
        }

        private bool IsHeadingToUpgradeDock(ShipSnapshot ship)
        {
            return ship != null
                && !string.IsNullOrEmpty(ship.DestinationHarbor)
                && ship.DestinationHarbor != ship.CurrentHarbor
                && IsHarborUpgradeDock(ship.DestinationHarbor);
        }

        private bool IsHarborRepairDock(string harborName)
        {
            object harbor = GetHarbor(harborName);
            return harbor != null && ReadBool(harbor, "RepairDock");
        }

        private bool IsHeadingToRepairDock(ShipSnapshot ship)
        {
            return ship != null
                && !string.IsNullOrEmpty(ship.DestinationHarbor)
                && ship.DestinationHarbor != ship.CurrentHarbor
                && IsHarborRepairDock(ship.DestinationHarbor);
        }

        private static bool HasKnownCurrentHarbor(ShipSnapshot ship)
        {
            return ship != null
                && !string.IsNullOrEmpty(ship.CurrentHarbor)
                && ship.CurrentHarbor != "None";
        }

        private object GetHarbor(string harborName)
        {
            if (getHarborMethod == null || string.IsNullOrEmpty(harborName) || harborName == "None")
            {
                return null;
            }

            try
            {
                return getHarborMethod.Invoke(staticSqLite, new object[] { harborName });
            }
            catch
            {
                return null;
            }
        }

        private float GetRemainingConditionOnArrival(object rawShip, ShipSnapshot ship, string destinationHarbor)
        {
            if (getRemainingConditionOnArrivalMethod != null && rawShip != null)
            {
                try
                {
                    return Convert.ToSingle(getRemainingConditionOnArrivalMethod.Invoke(
                        shipFactory,
                        new object[] { rawShip, ship.CurrentHarbor, destinationHarbor }));
                }
                catch
                {
                }
            }

            return ship.Condition;
        }

        private int GetShipSinkChance(float condition)
        {
            if (getShipSinkChanceMethod != null)
            {
                try
                {
                    return ToInt(getShipSinkChanceMethod.Invoke(shipFactory, new object[] { playerId, condition }));
                }
                catch
                {
                }
            }

            return condition <= 0f ? 100 : 0;
        }

        private void RefreshRepairTravelSafetyFloor()
        {
            if (playerId <= 0)
            {
                return;
            }

            float detectedDangerCondition = DetectSinkDangerCondition();
            float detectedSafetyFloor = Mathf.Clamp(detectedDangerCondition + RepairTravelSafetyMarginPercent, 0f, 100f);
            if (repairTravelSafetyPlayerId == playerId
                && Math.Abs(detectedDangerCondition - repairSinkDangerCondition) < 0.1f
                && Math.Abs(detectedSafetyFloor - repairTravelSafetyFloor) < 0.1f)
            {
                return;
            }

            repairTravelSafetyPlayerId = playerId;
            repairSinkDangerCondition = detectedDangerCondition;
            repairTravelSafetyFloor = detectedSafetyFloor;
            AddDecisionLog(string.Format(
                "Repair travel floor set to {0:0}% arrival condition; native sink risk starts near {1:0}%.",
                repairTravelSafetyFloor,
                repairSinkDangerCondition));
        }

        private float DetectSinkDangerCondition()
        {
            for (float condition = 100f; condition >= 0f; condition -= 0.5f)
            {
                if (GetShipSinkChance(condition) > 0)
                {
                    return condition;
                }
            }

            return 0f;
        }

        private static long EstimateRepairCost(float startCondition, float targetCondition, long pricePerPercent)
        {
            float delta = Mathf.Max(0f, targetCondition - startCondition);
            return ((long)delta) * pricePerPercent;
        }

        private int GetRepairDays(float conditionToRepair, int playerShipId)
        {
            if (getDaysForRepairMethod != null)
            {
                try
                {
                    return Math.Max(0, ToInt(getDaysForRepairMethod.Invoke(shipFactory, new object[] { conditionToRepair, playerShipId })));
                }
                catch
                {
                }
            }

            return 1 + (int)(conditionToRepair / 7f);
        }

        private object GetLatestRawShip(ShipSnapshot ship)
        {
            if (getPlayerShipMethod != null)
            {
                try
                {
                    object rawShip = getPlayerShipMethod.Invoke(dsqLite, new object[] { ship.PlayerShipId });
                    if (rawShip != null)
                    {
                        return rawShip;
                    }
                }
                catch
                {
                }
            }

            return ship.RawShip;
        }

        private void RefreshContrabandFreightCache()
        {
            contrabandFreightByName.Clear();

            if (getFreightAttributesMethod != null)
            {
                try
                {
                    object rawAttributes = getFreightAttributesMethod.Invoke(staticSqLite, null);
                    IDictionary dictionary = rawAttributes as IDictionary;
                    if (dictionary != null)
                    {
                        foreach (DictionaryEntry entry in dictionary)
                        {
                            string freight = entry.Key == null ? string.Empty : entry.Key.ToString();
                            object attributes = entry.Value;
                            if (string.IsNullOrEmpty(freight))
                            {
                                freight = ReadString(attributes, "Freight");
                            }

                            if (!string.IsNullOrEmpty(freight) && ReadBool(attributes, "Smugglersware"))
                            {
                                contrabandFreightByName[freight] = true;
                            }
                        }
                    }
                    else
                    {
                        IEnumerable attributes = rawAttributes as IEnumerable;
                        if (attributes != null)
                        {
                            foreach (object freightAttributes in attributes)
                            {
                                string freight = ReadString(freightAttributes, "Freight");
                                if (!string.IsNullOrEmpty(freight) && ReadBool(freightAttributes, "Smugglersware"))
                                {
                                    contrabandFreightByName[freight] = true;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    AddDecisionLog("Contraband freight lookup failed; using built-in fallback list: " + UnwrapMessage(ex));
                }
            }

            if (contrabandFreightByName.Count == 0)
            {
                SeedKnownContrabandFreights();
            }
        }

        private void SeedKnownContrabandFreights()
        {
            for (int i = 0; i < KnownContrabandFreights.Length; i++)
            {
                contrabandFreightByName[KnownContrabandFreights[i]] = true;
            }
        }

        private string GetCargoPolicyLabel()
        {
            return allowContrabandCargo ? "contraband allowed" : "legal cargo only";
        }

        private bool IsContrabandJob(object rawJob)
        {
            if (contrabandFreightByName.Count == 0)
            {
                SeedKnownContrabandFreights();
            }

            string freight = ReadString(rawJob, "Freight");
            return !string.IsNullOrEmpty(freight) && contrabandFreightByName.ContainsKey(freight);
        }

        private static bool HasWaypointUpgrade(ShipSnapshot ship)
        {
            return ship.Upgrade3 == 5 && (ship.Class == 3 || ship.Class == 4);
        }

        private static bool IsFreightUpgrade(int upgradeId)
        {
            return upgradeId >= 7 && upgradeId <= 15;
        }

        private static bool IsUpgradeCompatibleWithShip(ShipSnapshot ship, int upgradeId)
        {
            if (ship == null)
            {
                return false;
            }

            if (IsFreightUpgrade(upgradeId))
            {
                string freightType = GetUpgradeFreightType(upgradeId);
                return !string.IsNullOrEmpty(freightType)
                    && string.Equals(ship.FreightType, freightType, StringComparison.OrdinalIgnoreCase)
                    && ship.Upgrade4 != upgradeId
                    && ship.Upgrade5 != upgradeId;
            }

            if (upgradeId == 5)
            {
                return ship.Class == 3 || ship.Class == 4;
            }

            return upgradeId >= 1 && upgradeId <= 6;
        }

        private static int GetBaseUpgradeSlot(int upgradeId)
        {
            if (upgradeId == 1 || upgradeId == 2)
            {
                return 1;
            }

            if (upgradeId == 3 || upgradeId == 4)
            {
                return 2;
            }

            if (upgradeId == 5 || upgradeId == 6)
            {
                return 3;
            }

            if (IsFreightUpgrade(upgradeId))
            {
                return 4;
            }

            return 0;
        }

        private static int GetUpgradeSlotForShip(ShipSnapshot ship, int upgradeId)
        {
            int slot = GetBaseUpgradeSlot(upgradeId);
            if (slot == 0)
            {
                return 0;
            }

            if (slot == 4)
            {
                if (ship.Upgrade4 == 0)
                {
                    return 4;
                }

                if (ship.Upgrade5 == 0)
                {
                    return 5;
                }

                return 0;
            }

            return ship.GetUpgrade(slot) == 0 ? slot : 0;
        }

        private static long EstimateUpgradeCost(ShipSnapshot ship, int upgradeId)
        {
            long basePrice = GetUpgradeBasePrice(upgradeId);
            return basePrice <= 0L ? 0L : basePrice * Math.Max(1, ship.Class);
        }

        private static long GetUpgradeBasePrice(int upgradeId)
        {
            if (upgradeId == 1 || upgradeId == 2)
            {
                return 1500000L;
            }

            if (upgradeId == 3 || upgradeId == 4)
            {
                return 2000000L;
            }

            if (upgradeId == 5 || upgradeId == 6)
            {
                return 2500000L;
            }

            if (IsFreightUpgrade(upgradeId))
            {
                return 500000L;
            }

            return 0L;
        }

        private static string GetUpgradeFreightType(int upgradeId)
        {
            if (upgradeId >= 7 && upgradeId <= 9)
            {
                return "Container";
            }

            if (upgradeId >= 10 && upgradeId <= 12)
            {
                return "Bulk";
            }

            if (upgradeId >= 13 && upgradeId <= 15)
            {
                return "Tank";
            }

            return string.Empty;
        }

        private static string[] GetUpgradeFreights(int upgradeId)
        {
            switch (upgradeId)
            {
                case 7:
                    return new string[] { "Electronics", "LuxuryGoods", "Machinery" };
                case 8:
                    return new string[] { "Medicines", "Meat", "FruitVegetables" };
                case 9:
                    return new string[] { "RadioactiveMaterials", "AlcoholicBeverages", "Weapons" };
                case 10:
                    return new string[] { "Crop", "Sand", "Cement" };
                case 11:
                    return new string[] { "Coffee", "Timber", "Fertilizer" };
                case 12:
                    return new string[] { "Bauxite", "Phosphate", "Salts" };
                case 13:
                    return new string[] { "VegetableOil", "AnimalOil" };
                case 14:
                    return new string[] { "LiquidGas", "FuelOil" };
                case 15:
                    return new string[] { "Kerosine", "LiquidChemicals" };
                default:
                    return new string[0];
            }
        }

        private static string GetUpgradeName(int upgradeId)
        {
            switch (upgradeId)
            {
                case 1:
                    return "speed upgrade";
                case 2:
                    return "repair-time upgrade";
                case 3:
                    return "tug-fee upgrade";
                case 4:
                    return "fuel-consumption upgrade";
                case 5:
                    return "waypoint upgrade";
                case 6:
                    return "range upgrade";
                default:
                    return GetUpgradeFreightType(upgradeId) + " cargo revenue upgrade";
            }
        }

        private bool IsUpgradeAvailableAtHarbor(ShipSnapshot ship, string harborName, int upgradeId)
        {
            if (!IsFreightUpgrade(upgradeId))
            {
                return true;
            }

            bool harborDataKnown = false;
            bool supportsFreight = HarborSupportsUpgradeFreight(harborName, ship.FreightType, GetUpgradeFreights(upgradeId), out harborDataKnown);
            return !harborDataKnown || supportsFreight;
        }

        private bool HarborSupportsUpgradeFreight(string harborName, string freightType, string[] upgradeFreights, out bool harborDataKnown)
        {
            harborDataKnown = false;
            if (getUpgradeExportFromHarborMethod == null || upgradeFreights == null || upgradeFreights.Length == 0)
            {
                return true;
            }

            object rawExports = null;
            try
            {
                rawExports = getUpgradeExportFromHarborMethod.Invoke(staticSqLite, new object[] { harborName });
            }
            catch
            {
                return true;
            }

            IDictionary dictionary = rawExports as IDictionary;
            if (dictionary == null)
            {
                return true;
            }

            object rawFreights = null;
            foreach (DictionaryEntry entry in dictionary)
            {
                string key = entry.Key == null ? string.Empty : entry.Key.ToString();
                if (string.Equals(key, freightType, StringComparison.OrdinalIgnoreCase))
                {
                    rawFreights = entry.Value;
                    break;
                }
            }

            IEnumerable exportedFreights = rawFreights as IEnumerable;
            if (exportedFreights == null)
            {
                return true;
            }

            harborDataKnown = true;
            foreach (object rawFreight in exportedFreights)
            {
                string freight = rawFreight == null ? string.Empty : rawFreight.ToString();
                if (ContainsIgnoreCase(upgradeFreights, freight))
                {
                    return true;
                }
            }

            return false;
        }

        private double ScoreUpgradeCandidate(ShipSnapshot ship, string harborName, int upgradeId, long estimatedCost)
        {
            double score = GetBaseUpgradeUtility(upgradeId) - (estimatedCost * 0.02);
            if (IsFreightUpgrade(upgradeId))
            {
                score += EstimateFreightUpgradeOpportunity(ship, harborName, upgradeId);
                score += GetFreightUpgradeDiversityScore(ship, upgradeId);
            }

            return score;
        }

        private double GetFreightUpgradeDiversityScore(ShipSnapshot ship, int upgradeId)
        {
            if (ship == null || !IsFreightUpgrade(upgradeId))
            {
                return 0.0;
            }

            int[] group = GetFreightUpgradeIdsForType(ship.FreightType);
            if (group.Length == 0)
            {
                return 0.0;
            }

            int candidateCount = CountSameTypeFreightUpgradeUse(ship, upgradeId);
            int minimumCount = int.MaxValue;
            for (int i = 0; i < group.Length; i++)
            {
                int id = group[i];
                if (!IsUpgradeCompatibleWithShip(ship, id))
                {
                    continue;
                }

                minimumCount = Math.Min(minimumCount, CountSameTypeFreightUpgradeUse(ship, id));
            }

            if (minimumCount == int.MaxValue)
            {
                return 0.0;
            }

            double score = (minimumCount - candidateCount) * 6000000.0;
            int preferredId = GetPreferredFreightUpgradeId(ship, group);
            if (upgradeId == preferredId)
            {
                score += 2000000.0;
            }

            return score;
        }

        private int CountSameTypeFreightUpgradeUse(ShipSnapshot focusShip, int upgradeId)
        {
            int count = 0;
            for (int i = 0; i < ships.Count; i++)
            {
                ShipSnapshot fleetShip = ships[i];
                if (fleetShip == null
                    || fleetShip.PlayerShipId == focusShip.PlayerShipId
                    || !IsCaptainEnabled(fleetShip.PlayerShipId)
                    || !string.Equals(fleetShip.FreightType, focusShip.FreightType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                bool counted = fleetShip.Upgrade4 == upgradeId || fleetShip.Upgrade5 == upgradeId;
                if (counted)
                {
                    count++;
                }

                int plannedUpgradeId;
                if (!counted
                    && plannedFreightUpgradeByShipId.TryGetValue(fleetShip.PlayerShipId, out plannedUpgradeId)
                    && plannedUpgradeId == upgradeId)
                {
                    count++;
                }
            }

            return count;
        }

        private int GetPreferredFreightUpgradeId(ShipSnapshot ship, int[] group)
        {
            if (ship == null || group == null || group.Length == 0)
            {
                return 0;
            }

            int start = PositiveModulo(ship.PlayerShipId, group.Length);
            for (int i = 0; i < group.Length; i++)
            {
                int id = group[(start + i) % group.Length];
                if (IsUpgradeCompatibleWithShip(ship, id))
                {
                    return id;
                }
            }

            return 0;
        }

        private static int PositiveModulo(int value, int divisor)
        {
            if (divisor <= 0)
            {
                return 0;
            }

            int result = value % divisor;
            return result < 0 ? result + divisor : result;
        }

        private static int[] GetFreightUpgradeIdsForType(string freightType)
        {
            if (string.Equals(freightType, "Container", StringComparison.OrdinalIgnoreCase))
            {
                return new int[] { 7, 8, 9 };
            }

            if (string.Equals(freightType, "Bulk", StringComparison.OrdinalIgnoreCase))
            {
                return new int[] { 10, 11, 12 };
            }

            if (string.Equals(freightType, "Tank", StringComparison.OrdinalIgnoreCase))
            {
                return new int[] { 13, 14, 15 };
            }

            return new int[0];
        }

        private static double GetBaseUpgradeUtility(int upgradeId)
        {
            switch (upgradeId)
            {
                case 4:
                    return 4500000.0;
                case 1:
                    return 4000000.0;
                case 5:
                    return 3500000.0;
                case 2:
                    return 2500000.0;
                case 3:
                    return 1800000.0;
                case 6:
                    return 1500000.0;
                default:
                    return IsFreightUpgrade(upgradeId) ? 3000000.0 : 0.0;
            }
        }

        private double EstimateFreightUpgradeOpportunity(ShipSnapshot ship, string harborName, int upgradeId)
        {
            string[] freights = GetUpgradeFreights(upgradeId);
            if (freights.Length == 0)
            {
                return 0.0;
            }

            int contrabandSkipped = 0;
            int candidateCount = 0;
            Dictionary<string, List<JobCandidate>> candidatesByDestination = CollectJobCandidatesFromHarbor(ship, harborName, out contrabandSkipped, out candidateCount);
            if (candidatesByDestination == null)
            {
                return 0.0;
            }

            double opportunity = 0.0;
            foreach (KeyValuePair<string, List<JobCandidate>> pair in candidatesByDestination)
            {
                List<JobCandidate> candidates = pair.Value;
                for (int i = 0; i < candidates.Count; i++)
                {
                    JobCandidate candidate = candidates[i];
                    if (ContainsIgnoreCase(freights, candidate.Freight))
                    {
                        opportunity += candidate.Score * 0.5;
                    }
                }
            }

            return opportunity;
        }

        private int GetRegionOfHarbor(string harborName)
        {
            if (getRegionOfHarborMethod == null || string.IsNullOrEmpty(harborName))
            {
                return 0;
            }

            try
            {
                return ToInt(getRegionOfHarborMethod.Invoke(staticSqLite, new object[] { harborName }));
            }
            catch
            {
                return 0;
            }
        }

        private int GetUpgradeDockOwner(int region)
        {
            if (getUpgradeDockOwnerMethod == null || region <= 0)
            {
                return 0;
            }

            try
            {
                return ToInt(getUpgradeDockOwnerMethod.Invoke(shipFactory, new object[] { region }));
            }
            catch
            {
                return 0;
            }
        }

        private long EstimateUpgradeDockOwnerShares(int region, long upgradeCost)
        {
            if (getBusinessSharesFromRegionMethod == null || region <= 0 || upgradeCost <= 0L)
            {
                return 0L;
            }

            try
            {
                object shares = getBusinessSharesFromRegionMethod.Invoke(staticSqLite, new object[] { region });
                float percent = ReadFloat(shares, "UpgradeDockPercent");
                return percent <= 0f ? 0L : (long)(upgradeCost * percent);
            }
            catch
            {
                return 0L;
            }
        }

        private JobPlan FindBestJobPlan(ShipSnapshot ship)
        {
            return FindBestJobPlanFromHarbor(ship, ship.CurrentHarbor, true);
        }

        private JobPlan FindBestJobPlanFromHarbor(ShipSnapshot ship, string startHarbor, bool includeRouteLookahead)
        {
            Dictionary<string, HarborJobPlanSet> routeCache = includeRouteLookahead
                ? new Dictionary<string, HarborJobPlanSet>(StringComparer.OrdinalIgnoreCase)
                : null;
            HarborJobPlanSet initialPlans = GetRankedJobPlansFromHarbor(ship, startHarbor, routeCache);
            if (initialPlans == null || initialPlans.Plans.Count == 0)
            {
                return null;
            }

            JobPlan best = null;
            if (includeRouteLookahead)
            {
                HashSet<string> visitedHarbors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(startHarbor))
                {
                    visitedHarbors.Add(startHarbor);
                }

                RoutePlan route = BuildBestRouteFromHarbor(
                    ship,
                    startHarbor,
                    RouteLookaheadDepth,
                    1.0,
                    visitedHarbors,
                    routeCache);
                if (route != null && route.FirstLeg != null)
                {
                    best = route.FirstLeg;
                    ApplyRouteProjection(best, route);
                }
            }

            if (best == null)
            {
                best = initialPlans.Plans[0];
            }

            if (best != null)
            {
                best.ContrabandSkipped = initialPlans.ContrabandSkipped;
                best.CandidateCount = initialPlans.CandidateCount;
            }

            return best;
        }

        private JobPlan FindJobPlanToDestination(ShipSnapshot ship, string startHarbor, string destination)
        {
            int contrabandSkipped = 0;
            int candidateCount = 0;
            Dictionary<string, List<JobCandidate>> candidatesByDestination = CollectJobCandidatesFromHarbor(ship, startHarbor, out contrabandSkipped, out candidateCount);
            if (candidatesByDestination == null)
            {
                return null;
            }

            List<JobCandidate> candidates;
            if (!candidatesByDestination.TryGetValue(destination, out candidates))
            {
                return null;
            }

            JobPlan plan = BuildJobPlanForDestination(ship, startHarbor, destination, candidates);
            if (plan != null)
            {
                plan.ContrabandSkipped = contrabandSkipped;
                plan.CandidateCount = candidateCount;
            }

            return plan;
        }

        private HarborJobPlanSet GetRankedJobPlansFromHarbor(ShipSnapshot ship, string startHarbor, Dictionary<string, HarborJobPlanSet> routeCache)
        {
            if (string.IsNullOrEmpty(startHarbor) || startHarbor == "None")
            {
                return null;
            }

            HarborJobPlanSet cached;
            if (routeCache != null && routeCache.TryGetValue(startHarbor, out cached))
            {
                return cached;
            }

            int contrabandSkipped = 0;
            int candidateCount = 0;
            Dictionary<string, List<JobCandidate>> candidatesByDestination = CollectJobCandidatesFromHarbor(ship, startHarbor, out contrabandSkipped, out candidateCount);
            if (candidatesByDestination == null)
            {
                return null;
            }

            HarborJobPlanSet plans = new HarborJobPlanSet();
            plans.StartHarbor = startHarbor;
            plans.ContrabandSkipped = contrabandSkipped;
            plans.CandidateCount = candidateCount;

            foreach (KeyValuePair<string, List<JobCandidate>> pair in candidatesByDestination)
            {
                JobPlan plan = BuildJobPlanForDestination(ship, startHarbor, pair.Key, pair.Value);
                if (plan != null)
                {
                    plans.Plans.Add(plan);
                }
            }

            plans.Plans.Sort(delegate(JobPlan left, JobPlan right)
            {
                return right.Score.CompareTo(left.Score);
            });

            if (routeCache != null)
            {
                routeCache[startHarbor] = plans;
            }

            return plans;
        }

        private RoutePlan BuildBestRouteFromHarbor(
            ShipSnapshot ship,
            string startHarbor,
            int depthRemaining,
            double scoreMultiplier,
            HashSet<string> visitedHarbors,
            Dictionary<string, HarborJobPlanSet> routeCache)
        {
            if (depthRemaining <= 0)
            {
                return null;
            }

            HarborJobPlanSet availablePlans = GetRankedJobPlansFromHarbor(ship, startHarbor, routeCache);
            if (availablePlans == null || availablePlans.Plans.Count == 0)
            {
                return null;
            }

            RoutePlan best = null;
            int planLimit = Math.Min(RouteLookaheadBeamWidth, availablePlans.Plans.Count);
            for (int i = 0; i < planLimit; i++)
            {
                JobPlan leg = availablePlans.Plans[i];
                if (leg == null || string.IsNullOrEmpty(leg.End))
                {
                    continue;
                }

                bool revisitsHarbor = visitedHarbors != null && visitedHarbors.Contains(leg.End);
                if (revisitsHarbor && depthRemaining > 1)
                {
                    continue;
                }

                RoutePlan route = new RoutePlan();
                double weightedScore = leg.Score * scoreMultiplier;
                if (Math.Abs(scoreMultiplier - 1.0) < 0.001)
                {
                    weightedScore -= GetFleetDestinationCrowdingPenalty(ship, leg.End);
                }

                route.AddLeg(leg, weightedScore);

                if (depthRemaining > 1 && !revisitsHarbor)
                {
                    if (visitedHarbors != null)
                    {
                        visitedHarbors.Add(leg.End);
                    }

                    RoutePlan future = BuildBestRouteFromHarbor(
                        ship,
                        leg.End,
                        depthRemaining - 1,
                        scoreMultiplier * RouteFutureLegDiscount,
                        visitedHarbors,
                        routeCache);

                    if (visitedHarbors != null)
                    {
                        visitedHarbors.Remove(leg.End);
                    }

                    if (future != null)
                    {
                        route.Append(future);
                    }
                }

                if (best == null
                    || route.TotalScore > best.TotalScore
                    || (Math.Abs(route.TotalScore - best.TotalScore) < 0.1 && route.TotalPayment > best.TotalPayment))
                {
                    best = route;
                }
            }

            return best;
        }

        private void ApplyRouteProjection(JobPlan firstLeg, RoutePlan route)
        {
            if (firstLeg == null || route == null)
            {
                return;
            }

            firstLeg.ChainScore = route.TotalScore - firstLeg.Score;
            firstLeg.ChainPayment = route.FuturePayment;
            firstLeg.ChainJobs = route.FutureJobs;
            firstLeg.ChainEnd = route.GetFutureSummary();
            firstLeg.ChainDistanceInDays = route.FutureDistanceInDays;
            firstLeg.RouteLegs = route.Legs.Count;
        }

        private double GetFleetDestinationCrowdingPenalty(ShipSnapshot ship, string destination)
        {
            if (ship == null || string.IsNullOrEmpty(destination))
            {
                return 0.0;
            }

            int sameFreightShips = 0;
            int otherShips = 0;
            for (int i = 0; i < ships.Count; i++)
            {
                ShipSnapshot fleetShip = ships[i];
                if (fleetShip == null
                    || fleetShip.PlayerShipId == ship.PlayerShipId
                    || !IsCaptainEnabled(fleetShip.PlayerShipId))
                {
                    continue;
                }

                bool isTargetingDestination =
                    string.Equals(fleetShip.DestinationHarbor, destination, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(fleetShip.CurrentHarbor, destination, StringComparison.OrdinalIgnoreCase);
                bool isIdleAtDestination =
                    string.Equals(fleetShip.CurrentHarbor, destination, StringComparison.OrdinalIgnoreCase)
                    && IsShipIdleInHarbor(fleetShip);

                if (!isTargetingDestination && !isIdleAtDestination)
                {
                    continue;
                }

                if (string.Equals(fleetShip.FreightType, ship.FreightType, StringComparison.OrdinalIgnoreCase))
                {
                    sameFreightShips++;
                }
                else
                {
                    otherShips++;
                }
            }

            return (sameFreightShips * RouteSameFreightDestinationPenalty)
                + (otherShips * RouteOtherFleetDestinationPenalty);
        }

        private Dictionary<string, List<JobCandidate>> CollectJobCandidatesFromHarbor(ShipSnapshot ship, string startHarbor, out int contrabandSkipped, out int candidateCount)
        {
            contrabandSkipped = 0;
            candidateCount = 0;

            if (getJobsFromStartHarborMethod == null || string.IsNullOrEmpty(startHarbor) || startHarbor == "None")
            {
                return null;
            }

            object rawJobs = null;
            try
            {
                rawJobs = getJobsFromStartHarborMethod.Invoke(
                    dsqLite,
                    new object[] { startHarbor, ship.FreightType, ship.Class });
            }
            catch (Exception ex)
            {
                AddDecisionLog("Job lookup failed for " + ship.Name + " at " + startHarbor + ": " + UnwrapMessage(ex));
                return null;
            }

            IEnumerable jobs = rawJobs as IEnumerable;
            if (jobs == null)
            {
                return null;
            }

            Dictionary<string, List<JobCandidate>> candidatesByDestination = new Dictionary<string, List<JobCandidate>>(StringComparer.OrdinalIgnoreCase);
            foreach (object rawJob in jobs)
            {
                bool isContraband = IsContrabandJob(rawJob);
                if (isContraband && !allowContrabandCargo)
                {
                    contrabandSkipped++;
                    continue;
                }

                JobCandidate candidate = JobCandidate.From(rawJob, ship, isContraband);
                if (candidate == null)
                {
                    continue;
                }

                candidateCount++;
                List<JobCandidate> destinationCandidates;
                if (!candidatesByDestination.TryGetValue(candidate.End, out destinationCandidates))
                {
                    destinationCandidates = new List<JobCandidate>();
                    candidatesByDestination[candidate.End] = destinationCandidates;
                }

                destinationCandidates.Add(candidate);
            }

            return candidatesByDestination;
        }

        private static JobPlan BuildJobPlanForDestination(ShipSnapshot ship, string startHarbor, string destination, List<JobCandidate> candidates)
        {
            return BuildJobPlanForDestination(ship, startHarbor, destination, candidates, 0, 0);
        }

        private static JobPlan BuildJobPlanForDestination(ShipSnapshot ship, string startHarbor, string destination, List<JobCandidate> candidates, int reservedVolume, int reservedWeight)
        {
            if (candidates == null || candidates.Count == 0 || string.IsNullOrEmpty(destination))
            {
                return null;
            }

            List<JobCandidate> rankedCandidates = new List<JobCandidate>(candidates);
            rankedCandidates.Sort(delegate(JobCandidate left, JobCandidate right)
            {
                int priority = GetPackingPriority(ship, right).CompareTo(GetPackingPriority(ship, left));
                return priority != 0 ? priority : right.Score.CompareTo(left.Score);
            });

            if (rankedCandidates.Count > CargoPackingSearchLimit)
            {
                rankedCandidates.RemoveRange(CargoPackingSearchLimit, rankedCandidates.Count - CargoPackingSearchLimit);
            }

            double[] remainingScore = BuildRemainingScore(rankedCandidates);
            JobPlan current = new JobPlan();
            current.Start = startHarbor;
            current.End = destination;
            current.ReservedVolume = Math.Max(0, reservedVolume);
            current.ReservedWeight = Math.Max(0, reservedWeight);
            JobPlan best = null;
            SearchPackedJobPlan(ship, rankedCandidates, remainingScore, 0, current, ref best);
            return best;
        }

        private static double GetPackingPriority(ShipSnapshot ship, JobCandidate candidate)
        {
            if (ship == null || candidate == null)
            {
                return 0.0;
            }

            double capacityShare = 0.0;
            if (ship.Volume > 0)
            {
                capacityShare = Math.Max(capacityShare, (double)candidate.Volume / (double)ship.Volume);
            }

            if (ship.DeadweightTons > 0)
            {
                capacityShare = Math.Max(capacityShare, (double)candidate.Weight / (double)ship.DeadweightTons);
            }

            return candidate.Score / Math.Max(0.1, capacityShare);
        }

        private static double[] BuildRemainingScore(List<JobCandidate> candidates)
        {
            double[] remainingScore = new double[candidates.Count + 1];
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                remainingScore[i] = remainingScore[i + 1] + Math.Max(0.0, candidates[i].Score);
            }

            return remainingScore;
        }

        private static void SearchPackedJobPlan(
            ShipSnapshot ship,
            List<JobCandidate> candidates,
            double[] remainingScore,
            int index,
            JobPlan current,
            ref JobPlan best)
        {
            if (best != null && current.Score + remainingScore[index] <= best.Score + 0.1)
            {
                return;
            }

            if (index >= candidates.Count)
            {
                if (current.Jobs.Count > 0
                    && (best == null
                        || current.Score > best.Score
                        || (Math.Abs(current.Score - best.Score) < 0.1 && current.Payment > best.Payment)))
                {
                    best = current.Clone();
                }

                return;
            }

            JobCandidate candidate = candidates[index];
            if (CanAddCandidateToPlan(ship, current, candidate))
            {
                current.Add(candidate);
                SearchPackedJobPlan(ship, candidates, remainingScore, index + 1, current, ref best);
                current.RemoveLast();
            }

            SearchPackedJobPlan(ship, candidates, remainingScore, index + 1, current, ref best);
        }

        private static bool CanAddCandidateToPlan(ShipSnapshot ship, JobPlan plan, JobCandidate candidate)
        {
            if (ship == null || plan == null || candidate == null)
            {
                return false;
            }

            int reservedVolume = plan == null ? 0 : Math.Max(0, plan.ReservedVolume);
            int reservedWeight = plan == null ? 0 : Math.Max(0, plan.ReservedWeight);

            if (ship.Volume > 0 && reservedVolume + plan.Volume + candidate.Volume > ship.Volume)
            {
                return false;
            }

            if (ship.DeadweightTons > 0 && reservedWeight + plan.Weight + candidate.Weight > ship.DeadweightTons)
            {
                return false;
            }

            return true;
        }

        private bool DispatchJobPlan(ShipSnapshot ship, JobPlan plan, string reason)
        {
            if (plan == null || plan.Jobs.Count == 0)
            {
                return false;
            }

            if (updateJobPlayerShipMethod == null || setShipDontLoadJobsMethod == null || setShipGlobalDestinationMethod == null)
            {
                AddDecisionLog(string.Format("#{0} {1}: direct legal dispatch bridge is unavailable.", ship.PlayerShipId, ship.Name));
                return false;
            }

            try
            {
                SetNativeAiState(ship, false);
                for (int i = 0; i < plan.Jobs.Count; i++)
                {
                    updateJobPlayerShipMethod.Invoke(dsqLite, new object[] { plan.Jobs[i].JobId, ship.PlayerShipId });
                    WriteField(plan.Jobs[i].RawJob, "PlayerShipID", ship.PlayerShipId);
                }

                SetNativeCargoGuard(ship, true, plan.End);
                SetNativeDestinationHarbor(ship, plan.End);
                if (setShipSmugglersWareMethod != null)
                {
                    setShipSmugglersWareMethod.Invoke(dsqLite, new object[] { ship.PlayerShipId, plan.HasContraband });
                    WriteField(ship.RawShip, "HasSmugglerswareLoaded", plan.HasContraband);
                }

                bool sent = false;
                if (sendShipToDestinationMethod != null)
                {
                    try
                    {
                        sendShipToDestinationMethod.Invoke(shipFactory, new object[] { ship.PlayerShipId, plan.End });
                        sent = true;
                    }
                    catch (Exception moveEx)
                    {
                        AddDecisionLog(string.Format(
                            "#{0} {1}: direct movement call failed; trying native cast-out event: {2}",
                            ship.PlayerShipId,
                            ship.Name,
                            UnwrapMessage(moveEx)));
                    }
                }
                if (!sent)
                {
                    sent = SendCargoEvent("SHIP_CAST_IN_DONE", ship.PlayerShipId);
                }

                if (sent)
                {
                    ship.DestinationHarbor = plan.End;
                    if (!IsUpgradeDockRoutingReason(reason))
                    {
                        ClearPlannedFreightUpgrade(ship);
                    }

                    MarkShipDispatched(ship);
                    AddDecisionLog(string.Format(
                        "#{0} {1}: loaded {2} {3}job(s) for {4} and dispatched from {5}.",
                        ship.PlayerShipId,
                        ship.Name,
                        plan.Jobs.Count,
                        plan.HasContraband ? string.Empty : "legal ",
                        plan.End,
                        reason));
                    return true;
                }

                AddDecisionLog(string.Format("#{0} {1}: loaded jobs but could not trigger cast-out.", ship.PlayerShipId, ship.Name));
                return false;
            }
            catch (Exception ex)
            {
                AddDecisionLog(string.Format("#{0} {1}: direct dispatch failed: {2}", ship.PlayerShipId, ship.Name, UnwrapMessage(ex)));
                return false;
            }
        }

        private bool IsShipIdleInHarbor(ShipSnapshot ship)
        {
            if (IsRepairPending(ship) || IsUpgradePending(ship))
            {
                return false;
            }

            if (!HasKnownCurrentHarbor(ship))
            {
                return false;
            }

            if (HasActiveDestination(ship))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(ship.Status))
            {
                string status = ship.Status.ToLowerInvariant();
                if (status.Contains("repair") || status.Contains("upgrade") || status.Contains("travel") || status == "minigame_castout")
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasActiveDestination(ShipSnapshot ship)
        {
            return ship != null
                && !IsNoneOrEmpty(ship.DestinationHarbor)
                && ship.DestinationHarbor != ship.CurrentHarbor;
        }

        private bool IsDispatchSettling(ShipSnapshot ship)
        {
            if (ship == null)
            {
                return false;
            }

            float lastDispatch;
            if (!lastDispatchRealtimeByShipId.TryGetValue(ship.PlayerShipId, out lastDispatch))
            {
                return false;
            }

            if (IsShipIdleInHarbor(ship))
            {
                lastDispatchRealtimeByShipId.Remove(ship.PlayerShipId);
                return false;
            }

            if (Time.realtimeSinceStartup - lastDispatch <= GetDispatchSettleDelaySeconds())
            {
                return true;
            }

            lastDispatchRealtimeByShipId.Remove(ship.PlayerShipId);
            return false;
        }

        private void MarkShipDispatched(ShipSnapshot ship)
        {
            if (ship != null)
            {
                lastDispatchRealtimeByShipId[ship.PlayerShipId] = Time.realtimeSinceStartup;
                ClearIdleTracking(ship);
            }
        }

        private float GetDispatchSettleDelaySeconds()
        {
            float speed = Mathf.Max(1f, GetCurrentInGameSpeed());
            return Mathf.Clamp(DispatchSettleSeconds / speed, DispatchSettleMinRealtimeSeconds, DispatchSettleSeconds);
        }

        private void SetPlannedFreightUpgrade(ShipSnapshot ship, int upgradeId)
        {
            if (ship == null)
            {
                return;
            }

            if (IsFreightUpgrade(upgradeId))
            {
                plannedFreightUpgradeByShipId[ship.PlayerShipId] = upgradeId;
            }
            else
            {
                plannedFreightUpgradeByShipId.Remove(ship.PlayerShipId);
            }
        }

        private void ClearPlannedFreightUpgrade(ShipSnapshot ship)
        {
            if (ship != null)
            {
                plannedFreightUpgradeByShipId.Remove(ship.PlayerShipId);
            }
        }

        private static bool IsUpgradeDockRoutingReason(string reason)
        {
            return !string.IsNullOrEmpty(reason)
                && reason.IndexOf("upgrade dock routing", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ClearIdleTracking(ShipSnapshot ship)
        {
            if (ship == null)
            {
                return;
            }

            idleSinceGameDateByShipId.Remove(ship.PlayerShipId);
            idleHarborByShipId.Remove(ship.PlayerShipId);
        }

        private static bool IsNoneOrEmpty(string value)
        {
            return string.IsNullOrEmpty(value) || value == "None";
        }

        private static string DescribeShipLocationState(ShipSnapshot ship)
        {
            return string.Format(
                "harbor={0}, dest={1}, status={2}, condition={3:0}%",
                string.IsNullOrEmpty(ship.CurrentHarbor) ? "-" : ship.CurrentHarbor,
                string.IsNullOrEmpty(ship.DestinationHarbor) ? "-" : ship.DestinationHarbor,
                string.IsNullOrEmpty(ship.Status) ? "-" : ship.Status,
                ship.Condition);
        }

        private bool IsRepairPending(ShipSnapshot ship)
        {
            if (ship == null || ship.Repaired)
            {
                return false;
            }

            if (ship.RepairFinish <= DateTime.MinValue.AddDays(1.0))
            {
                return false;
            }

            return ship.RepairFinish > GetCurrentGameDateTime();
        }

        private bool IsUpgradePending(ShipSnapshot ship)
        {
            if (ship == null || ship.Upgraded)
            {
                return false;
            }

            if (ship.UpgradeFinish <= DateTime.MinValue.AddDays(1.0))
            {
                return false;
            }

            return ship.UpgradeFinish > GetCurrentGameDateTime();
        }

        private DateTime GetCurrentGameDateTime()
        {
            if (getCurrentDateTimeMethod != null)
            {
                try
                {
                    object value = getCurrentDateTimeMethod.Invoke(null, null);
                    if (value is DateTime)
                    {
                        return (DateTime)value;
                    }
                }
                catch
                {
                }
            }

            return DateTime.Now;
        }

        private float GetCurrentInGameSpeed()
        {
            if (getCurrentInGameSpeedMethod != null)
            {
                try
                {
                    float speed = Convert.ToSingle(getCurrentInGameSpeedMethod.Invoke(null, null));
                    if (speed > 0f)
                    {
                        return speed;
                    }
                }
                catch
                {
                }
            }

            return Mathf.Max(1f, Time.timeScale);
        }

        private float GetAutomationTickDelaySeconds()
        {
            float speed = Mathf.Max(1f, GetCurrentInGameSpeed());
            return Mathf.Clamp(AutomationTickBaseRealtimeSeconds / speed, AutomationTickMinRealtimeSeconds, AutomationTickBaseRealtimeSeconds);
        }

        private string GetDisplayStatus(ShipSnapshot ship)
        {
            if (IsRepairPending(ship))
            {
                return "Repairing";
            }

            if (IsHeadingToRepairDock(ship))
            {
                return "To repair";
            }

            if (IsUpgradePending(ship))
            {
                return "Upgrading";
            }

            if (IsHeadingToUpgradeDock(ship))
            {
                return "To upgrade";
            }

            return ship.Status;
        }

        private void SetAllVisibleShips(bool enabled)
        {
            for (int i = 0; i < ships.Count; i++)
            {
                SetCaptainEnabled(ships[i].PlayerShipId, enabled);
            }

            AddDecisionLog((enabled ? "Enabled" : "Disabled") + " TO2 Captain mode for all visible ships.");
        }

        // Enrolls a newly-seen ship without triggering an immediate evaluation mid-refresh; the next
        // automation tick (or F8/Plan) picks it up like any other enabled ship.
        private void AutoEnrollNewShip(ShipSnapshot ship)
        {
            captainEnabledByShipId[ship.PlayerShipId] = true;
            PlayerPrefs.SetInt(CaptainPrefsPrefix + ship.PlayerShipId, 1);
            PlayerPrefs.Save();
            AddDecisionLog(string.Format(
                "#{0} {1}: new ship auto-enrolled into TO2 Captain mode (auto-pilot all is on).",
                ship.PlayerShipId,
                ship.Name));
        }

        private void SyncNativeAiStateForEnabledShips(bool nativeAiEnabled)
        {
            for (int i = 0; i < ships.Count; i++)
            {
                if (IsCaptainEnabled(ships[i].PlayerShipId))
                {
                    if (!nativeAiEnabled)
                    {
                        SetNativeAiState(ships[i], false);
                        ClearNativeCargoGuard(ships[i]);
                    }
                    else if (ships[i].Condition < minimumSailCondition)
                    {
                        string prefix = string.Format("#{0} {1}: ", ships[i].PlayerShipId, ships[i].Name);
                        long reserve = Math.Max(500000L, Math.Abs(playerCredits) / 5L);
                        HandleLowConditionShip(ships[i], prefix, reserve);
                    }
                    else
                    {
                        EvaluateShip(ships[i], "live actions toggled on");
                    }
                }
            }
        }

        private void SetMinimumSailCondition(float value)
        {
            minimumSailCondition = Mathf.Round(Mathf.Clamp(value, MinSailConditionFloor, MinSailConditionCeiling));
            PlayerPrefs.SetFloat("TO2FA.MinimumSailCondition", minimumSailCondition);
            if (repairTargetCondition < minimumSailCondition)
            {
                repairTargetCondition = minimumSailCondition;
                PlayerPrefs.SetFloat("TO2FA.RepairTargetCondition", repairTargetCondition);
            }
            PlayerPrefs.Save();
            AddDecisionLog(string.Format("Minimum condition to sail set to {0:0}%.", minimumSailCondition));
        }

        private void SetRepairTargetCondition(float value)
        {
            repairTargetCondition = Mathf.Clamp(value, minimumSailCondition, 100f);
            PlayerPrefs.SetFloat("TO2FA.RepairTargetCondition", repairTargetCondition);
            PlayerPrefs.Save();
            AddDecisionLog(string.Format("Repair target condition set to {0:0}%.", repairTargetCondition));
        }

        private ShipSnapshot FindShipSnapshot(int playerShipId)
        {
            for (int i = 0; i < ships.Count; i++)
            {
                if (ships[i].PlayerShipId == playerShipId)
                {
                    return ships[i];
                }
            }

            return null;
        }

        private bool SetNativeAiState(ShipSnapshot ship, bool enabled)
        {
            if (updatePlayerShipAiStateMethod == null)
            {
                AddDecisionLog(string.Format("#{0} {1}: native AI state setter is unavailable.", ship.PlayerShipId, ship.Name));
                return false;
            }

            try
            {
                updatePlayerShipAiStateMethod.Invoke(dsqLite, new object[] { ship.PlayerShipId, enabled });
                ship.IsAI = enabled;
                WriteField(ship.RawShip, "IsAI", enabled);
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog(string.Format("#{0} {1}: failed to set native AI state: {2}", ship.PlayerShipId, ship.Name, UnwrapMessage(ex)));
                return false;
            }
        }

        private bool SetNativeDestinationHarbor(ShipSnapshot ship, string destination)
        {
            if (updatePlayerShipDestinationHarborMethod == null)
            {
                return false;
            }

            try
            {
                updatePlayerShipDestinationHarborMethod.Invoke(dsqLite, new object[] { ship.PlayerShipId, destination });
                WriteField(ship.RawShip, "DestinationHarbor", destination);
                ship.DestinationHarbor = destination;
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog(string.Format("#{0} {1}: failed to set native destination harbor: {2}", ship.PlayerShipId, ship.Name, UnwrapMessage(ex)));
                return false;
            }
        }

        private void ClearNativeCargoGuard(ShipSnapshot ship)
        {
            SetNativeCargoGuard(ship, false, "None");
        }

        private bool SetNativeCargoGuard(ShipSnapshot ship, bool dontLoadJobs, string globalDestination)
        {
            bool ok = true;
            if (setShipDontLoadJobsMethod != null)
            {
                try
                {
                    setShipDontLoadJobsMethod.Invoke(dsqLite, new object[] { ship.PlayerShipId, dontLoadJobs });
                    WriteField(ship.RawShip, "DontLoadJobs", dontLoadJobs);
                }
                catch (Exception ex)
                {
                    AddDecisionLog(string.Format("#{0} {1}: failed to set native job-loading guard: {2}", ship.PlayerShipId, ship.Name, UnwrapMessage(ex)));
                    ok = false;
                }
            }
            else
            {
                ok = false;
            }

            if (setShipGlobalDestinationMethod != null && globalDestination != null)
            {
                try
                {
                    setShipGlobalDestinationMethod.Invoke(dsqLite, new object[] { ship.PlayerShipId, globalDestination });
                    WriteField(ship.RawShip, "GlobalDestination", globalDestination);
                }
                catch (Exception ex)
                {
                    AddDecisionLog(string.Format("#{0} {1}: failed to set native global destination: {2}", ship.PlayerShipId, ship.Name, UnwrapMessage(ex)));
                    ok = false;
                }
            }
            else if (globalDestination != null)
            {
                ok = false;
            }

            return ok;
        }

        private bool TriggerNativeAiCastOut(ShipSnapshot ship, string reason)
        {
            if (SendCargoEvent("SHIP_CAST_IN_DONE", ship.PlayerShipId))
            {
                AddDecisionLog(string.Format("#{0} {1}: native AI triggered from {2}.", ship.PlayerShipId, ship.Name, reason));
                return true;
            }

            AddDecisionLog(string.Format("#{0} {1}: failed to trigger native AI.", ship.PlayerShipId, ship.Name));
            return false;
        }

        private bool SendCargoEvent(string eventName, object payload)
        {
            if (sendEventMethod == null || cargoEventType == null)
            {
                return false;
            }

            try
            {
                object eventValue = Enum.Parse(cargoEventType, eventName);
                sendEventMethod.Invoke(null, new object[] { this, eventValue, payload });
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog("Failed to send native event " + eventName + ": " + UnwrapMessage(ex));
                return false;
            }
        }

        private bool HasAnyCaptainEnabled()
        {
            foreach (KeyValuePair<int, bool> pair in captainEnabledByShipId)
            {
                if (pair.Value)
                {
                    return true;
                }
            }

            return false;
        }

        private void WarnForMissingEnabledShips(Dictionary<int, bool> visibleShipIds)
        {
            foreach (KeyValuePair<int, bool> pair in captainEnabledByShipId)
            {
                if (!pair.Value || visibleShipIds.ContainsKey(pair.Key) || missingShipWarnedByShipId.ContainsKey(pair.Key))
                {
                    continue;
                }

                string name;
                if (!lastSeenShipNameById.TryGetValue(pair.Key, out name) || string.IsNullOrEmpty(name))
                {
                    name = "unknown";
                }

                missingShipWarnedByShipId[pair.Key] = true;
                AddDecisionLog(string.Format(
                    "#{0} {1}: enabled ship is missing from the native player ship list; native game state may have removed it.",
                    pair.Key,
                    name));
            }
        }

        private bool IsCaptainEnabled(int playerShipId)
        {
            EnsureCaptainPreferenceLoaded(playerShipId);
            return captainEnabledByShipId.ContainsKey(playerShipId) && captainEnabledByShipId[playerShipId];
        }

        private void SetCaptainEnabled(int playerShipId, bool enabled)
        {
            captainEnabledByShipId[playerShipId] = enabled;
            PlayerPrefs.SetInt(CaptainPrefsPrefix + playerShipId, enabled ? 1 : 0);
            PlayerPrefs.Save();

            ShipSnapshot ship = FindShipSnapshot(playerShipId);
            if (ship != null && liveActions)
            {
                if (enabled)
                {
                    AddDecisionLog(string.Format("#{0} {1}: TO2 Captain enabled; evaluating automation plan.", ship.PlayerShipId, ship.Name));
                    EvaluateShip(ship, "toggle");
                }
                else
                {
                    SetNativeAiState(ship, false);
                    ClearNativeCargoGuard(ship);
                    ClearPlannedFreightUpgrade(ship);
                    AddDecisionLog(string.Format("#{0} {1}: TO2 Captain disabled; native AI state restored to manual.", ship.PlayerShipId, ship.Name));
                }
            }
        }

        private void EnsureCaptainPreferenceLoaded(int playerShipId)
        {
            if (!captainEnabledByShipId.ContainsKey(playerShipId))
            {
                captainEnabledByShipId[playerShipId] = PlayerPrefs.GetInt(CaptainPrefsPrefix + playerShipId, 0) == 1;
            }
        }

        private object FindController(string typeName, string tag)
        {
            Type type = FindType(typeName);
            if (type == null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(tag))
            {
                GameObject tagged = GameObject.FindWithTag(tag);
                if (tagged != null)
                {
                    Component component = tagged.GetComponent(type);
                    if (component != null)
                    {
                        return component;
                    }
                }
            }

            return UnityEngine.Object.FindObjectOfType(type);
        }

        private static Type FindType(string typeName)
        {
            Type type = Type.GetType(typeName + ", Assembly-CSharp");
            if (type != null)
            {
                return type;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(typeName);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private void AddDecisionLog(string message)
        {
            string line = string.Format("{0:HH:mm:ss} {1}", DateTime.Now, message);
            decisionLog.Add(line);
            while (decisionLog.Count > 80)
            {
                decisionLog.RemoveAt(0);
            }

            logScroll.y = float.MaxValue;
            Debug.Log(LogPrefix + " " + message);
        }

        private static string TrimForUi(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "-";
            }

            if (value.Length <= maxChars)
            {
                return value;
            }

            if (maxChars <= 3)
            {
                return value.Substring(0, maxChars);
            }

            return value.Substring(0, maxChars - 3) + "...";
        }

        private static string Capitalize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Substring(0, 1).ToUpperInvariant() + value.Substring(1);
        }

        private static bool ContainsIgnoreCase(string[] values, string value)
        {
            if (values == null || string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string UnwrapMessage(Exception ex)
        {
            TargetInvocationException targetInvocation = ex as TargetInvocationException;
            if (targetInvocation != null && targetInvocation.InnerException != null)
            {
                return targetInvocation.InnerException.Message;
            }

            return ex.Message;
        }

        private static object ReadField(object instance, string fieldName)
        {
            if (instance == null)
            {
                return null;
            }

            FieldInfo field = FindField(instance.GetType(), fieldName);
            return field == null ? null : field.GetValue(instance);
        }

        private static void WriteField(object instance, string fieldName, object value)
        {
            if (instance == null)
            {
                return;
            }

            FieldInfo field = FindField(instance.GetType(), fieldName);
            if (field != null)
            {
                field.SetValue(instance, value);
            }
        }

        private static FieldInfo FindField(Type type, string fieldName)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    return field;
                }

                type = type.BaseType;
            }

            return null;
        }

        private static string ReadString(object instance, string fieldName)
        {
            object value = ReadField(instance, fieldName);
            return value == null ? string.Empty : value.ToString();
        }

        private static int ReadInt(object instance, string fieldName)
        {
            return ToInt(ReadField(instance, fieldName));
        }

        private static long ReadLong(object instance, string fieldName)
        {
            return ToLong(ReadField(instance, fieldName));
        }

        private static float ReadFloat(object instance, string fieldName)
        {
            object value = ReadField(instance, fieldName);
            if (value == null)
            {
                return 0f;
            }

            try
            {
                return Convert.ToSingle(value);
            }
            catch
            {
                return 0f;
            }
        }

        private static bool ReadBool(object instance, string fieldName)
        {
            object value = ReadField(instance, fieldName);
            if (value == null)
            {
                return false;
            }

            try
            {
                return Convert.ToBoolean(value);
            }
            catch
            {
                return false;
            }
        }

        private static DateTime ReadDateTime(object instance, string fieldName)
        {
            object value = ReadField(instance, fieldName);
            if (value == null)
            {
                return DateTime.MinValue;
            }

            try
            {
                return Convert.ToDateTime(value);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private static int ToInt(object value)
        {
            if (value == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static long ToLong(object value)
        {
            if (value == null)
            {
                return 0L;
            }

            try
            {
                return Convert.ToInt64(value);
            }
            catch
            {
                return 0L;
            }
        }

        private sealed class FleetNoticeSnapshot
        {
            public UnityEngine.Object Item;
            public int Id;
            public int NewsType;
            public string Headline;
            public float FirstSeenRealtime;
        }

        private sealed class OnboardJobInfo
        {
            public int JobId;
            public string Start;
            public string End;
            public string Freight;
            public int Volume;
            public int Weight;
            public long Payment;
            public object RawJob;
        }

        private sealed class OnboardDestinationSummary
        {
            public string Harbor;
            public int JobCount;
            public int Volume;
            public int Weight;
            public long Payment;
        }

        private sealed class ShipSnapshot
        {
            public int PlayerShipId;
            public string Name;
            public string FreightType;
            public int Class;
            public int Volume;
            public int DeadweightTons;
            public float FuelLoaded;
            public float Condition;
            public string CurrentHarbor;
            public string DestinationHarbor;
            public string Status;
            public bool IsAI;
            public DateTime RepairFinish;
            public DateTime UpgradeFinish;
            public bool Repaired;
            public bool Upgraded;
            public int Upgrade1;
            public int Upgrade2;
            public int Upgrade3;
            public int Upgrade4;
            public int Upgrade5;
            public object RawShip;

            public static ShipSnapshot From(object rawShip)
            {
                ShipSnapshot snapshot = new ShipSnapshot();
                snapshot.RawShip = rawShip;
                snapshot.PlayerShipId = ReadInt(rawShip, "PlayerShipID");
                snapshot.Name = ReadString(rawShip, "Name");
                snapshot.FreightType = ReadString(rawShip, "FreightType");
                snapshot.Class = ReadInt(rawShip, "Class");
                snapshot.Volume = ReadInt(rawShip, "Volume");
                snapshot.DeadweightTons = ReadInt(rawShip, "DeadweightTons");
                snapshot.FuelLoaded = ReadFloat(rawShip, "FuelLoaded");
                snapshot.Condition = ReadFloat(rawShip, "Condition");
                snapshot.CurrentHarbor = ReadString(rawShip, "CurrentHarbor");
                snapshot.DestinationHarbor = ReadString(rawShip, "DestinationHarbor");
                snapshot.Status = ReadString(rawShip, "Status");
                snapshot.IsAI = ReadBool(rawShip, "IsAI");
                snapshot.RepairFinish = ReadDateTime(rawShip, "RepairFinish");
                snapshot.UpgradeFinish = ReadDateTime(rawShip, "UpgradeFinish");
                snapshot.Repaired = ReadBool(rawShip, "Repaired");
                snapshot.Upgraded = ReadBool(rawShip, "Upgraded");
                snapshot.Upgrade1 = ReadInt(rawShip, "Upgrade1");
                snapshot.Upgrade2 = ReadInt(rawShip, "Upgrade2");
                snapshot.Upgrade3 = ReadInt(rawShip, "Upgrade3");
                snapshot.Upgrade4 = ReadInt(rawShip, "Upgrade4");
                snapshot.Upgrade5 = ReadInt(rawShip, "Upgrade5");
                return snapshot;
            }

            public int GetUpgrade(int slot)
            {
                switch (slot)
                {
                    case 1:
                        return Upgrade1;
                    case 2:
                        return Upgrade2;
                    case 3:
                        return Upgrade3;
                    case 4:
                        return Upgrade4;
                    case 5:
                        return Upgrade5;
                    default:
                        return 0;
                }
            }

            public int UpgradeCount()
            {
                int count = 0;
                if (Upgrade1 != 0)
                {
                    count++;
                }

                if (Upgrade2 != 0)
                {
                    count++;
                }

                if (Upgrade3 != 0)
                {
                    count++;
                }

                if (Upgrade4 != 0)
                {
                    count++;
                }

                if (Upgrade5 != 0)
                {
                    count++;
                }

                return count;
            }

            public void SetUpgrade(int slot, int upgradeId)
            {
                switch (slot)
                {
                    case 1:
                        Upgrade1 = upgradeId;
                        break;
                    case 2:
                        Upgrade2 = upgradeId;
                        break;
                    case 3:
                        Upgrade3 = upgradeId;
                        break;
                    case 4:
                        Upgrade4 = upgradeId;
                        break;
                    case 5:
                        Upgrade5 = upgradeId;
                        break;
                }
            }
        }

        private sealed class RepairPlan
        {
            public bool CanStart;
            public string Message;
            public float StartCondition;
            public float TargetCondition;
            public int Region;
            public long RepairDockBasePrice;
            public long EstimatedGrossCost;
            public int RepairDays;
        }

        private sealed class RepairDockCandidate
        {
            public string Harbor;
            public float Distance;
            public float ArrivalCondition;
        }

        private sealed class UpgradePlan
        {
            public bool CanStart;
            public string Harbor;
            public int UpgradeId;
            public int Slot;
            public string Name;
            public int Region;
            public int UpgradeDockOwner;
            public long UpgradeDockOwnerShares;
            public long EstimatedCost;
            public double Score;
        }

        private sealed class UpgradeDockCandidate
        {
            public string Harbor;
            public float Distance;
            public float ArrivalCondition;
            public UpgradePlan Plan;
        }

        private sealed class HarborJobPlanSet
        {
            public readonly List<JobPlan> Plans = new List<JobPlan>();
            public string StartHarbor;
            public int ContrabandSkipped;
            public int CandidateCount;
        }

        private sealed class RoutePlan
        {
            public readonly List<JobPlan> Legs = new List<JobPlan>();
            public double TotalScore;
            public long TotalPayment;
            public int TotalDistanceInDays;
            public int TotalJobs;

            public JobPlan FirstLeg
            {
                get { return Legs.Count == 0 ? null : Legs[0]; }
            }

            public long FuturePayment
            {
                get
                {
                    long payment = 0L;
                    for (int i = 1; i < Legs.Count; i++)
                    {
                        payment += Legs[i].Payment;
                    }

                    return payment;
                }
            }

            public int FutureJobs
            {
                get
                {
                    int jobs = 0;
                    for (int i = 1; i < Legs.Count; i++)
                    {
                        jobs += Legs[i].Jobs.Count;
                    }

                    return jobs;
                }
            }

            public int FutureDistanceInDays
            {
                get
                {
                    int days = 0;
                    for (int i = 1; i < Legs.Count; i++)
                    {
                        days += Legs[i].DistanceInDays;
                    }

                    return days;
                }
            }

            public void AddLeg(JobPlan leg, double weightedScore)
            {
                if (leg == null)
                {
                    return;
                }

                Legs.Add(leg);
                TotalScore += weightedScore;
                TotalPayment += leg.Payment;
                TotalDistanceInDays += leg.DistanceInDays;
                TotalJobs += leg.Jobs.Count;
            }

            public void Append(RoutePlan route)
            {
                if (route == null)
                {
                    return;
                }

                for (int i = 0; i < route.Legs.Count; i++)
                {
                    Legs.Add(route.Legs[i]);
                }

                TotalScore += route.TotalScore;
                TotalPayment += route.TotalPayment;
                TotalDistanceInDays += route.TotalDistanceInDays;
                TotalJobs += route.TotalJobs;
            }

            public string GetFutureSummary()
            {
                if (Legs.Count <= 1)
                {
                    return string.Empty;
                }

                List<string> harbors = new List<string>();
                for (int i = 1; i < Legs.Count; i++)
                {
                    if (!string.IsNullOrEmpty(Legs[i].End))
                    {
                        harbors.Add(Legs[i].End);
                    }
                }

                return harbors.Count == 0 ? string.Empty : string.Join(" -> ", harbors.ToArray());
            }
        }

        private sealed class JobPlan
        {
            public readonly List<JobCandidate> Jobs = new List<JobCandidate>();
            public string Start;
            public string End;
            public int ReservedVolume;
            public int ReservedWeight;
            public int Volume;
            public int Weight;
            public long Payment;
            public int DistanceInDays;
            public double Score;
            public double ChainScore;
            public long ChainPayment;
            public int ChainJobs;
            public string ChainEnd;
            public int ChainDistanceInDays;
            public int RouteLegs;
            public int ContrabandSkipped;
            public int CandidateCount;
            public bool HasContraband;

            public double TotalScore
            {
                get { return Score + ChainScore; }
            }

            public string GetChainSummary()
            {
                if (Math.Abs(ChainScore) < 0.1 && ChainJobs <= 0)
                {
                    return string.Empty;
                }

                string routeSummary = string.IsNullOrEmpty(ChainEnd) ? "no strong follow-on" : ChainEnd;
                return string.Format(
                    " Lookahead: {0:+0;-0;0} score via {1} ({2} future leg(s), {3} future job(s), {4:n0} pay, eta +{5}d).",
                    ChainScore,
                    routeSummary,
                    Math.Max(0, RouteLegs - 1),
                    ChainJobs,
                    ChainPayment,
                    ChainDistanceInDays);
            }

            public void Add(JobCandidate candidate)
            {
                Jobs.Add(candidate);
                if (string.IsNullOrEmpty(Start))
                {
                    Start = candidate.Start;
                }

                if (string.IsNullOrEmpty(End))
                {
                    End = candidate.End;
                }

                Volume += candidate.Volume;
                Weight += candidate.Weight;
                Payment += candidate.Payment;
                DistanceInDays = Math.Max(DistanceInDays, candidate.DistanceInDays);
                Score += candidate.Score;
                HasContraband = HasContraband || candidate.IsContraband;
            }

            public void RemoveLast()
            {
                if (Jobs.Count == 0)
                {
                    return;
                }

                JobCandidate candidate = Jobs[Jobs.Count - 1];
                Jobs.RemoveAt(Jobs.Count - 1);
                Volume -= candidate.Volume;
                Weight -= candidate.Weight;
                Payment -= candidate.Payment;
                Score -= candidate.Score;
                RecalculateDerivedState();
            }

            public JobPlan Clone()
            {
                JobPlan clone = new JobPlan();
                clone.Start = Start;
                clone.End = End;
                clone.ReservedVolume = ReservedVolume;
                clone.ReservedWeight = ReservedWeight;
                for (int i = 0; i < Jobs.Count; i++)
                {
                    clone.Add(Jobs[i]);
                }

                return clone;
            }

            private void RecalculateDerivedState()
            {
                DistanceInDays = 0;
                HasContraband = false;
                for (int i = 0; i < Jobs.Count; i++)
                {
                    DistanceInDays = Math.Max(DistanceInDays, Jobs[i].DistanceInDays);
                    HasContraband = HasContraband || Jobs[i].IsContraband;
                }
            }
        }

        private sealed class JobCandidate
        {
            public int JobId;
            public string Freight;
            public string Start;
            public string End;
            public int Volume;
            public int Weight;
            public long Payment;
            public int DistanceInDays;
            public double Score;
            public bool IsContraband;
            public object RawJob;

            public static JobCandidate From(object rawJob, ShipSnapshot ship, bool isContraband)
            {
                if (ReadBool(rawJob, "Reserved"))
                {
                    return null;
                }

                int playerShipId = ReadInt(rawJob, "PlayerShipID");
                if (playerShipId != 0 && playerShipId != 1)
                {
                    return null;
                }

                int volume = ReadInt(rawJob, "Volume");
                int weight = ReadInt(rawJob, "Weight");
                if (ship.Volume > 0 && volume > ship.Volume)
                {
                    return null;
                }

                if (ship.DeadweightTons > 0 && weight > ship.DeadweightTons)
                {
                    return null;
                }

                JobCandidate candidate = new JobCandidate();
                candidate.RawJob = rawJob;
                candidate.JobId = ReadInt(rawJob, "JobID");
                candidate.Freight = ReadString(rawJob, "Freight");
                candidate.Start = ReadString(rawJob, "Start");
                candidate.End = ReadString(rawJob, "End");
                if (candidate.JobId <= 0 || string.IsNullOrEmpty(candidate.End))
                {
                    return null;
                }

                candidate.Volume = volume;
                candidate.Weight = weight;
                candidate.Payment = ReadLong(rawJob, "Payment");
                candidate.DistanceInDays = Math.Max(1, ReadInt(rawJob, "DistanceInDays"));
                candidate.IsContraband = isContraband;

                int relationshipPoints = ReadInt(rawJob, "RelationshipPoints");
                long penalty = Math.Max(0L, ReadLong(rawJob, "ContractualPenalty"));
                double paymentPerDay = (double)candidate.Payment / (double)candidate.DistanceInDays;
                candidate.Score = paymentPerDay + (relationshipPoints * 1000.0) - (penalty * 0.02);
                return candidate;
            }
        }
    }
}
