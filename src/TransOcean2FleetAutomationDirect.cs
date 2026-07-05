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
        private object shipFactory;
        private MethodInfo getPlayerIdMethod;
        private MethodInfo getPlayerCreditsMethod;
        private MethodInfo getAllPlayerShipsMethod;
        private MethodInfo getJobsFromStartHarborMethod;
        private MethodInfo updatePlayerShipAiStateMethod;
        private MethodInfo sendEventMethod;
        private Type cargoEventType;

        private bool showPanel;
        private bool liveActions = true;
        private bool evaluateEnabledShipsEveryTick = true;
        private bool gameSessionActive;
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
            evaluateEnabledShipsEveryTick = PlayerPrefs.GetInt("TO2FA.TickEnabled", 1) == 1;
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
            get { return dsqLite != null && shipFactory != null; }
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
            GUILayout.Label(TrimForUi(ship.Status, 13), GUILayout.Width(95f));
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
            updatePlayerShipAiStateMethod = dsqLiteType.GetMethod("UpdatePlayerShipAiState", new Type[] { typeof(int), typeof(bool) });
            getJobsFromStartHarborMethod = dsqLiteType.GetMethod(
                "GetAllJobsFromStartHarbor",
                new Type[] { typeof(string), typeof(string), typeof(int) });
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

            if (liveActions)
            {
                SetNativeAiState(ship, true);
            }

            if (!IsShipIdleInHarbor(ship))
            {
                AddDecisionLog(prefix + (liveActions ? "native AI armed; waiting because ship is not idle in a harbor." : "waiting; ship is not idle in a harbor."));
                return;
            }

            long reserve = Math.Max(500000L, Math.Abs(playerCredits) / 5L);
            if (ship.Condition < 55f)
            {
                if (playerCredits > reserve)
                {
                    AddDecisionLog(prefix + string.Format("repair is priority ({0:0}% condition, reserve {1:n0}).", ship.Condition, reserve));
                }
                else
                {
                    AddDecisionLog(prefix + string.Format("condition is low ({0:0}%) but treasury is below reserve; defer non-emergency repair.", ship.Condition));
                }
            }
            else if (ship.Condition < 70f && playerCredits > reserve * 2L)
            {
                AddDecisionLog(prefix + string.Format("repair soon if route options are weak ({0:0}% condition).", ship.Condition));
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
            if (string.IsNullOrEmpty(ship.CurrentHarbor))
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
                    SetNativeAiState(ships[i], nativeAiEnabled);
                    if (nativeAiEnabled && IsShipIdleInHarbor(ships[i]))
                    {
                        TriggerNativeAiCastOut(ships[i], "live actions toggled on");
                    }
                }
            }
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
            if (sendEventMethod == null || cargoEventType == null)
            {
                AddDecisionLog(string.Format("#{0} {1}: native event bridge is unavailable.", ship.PlayerShipId, ship.Name));
                return false;
            }

            try
            {
                object eventValue = Enum.Parse(cargoEventType, "SHIP_CAST_IN_DONE");
                sendEventMethod.Invoke(null, new object[] { this, eventValue, ship.PlayerShipId });
                AddDecisionLog(string.Format("#{0} {1}: native AI triggered from {2}.", ship.PlayerShipId, ship.Name, reason));
                return true;
            }
            catch (Exception ex)
            {
                AddDecisionLog(string.Format("#{0} {1}: failed to trigger native AI: {2}", ship.PlayerShipId, ship.Name, UnwrapMessage(ex)));
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
                return snapshot;
            }
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
