using System;
using System.Collections;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;

namespace TransOcean2FleetAutomation
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class FleetAutomationProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.daogl.transocean2.fleetautomationprobe";
        public const string PluginName = "TransOcean 2 Fleet Automation Probe";
        public const string PluginVersion = "0.1.2";

        private Type dynamicDbType;
        private Type staticDbType;
        private Type shipFactoryType;
        private UnityEngine.Object dynamicDb;
        private UnityEngine.Object staticDb;
        private UnityEngine.Object shipFactory;
        private MethodInfo getAllPlayerShipsMethod;
        private float nextAutoProbeTime;
        private int startupProbeAttempts;
        private bool startupProbeDone;

        private void Awake()
        {
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded in reflection-only mode. Press F8 in-game to dump fleet automation state.");
            nextAutoProbeTime = 5f;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                DumpFleetState("F8");
            }

            if (Time.unscaledTime >= nextAutoProbeTime)
            {
                if (!startupProbeDone)
                {
                    ProbeStartupTick();
                }
                else
                {
                    nextAutoProbeTime = Time.unscaledTime + 30f;
                    RefreshReferences();
                }
            }
        }

        private void ProbeStartupTick()
        {
            nextAutoProbeTime = Time.unscaledTime + 1f;
            startupProbeAttempts++;
            RefreshReferences();

            if (dynamicDb != null && staticDb != null && shipFactory != null)
            {
                startupProbeDone = true;
                nextAutoProbeTime = Time.unscaledTime + 30f;
                Logger.LogInfo("Game controllers found after " + startupProbeAttempts + " delayed probe attempt(s). Running read-only fleet dump.");
                DumpFleetState("startup");
                return;
            }

            if (startupProbeAttempts == 1 || startupProbeAttempts % 10 == 0)
            {
                Logger.LogInfo("Waiting for game controllers. dynamicDb=" + Present(dynamicDb) + ", staticDb=" + Present(staticDb) + ", shipFactory=" + Present(shipFactory));
            }

            if (startupProbeAttempts >= 60)
            {
                startupProbeDone = true;
                nextAutoProbeTime = Time.unscaledTime + 30f;
                Logger.LogWarning("Timed out waiting for game controllers. Load into a save/game session and press F8 to probe again.");
            }
        }

        private void RefreshReferences()
        {
            ResolveGameTypes();

            if (dynamicDb == null)
            {
                dynamicDb = FindGameObject(dynamicDbType);
            }

            if (staticDb == null)
            {
                staticDb = FindGameObject(staticDbType);
            }

            if (shipFactory == null)
            {
                shipFactory = FindGameObject(shipFactoryType);
            }

            if (getAllPlayerShipsMethod == null && dynamicDbType != null)
            {
                getAllPlayerShipsMethod = dynamicDbType.GetMethod("GetALLPlayerShips", Type.EmptyTypes);
            }
        }

        private void DumpFleetState(string reason)
        {
            RefreshReferences();
            if (dynamicDb == null || getAllPlayerShipsMethod == null)
            {
                Logger.LogWarning("Cannot dump fleet state: DynamicSQLiteController not ready. reason=" + reason);
                return;
            }

            try
            {
                IEnumerable ships = getAllPlayerShipsMethod.Invoke(dynamicDb, null) as IEnumerable;
                if (ships == null)
                {
                    Logger.LogInfo("Fleet dump (" + reason + "): GetALLPlayerShips returned null.");
                    return;
                }

                int count = 0;
                foreach (object ignored in ships)
                {
                    count++;
                }

                Logger.LogInfo("Fleet dump (" + reason + "): " + count + " ship(s) visible.");

                int index = 0;
                foreach (object ship in ships)
                {
                    if (ship == null)
                    {
                        Logger.LogInfo("  [" + index + "] <null ship>");
                        index++;
                        continue;
                    }

                    Logger.LogInfo(FormatShip(ship));
                    index++;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Fleet dump failed during " + reason + ": " + ex.GetType().Name + " - " + ex.Message);
            }
        }

        private string FormatShip(object ship)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("  Ship #").Append(FieldValue(ship, "PlayerShipID"))
                .Append(" player=").Append(FieldValue(ship, "PlayerID"))
                .Append(" name=\"").Append(FieldValue(ship, "Name")).Append("\"")
                .Append(" class=").Append(FieldValue(ship, "Class"))
                .Append(" freight=").Append(FieldValue(ship, "FreightType"))
                .Append(" status=").Append(FieldValue(ship, "Status"))
                .Append(" current=").Append(FieldValue(ship, "CurrentHarbor"))
                .Append(" dest=").Append(FieldValue(ship, "DestinationHarbor"))
                .Append(" fuel=").Append(FieldValue(ship, "FuelLoaded")).Append('/').Append(FieldValue(ship, "FuelCapacity"))
                .Append(" condition=").Append(FieldValue(ship, "Condition"))
                .Append(" routeStep=").Append(FieldValue(ship, "AutomateRouteStep"))
                .Append(" route=[")
                .Append(FieldValue(ship, "AutomateRouteStart")).Append(" -> ")
                .Append(FieldValue(ship, "AutomateRouteDestination1")).Append(" -> ")
                .Append(FieldValue(ship, "AutomateRouteDestination2")).Append(" -> ")
                .Append(FieldValue(ship, "AutomateRouteDestination3")).Append(" -> ")
                .Append(FieldValue(ship, "AutomateRouteDestination4")).Append(']');

            return sb.ToString();
        }

        private void ResolveGameTypes()
        {
            if (dynamicDbType != null && staticDbType != null && shipFactoryType != null)
            {
                return;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (dynamicDbType == null)
                {
                    dynamicDbType = assembly.GetType("Cargo.DynamicSQLiteController", false);
                }

                if (staticDbType == null)
                {
                    staticDbType = assembly.GetType("Cargo.StaticSQLiteController", false);
                }

                if (shipFactoryType == null)
                {
                    shipFactoryType = assembly.GetType("Cargo.ShipFactory", false);
                }
            }
        }

        private static UnityEngine.Object FindGameObject(Type type)
        {
            return type == null ? null : UnityEngine.Object.FindObjectOfType(type);
        }

        private static object FieldValue(object target, string fieldName)
        {
            if (target == null)
            {
                return "<null>";
            }

            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                return "<missing>";
            }

            object value = field.GetValue(target);
            return value == null ? "<null>" : value;
        }

        private static string Present(UnityEngine.Object obj)
        {
            return obj == null ? "missing" : "found";
        }
    }
}
