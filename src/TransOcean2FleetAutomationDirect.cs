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
        private const double ChainOpportunityWeight = 0.35;
        private const float DispatchSettleSeconds = 20f;

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
        private readonly Dictionary<string, bool> contrabandFreightByName = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ShipSnapshot> ships = new List<ShipSnapshot>();
        private readonly List<string> decisionLog = new List<string>();

        private object dsqLite;
        private object staticSqLite;
        private object shipFactory;
        private MethodInfo getPlayerIdMethod;
        private MethodInfo getPlayerCreditsMethod;
        private MethodInfo getAllPlayerShipsMethod;
        private MethodInfo getPlayerShipMethod;
        private MethodInfo getJobsFromStartHarborMethod;
        private MethodInfo getHarborMethod;
        private MethodInfo getFreightAttributesMethod;
        private MethodInfo getRepairDockHarborsMethod;
        private MethodInfo getUpgradeDockHarborsMethod;
        private MethodInfo getUpgradeExportFromHarborMethod;
        private MethodInfo getBusinessSharesFromRegionMethod;
        private MethodInfo getHarborDistanceMethod;
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
        private MethodInfo getShipSinkChanceMethod;
        private MethodInfo getDaysForRepairMethod;
        private MethodInfo getCurrentDateTimeMethod;
        private MethodInfo updatePlayerShipAiStateMethod;
        private MethodInfo sendEventMethod;
        private Type cargoEventType;

        private bool showPanel;
        private bool liveActions = true;
        private bool autoRepairs = true;
        private bool autoUpgrades = true;
        private bool evaluateEnabledShipsEveryTick = true;
        private bool allowContrabandCargo;
        private bool gameSessionActive;
        private float minimumSailCondition = 85f;
        private float repairTargetCondition = 100f;
        private float nextControllerLookup;
        private float nextRefresh;
        private float nextAutomationTick;
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
            minimumSailCondition = PlayerPrefs.GetFloat("TO2FA.MinimumSailCondition", 85f);
            minimumSailCondition = Mathf.Clamp(minimumSailCondition, 50f, 100f);
            repairTargetCondition = PlayerPrefs.GetFloat("TO2FA.RepairTargetCondition", 100f);
            repairTargetCondition = Mathf.Clamp(repairTargetCondition, minimumSailCondition, 100f);
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

            if (evaluateEnabledShipsEveryTick && Time.realtimeSinceStartup >= nextAutomationTick)
            {
                nextAutomationTick = Time.realtimeSinceStartup + 30f;
                if (IsGameSessionActive() && HasAnyCaptainEnabled())
                {
                    EvaluateEnabledShips("scheduled automation tick");
                }
            }
        }

        private void OnGUI()
        {
            if (!showPanel || !IsGameSessionActive())
            {
                return;
            }

            float width = Mathf.Min(880f, Screen.width - 24f);
            float height = Mathf.Min(680f, Screen.height - 24f);
            GUILayout.BeginArea(new Rect(12f, 12f, width, height), "TO2 Fleet Captain", GUI.skin.window);

            GUILayout.Label(statusText);
            GUILayout.Label(string.Format("Player {0} treasury: {1:n0}", playerId, playerCredits));

            GUILayout.BeginHorizontal();
            bool newLiveActions = GUILayout.Toggle(liveActions, "Live actions", GUILayout.Width(125f));
            if (newLiveActions != liveActions)
            {
                liveActions = newLiveActions;
                PlayerPrefs.SetInt("TO2FA.LiveActions", liveActions ? 1 : 0);
                PlayerPrefs.Save();
                AddDecisionLog("Live actions " + (liveActions ? "enabled." : "disabled; restoring manual control for enabled ships."));
                SyncNativeAiStateForEnabledShips(liveActions);
            }

            bool newAutoRepairs = GUILayout.Toggle(autoRepairs, "Auto repairs", GUILayout.Width(120f));
            if (newAutoRepairs != autoRepairs)
            {
                autoRepairs = newAutoRepairs;
                PlayerPrefs.SetInt("TO2FA.AutoRepairs", autoRepairs ? 1 : 0);
                PlayerPrefs.Save();
                AddDecisionLog("Auto repairs " + (autoRepairs ? "enabled." : "disabled."));
            }

            bool newAutoUpgrades = GUILayout.Toggle(autoUpgrades, "Auto upgrades", GUILayout.Width(130f));
            if (newAutoUpgrades != autoUpgrades)
            {
                autoUpgrades = newAutoUpgrades;
                PlayerPrefs.SetInt("TO2FA.AutoUpgrades", autoUpgrades ? 1 : 0);
                PlayerPrefs.Save();
                AddDecisionLog("Auto upgrades " + (autoUpgrades ? "enabled." : "disabled."));
            }

            bool newAllowContrabandCargo = GUILayout.Toggle(allowContrabandCargo, "Allow contraband", GUILayout.Width(145f));
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

            bool newTick = GUILayout.Toggle(evaluateEnabledShipsEveryTick, "Evaluate every 30s", GUILayout.Width(180f));
            if (newTick != evaluateEnabledShipsEveryTick)
            {
                evaluateEnabledShipsEveryTick = newTick;
                PlayerPrefs.SetInt("TO2FA.TickEnabled", evaluateEnabledShipsEveryTick ? 1 : 0);
                PlayerPrefs.Save();
            }

            if (GUILayout.Button("Refresh", GUILayout.Width(90f)))
            {
                RefreshFleet(true);
            }

            if (GUILayout.Button("Run", GUILayout.Width(100f)))
            {
                EvaluateEnabledShips("panel button");
            }

            if (GUILayout.Button("Enable all", GUILayout.Width(100f)))
            {
                SetAllVisibleShips(true);
            }

            if (GUILayout.Button("Disable all", GUILayout.Width(105f)))
            {
                SetAllVisibleShips(false);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format("Minimum condition to sail: {0:0}%", minimumSailCondition), GUILayout.Width(235f));
            float newMinimumSailCondition = GUILayout.HorizontalSlider(minimumSailCondition, 50f, 100f, GUILayout.Width(240f));
            newMinimumSailCondition = Mathf.Round(newMinimumSailCondition);
            if (Mathf.Abs(newMinimumSailCondition - minimumSailCondition) > 0.1f)
            {
                minimumSailCondition = newMinimumSailCondition;
                PlayerPrefs.SetFloat("TO2FA.MinimumSailCondition", minimumSailCondition);
                PlayerPrefs.Save();
                AddDecisionLog(string.Format("Minimum condition to sail set to {0:0}%.", minimumSailCondition));
            }
            if (GUILayout.Button("85%", GUILayout.Width(55f)))
            {
                SetMinimumSailCondition(85f);
            }
            if (GUILayout.Button("90%", GUILayout.Width(55f)))
            {
                SetMinimumSailCondition(90f);
            }
            if (GUILayout.Button("95%", GUILayout.Width(55f)))
            {
                SetMinimumSailCondition(95f);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format("Repair target condition: {0:0}%", repairTargetCondition), GUILayout.Width(235f));
            float newRepairTargetCondition = GUILayout.HorizontalSlider(repairTargetCondition, minimumSailCondition, 100f, GUILayout.Width(240f));
            newRepairTargetCondition = Mathf.Round(newRepairTargetCondition);
            if (Mathf.Abs(newRepairTargetCondition - repairTargetCondition) > 0.1f)
            {
                SetRepairTargetCondition(newRepairTargetCondition);
            }
            if (GUILayout.Button("90%", GUILayout.Width(55f)))
            {
                SetRepairTargetCondition(90f);
            }
            if (GUILayout.Button("95%", GUILayout.Width(55f)))
            {
                SetRepairTargetCondition(95f);
            }
            if (GUILayout.Button("100%", GUILayout.Width(55f)))
            {
                SetRepairTargetCondition(100f);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Auto", GUILayout.Width(45f));
            GUILayout.Label("Ship", GUILayout.Width(205f));
            GUILayout.Label("Status", GUILayout.Width(95f));
            GUILayout.Label("Harbor", GUILayout.Width(145f));
            GUILayout.Label("Dest", GUILayout.Width(145f));
            GUILayout.Label("Cond", GUILayout.Width(60f));
            GUILayout.Label("Fuel", GUILayout.Width(60f));
            GUILayout.Label("Plan", GUILayout.Width(60f));
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Max(180f, height * 0.45f)));
            for (int i = 0; i < ships.Count; i++)
            {
                DrawShipRow(ships[i]);
            }
            GUILayout.EndScrollView();

            GUILayout.Label("Decision log");
            logScroll = GUILayout.BeginScrollView(logScroll, GUILayout.Height(Mathf.Max(120f, height * 0.28f)));
            for (int i = 0; i < decisionLog.Count; i++)
            {
                GUILayout.Label(decisionLog[i]);
            }
            GUILayout.EndScrollView();

            GUILayout.Label("F8 run enabled ships. F9 hide panel. F10 refresh fleet.");
            GUILayout.EndArea();
        }

        private bool ControllersReady
        {
            get { return dsqLite != null && staticSqLite != null && shipFactory != null; }
        }

        private bool IsGameSessionActive()
        {
            return gameSessionActive && playerId > 0 && ships.Count > 0;
        }

        private void DrawShipRow(ShipSnapshot ship)
        {
            GUILayout.BeginHorizontal();
            bool enabled = IsCaptainEnabled(ship.PlayerShipId);
            bool next = GUILayout.Toggle(enabled, string.Empty, GUILayout.Width(45f));
            if (next != enabled)
            {
                SetCaptainEnabled(ship.PlayerShipId, next);
            }

            GUILayout.Label(TrimForUi("#" + ship.PlayerShipId + " " + ship.Name, 28), GUILayout.Width(205f));
            GUILayout.Label(TrimForUi(GetDisplayStatus(ship), 13), GUILayout.Width(95f));
            GUILayout.Label(TrimForUi(ship.CurrentHarbor, 18), GUILayout.Width(145f));
            GUILayout.Label(TrimForUi(ship.DestinationHarbor, 18), GUILayout.Width(145f));
            GUILayout.Label(string.Format("{0:0}%", ship.Condition), GUILayout.Width(60f));
            GUILayout.Label(string.Format("{0:0}", ship.FuelLoaded), GUILayout.Width(60f));

            if (GUILayout.Button("Plan", GUILayout.Width(60f)))
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
            upgradePlayerShipMethod = shipFactoryType.GetMethod("UpgradePlayerShip", new Type[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(long) });
            getUpgradeDockOwnerMethod = shipFactoryType.GetMethod("GetUpgradeDockOwner", new Type[] { typeof(int) });
            getShipSinkChanceMethod = shipFactoryType.GetMethod("GetShipSinkChance", new Type[] { typeof(int), typeof(float) });
            getDaysForRepairMethod = shipFactoryType.GetMethod("GetDaysForRepair", new Type[] { typeof(float), typeof(int) });
            Type timerType = FindType("Deck13HH.Timer");
            if (timerType != null)
            {
                getCurrentDateTimeMethod = timerType.GetMethod("get_CurrentDateTime", BindingFlags.Public | BindingFlags.Static);
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

                Dictionary<int, bool> visibleShipIds = new Dictionary<int, bool>();
                object rawShips = getAllPlayerShipsMethod.Invoke(dsqLite, new object[] { playerId });
                IEnumerable enumerableShips = rawShips as IEnumerable;
                if (enumerableShips != null)
                {
                    foreach (object rawShip in enumerableShips)
                    {
                        ShipSnapshot ship = ShipSnapshot.From(rawShip);
                        ships.Add(ship);
                        EnsureCaptainPreferenceLoaded(ship.PlayerShipId);
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
            long upgradeReserve = Math.Max(1500000L, Math.Abs(playerCredits) / 3L);

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

            if (liveActions)
            {
                if (bestPlan == null)
                {
                    SetNativeAiState(ship, false);
                    ClearNativeCargoGuard(ship);
                    AddDecisionLog(prefix + "live dispatch held so native AI cannot choose blocked cargo.");
                }
                else
                {
                    DispatchJobPlan(ship, bestPlan, reason);
                }
            }
            else
            {
                AddDecisionLog(prefix + "live actions are OFF; recommendation only.");
            }
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
                AddDecisionLog(prefix + "held for maintenance; no safe reachable repair dock found.");
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

        private bool TryHandleUpgradeNeed(ShipSnapshot ship, string prefix, long reserve)
        {
            if (upgradePlayerShipMethod == null || getUpgradeDockHarborsMethod == null || getRegionOfHarborMethod == null)
            {
                return false;
            }

            if (!HasFleetUpgradeTurn(ship))
            {
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
            return TrySendToServiceDock(ship, prefix, "upgrade", destination.Harbor, destination.Distance, destination.ArrivalCondition);
        }

        private bool HasFleetUpgradeTurn(ShipSnapshot ship)
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
                return true;
            }

            return ship.UpgradeCount() <= minimumUpgradeCount;
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
            }

            return score;
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

        private JobPlan FindBestJobPlanFromHarbor(ShipSnapshot ship, string startHarbor, bool includeChainOpportunity)
        {
            int contrabandSkipped = 0;
            int candidateCount = 0;
            Dictionary<string, List<JobCandidate>> candidatesByDestination = CollectJobCandidatesFromHarbor(ship, startHarbor, out contrabandSkipped, out candidateCount);
            if (candidatesByDestination == null)
            {
                return null;
            }

            JobPlan best = null;
            foreach (KeyValuePair<string, List<JobCandidate>> pair in candidatesByDestination)
            {
                JobPlan plan = BuildJobPlanForDestination(ship, startHarbor, pair.Key, pair.Value);
                if (plan == null)
                {
                    continue;
                }

                if (includeChainOpportunity)
                {
                    AddChainOpportunity(ship, plan);
                }

                if (best == null || plan.TotalScore > best.TotalScore)
                {
                    best = plan;
                }
            }

            if (best != null)
            {
                best.ContrabandSkipped = contrabandSkipped;
                best.CandidateCount = candidateCount;
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

        private void AddChainOpportunity(ShipSnapshot ship, JobPlan plan)
        {
            if (plan == null || string.IsNullOrEmpty(plan.End) || plan.End == plan.Start)
            {
                return;
            }

            JobPlan followUp = FindBestJobPlanFromHarbor(ship, plan.End, false);
            if (followUp == null)
            {
                return;
            }

            plan.ChainScore = followUp.Score * ChainOpportunityWeight;
            plan.ChainPayment = followUp.Payment;
            plan.ChainJobs = followUp.Jobs.Count;
            plan.ChainEnd = followUp.End;
            plan.ChainDistanceInDays = followUp.DistanceInDays;
        }

        private static JobPlan BuildJobPlanForDestination(ShipSnapshot ship, string startHarbor, string destination, List<JobCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0 || string.IsNullOrEmpty(destination))
            {
                return null;
            }

            candidates.Sort(delegate(JobCandidate left, JobCandidate right)
            {
                return right.Score.CompareTo(left.Score);
            });

            JobPlan plan = new JobPlan();
            plan.Start = startHarbor;
            plan.End = destination;

            for (int i = 0; i < candidates.Count; i++)
            {
                JobCandidate candidate = candidates[i];
                if (!CanAddCandidateToPlan(ship, plan, candidate))
                {
                    continue;
                }

                plan.Add(candidate);
            }

            return plan.Jobs.Count == 0 ? null : plan;
        }

        private static bool CanAddCandidateToPlan(ShipSnapshot ship, JobPlan plan, JobCandidate candidate)
        {
            if (ship.Volume > 0 && plan.Volume + candidate.Volume > ship.Volume)
            {
                return false;
            }

            if (ship.DeadweightTons > 0 && plan.Weight + candidate.Weight > ship.DeadweightTons)
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

            if (Time.realtimeSinceStartup - lastDispatch <= DispatchSettleSeconds)
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
            }
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
            minimumSailCondition = Mathf.Clamp(value, 50f, 100f);
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

        private sealed class JobPlan
        {
            public readonly List<JobCandidate> Jobs = new List<JobCandidate>();
            public string Start;
            public string End;
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
            public int ContrabandSkipped;
            public int CandidateCount;
            public bool HasContraband;

            public double TotalScore
            {
                get { return Score + ChainScore; }
            }

            public string GetChainSummary()
            {
                if (ChainJobs <= 0 || string.IsNullOrEmpty(ChainEnd))
                {
                    return string.Empty;
                }

                return string.Format(
                    " Chain opportunity: +{0:0} score toward {1} ({2} follow-on job(s), {3:n0} pay, eta {4}d).",
                    ChainScore,
                    ChainEnd,
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
                if (playerShipId != 0 && playerShipId != 1 && playerShipId != ship.PlayerShipId)
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
