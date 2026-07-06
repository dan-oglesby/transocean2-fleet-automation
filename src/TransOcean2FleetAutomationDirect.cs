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

        private readonly Dictionary<int, bool> captainEnabledByShipId = new Dictionary<int, bool>();
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
        private MethodInfo getRepairDockHarborsMethod;
        private MethodInfo getHarborDistanceMethod;
        private MethodInfo getRegionOfHarborMethod;
        private MethodInfo getShipClassesMethod;
        private MethodInfo repairPlayerShipMethod;
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
        private bool evaluateEnabledShipsEveryTick = true;
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
            evaluateEnabledShipsEveryTick = PlayerPrefs.GetInt("TO2FA.TickEnabled", 1) == 1;
            minimumSailCondition = PlayerPrefs.GetFloat("TO2FA.MinimumSailCondition", 85f);
            minimumSailCondition = Mathf.Clamp(minimumSailCondition, 50f, 100f);
            repairTargetCondition = PlayerPrefs.GetFloat("TO2FA.RepairTargetCondition", 100f);
            repairTargetCondition = Mathf.Clamp(repairTargetCondition, minimumSailCondition, 100f);
            AddDecisionLog("Captain UI attached. Live actions are " + (liveActions ? "ON." : "OFF."));
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
            getJobsFromStartHarborMethod = dsqLiteType.GetMethod(
                "GetAllJobsFromStartHarbor",
                new Type[] { typeof(string), typeof(string), typeof(int) });
            Type staticSqLiteType = staticSqLite.GetType();
            getHarborMethod = staticSqLiteType.GetMethod("GetHarbor", new Type[] { typeof(string) });
            getRepairDockHarborsMethod = staticSqLiteType.GetMethod("GetRepairDockHarbors", Type.EmptyTypes);
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

                object rawShips = getAllPlayerShipsMethod.Invoke(dsqLite, new object[] { playerId });
                IEnumerable enumerableShips = rawShips as IEnumerable;
                if (enumerableShips != null)
                {
                    foreach (object rawShip in enumerableShips)
                    {
                        ShipSnapshot ship = ShipSnapshot.From(rawShip);
                        ships.Add(ship);
                        EnsureCaptainPreferenceLoaded(ship.PlayerShipId);
                    }
                }

                gameSessionActive = playerId > 0 && ships.Count > 0;
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

            if (IsRepairPending(ship))
            {
                SetNativeAiState(ship, false);
                AddDecisionLog(prefix + "repair is already in progress; keeping automation on hold.");
                return;
            }

            if (ship.Condition < minimumSailCondition)
            {
                HandleLowConditionShip(ship, prefix, reserve);
                return;
            }

            if (!IsShipIdleInHarbor(ship))
            {
                AddDecisionLog(prefix + (liveActions ? "native AI armed; waiting because ship is not idle in a harbor." : "waiting; ship is not idle in a harbor."));
                return;
            }

            if (liveActions)
            {
                SetNativeAiState(ship, true);
            }

            if (ship.Condition < minimumSailCondition + 5f && playerCredits > reserve * 2L)
            {
                AddDecisionLog(prefix + string.Format("maintenance soon: condition {0:0}% is close to sail minimum {1:0}%.", ship.Condition, minimumSailCondition));
            }

            JobCandidate bestJob = FindBestJob(ship);
            if (bestJob == null)
            {
                AddDecisionLog(prefix + "no matching unreserved jobs found at " + ship.CurrentHarbor + ".");
            }
            else
            {
                AddDecisionLog(prefix + string.Format(
                    "best route {0} -> {1}, pay {2:n0}, eta {3}d, score {4:0}.",
                    bestJob.Start,
                    bestJob.End,
                    bestJob.Payment,
                    bestJob.DistanceInDays,
                    bestJob.Score));
            }

            if (liveActions)
            {
                TriggerNativeAiCastOut(ship, reason);
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

            try
            {
                sendShipToDestinationMethod.Invoke(shipFactory, new object[] { ship.PlayerShipId, destination.Harbor });
                ship.DestinationHarbor = destination.Harbor;
                AddDecisionLog(prefix + string.Format(
                    "routing to repair dock at {0} ({1:0} distance units, arrival condition about {2:0}%) before taking more work.",
                    destination.Harbor,
                    destination.Distance,
                    destination.ArrivalCondition));
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog(prefix + "failed to route to repair dock: " + UnwrapMessage(ex));
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

        private static bool HasWaypointUpgrade(ShipSnapshot ship)
        {
            return ship.Upgrade3 == 5 && (ship.Class == 3 || ship.Class == 4);
        }

        private JobCandidate FindBestJob(ShipSnapshot ship)
        {
            if (getJobsFromStartHarborMethod == null || string.IsNullOrEmpty(ship.CurrentHarbor))
            {
                return null;
            }

            object rawJobs = null;
            try
            {
                rawJobs = getJobsFromStartHarborMethod.Invoke(
                    dsqLite,
                    new object[] { ship.CurrentHarbor, ship.FreightType, ship.Class });
            }
            catch (Exception ex)
            {
                AddDecisionLog("Job lookup failed for " + ship.Name + ": " + UnwrapMessage(ex));
                return null;
            }

            IEnumerable jobs = rawJobs as IEnumerable;
            if (jobs == null)
            {
                return null;
            }

            JobCandidate best = null;
            foreach (object rawJob in jobs)
            {
                JobCandidate candidate = JobCandidate.From(rawJob, ship);
                if (candidate == null)
                {
                    continue;
                }

                if (best == null || candidate.Score > best.Score)
                {
                    best = candidate;
                }
            }

            return best;
        }

        private bool IsShipIdleInHarbor(ShipSnapshot ship)
        {
            if (IsRepairPending(ship))
            {
                return false;
            }

            if (!HasKnownCurrentHarbor(ship))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(ship.DestinationHarbor) && ship.DestinationHarbor != ship.CurrentHarbor)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(ship.Status))
            {
                string status = ship.Status.ToLowerInvariant();
                if (status.Contains("repair") || status.Contains("upgrade") || status.Contains("cast") || status.Contains("travel"))
                {
                    return false;
                }
            }

            return true;
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
                    }
                    else if (ships[i].Condition < minimumSailCondition)
                    {
                        string prefix = string.Format("#{0} {1}: ", ships[i].PlayerShipId, ships[i].Name);
                        long reserve = Math.Max(500000L, Math.Abs(playerCredits) / 5L);
                        HandleLowConditionShip(ships[i], prefix, reserve);
                    }
                    else
                    {
                        SetNativeAiState(ships[i], true);
                        if (IsShipIdleInHarbor(ships[i]))
                        {
                            TriggerNativeAiCastOut(ships[i], "live actions toggled on");
                        }
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
                    AddDecisionLog(string.Format("#{0} {1}: TO2 Captain enabled; arming native AI.", ship.PlayerShipId, ship.Name));
                    EvaluateShip(ship, "toggle");
                }
                else
                {
                    SetNativeAiState(ship, false);
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

            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(instance);
        }

        private static void WriteField(object instance, string fieldName, object value)
        {
            if (instance == null)
            {
                return;
            }

            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(instance, value);
            }
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
            public bool Repaired;
            public int Upgrade3;
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
                snapshot.Repaired = ReadBool(rawShip, "Repaired");
                snapshot.Upgrade3 = ReadInt(rawShip, "Upgrade3");
                return snapshot;
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

        private sealed class JobCandidate
        {
            public string Start;
            public string End;
            public long Payment;
            public int DistanceInDays;
            public double Score;

            public static JobCandidate From(object rawJob, ShipSnapshot ship)
            {
                if (ReadBool(rawJob, "Reserved"))
                {
                    return null;
                }

                if (ReadInt(rawJob, "PlayerShipID") != 0)
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
                candidate.Start = ReadString(rawJob, "Start");
                candidate.End = ReadString(rawJob, "End");
                candidate.Payment = ReadLong(rawJob, "Payment");
                candidate.DistanceInDays = Math.Max(1, ReadInt(rawJob, "DistanceInDays"));

                int relationshipPoints = ReadInt(rawJob, "RelationshipPoints");
                long penalty = Math.Max(0L, ReadLong(rawJob, "ContractualPenalty"));
                double paymentPerDay = (double)candidate.Payment / (double)candidate.DistanceInDays;
                candidate.Score = paymentPerDay + (relationshipPoints * 1000.0) - (penalty * 0.02);
                return candidate;
            }
        }
    }
}
