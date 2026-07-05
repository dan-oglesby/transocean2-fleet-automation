using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using Cargo;
using UnityEngine;

namespace TransOcean2FleetAutomation
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class FleetAutomationProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.daogl.transocean2.fleetautomationprobe";
        public const string PluginName = "TransOcean 2 Fleet Automation Probe";
        public const string PluginVersion = "0.1.0";

        private DynamicSQLiteController dynamicDb;
        private StaticSQLiteController staticDb;
        private ShipFactory shipFactory;
        private float nextAutoProbeTime;

        private void Awake()
        {
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded. Press F8 in-game to dump fleet automation state.");
            StartCoroutine(ProbeWhenReady());
        }

        private IEnumerator ProbeWhenReady()
        {
            for (int attempt = 1; attempt <= 60; attempt++)
            {
                RefreshReferences();
                if (dynamicDb != null && staticDb != null && shipFactory != null)
                {
                    Logger.LogInfo("Game controllers found after " + attempt + " probe attempt(s). Running read-only fleet dump.");
                    DumpFleetState("startup");
                    yield break;
                }

                if (attempt == 1 || attempt % 10 == 0)
                {
                    Logger.LogInfo("Waiting for game controllers. dynamicDb=" + Present(dynamicDb) + ", staticDb=" + Present(staticDb) + ", shipFactory=" + Present(shipFactory));
                }

                yield return new WaitForSeconds(1f);
            }

            Logger.LogWarning("Timed out waiting for game controllers. Load into a save/game session and press F8 to probe again.");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                DumpFleetState("F8");
            }

            if (Time.unscaledTime >= nextAutoProbeTime)
            {
                nextAutoProbeTime = Time.unscaledTime + 30f;
                RefreshReferences();
            }
        }

        private void RefreshReferences()
        {
            if (dynamicDb == null)
            {
                dynamicDb = FindObjectOfType<DynamicSQLiteController>();
            }

            if (staticDb == null)
            {
                staticDb = FindObjectOfType<StaticSQLiteController>();
            }

            if (shipFactory == null)
            {
                shipFactory = FindObjectOfType<ShipFactory>();
            }
        }

        private void DumpFleetState(string reason)
        {
            RefreshReferences();
            if (dynamicDb == null)
            {
                Logger.LogWarning("Cannot dump fleet state: DynamicSQLiteController not found. reason=" + reason);
                return;
            }

            try
            {
                List<DynamicTablePlayerShips> ships = dynamicDb.GetALLPlayerShips();
                if (ships == null)
                {
                    Logger.LogInfo("Fleet dump (" + reason + "): GetALLPlayerShips returned null.");
                    return;
                }

                Logger.LogInfo("Fleet dump (" + reason + "): " + ships.Count + " ship(s) visible.");
                for (int i = 0; i < ships.Count; i++)
                {
                    DynamicTablePlayerShips ship = ships[i];
                    if (ship == null)
                    {
                        Logger.LogInfo("  [" + i + "] <null ship>");
                        continue;
                    }

                    Logger.LogInfo(FormatShip(ship));
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Fleet dump failed during " + reason + ": " + ex.GetType().Name + " - " + ex.Message);
            }
        }

        private string FormatShip(DynamicTablePlayerShips ship)
        {
            StringBuilder sb = new StringBuilder(256);
            sb.Append("  Ship #").Append(ship.PlayerShipID)
                .Append(" player=").Append(ship.PlayerID)
                .Append(" name=\"").Append(ship.Name).Append("\"")
                .Append(" class=").Append(ship.Class)
                .Append(" freight=").Append(ship.FreightType)
                .Append(" status=").Append(ship.Status)
                .Append(" current=").Append(ship.CurrentHarbor)
                .Append(" dest=").Append(ship.DestinationHarbor)
                .Append(" fuel=").Append(ship.FuelLoaded).Append('/').Append(ship.FuelCapacity)
                .Append(" condition=").Append(ship.Condition)
                .Append(" routeStep=").Append(ship.AutomateRouteStep)
                .Append(" route=[")
                .Append(ship.AutomateRouteStart).Append(" -> ")
                .Append(ship.AutomateRouteDestination1).Append(" -> ")
                .Append(ship.AutomateRouteDestination2).Append(" -> ")
                .Append(ship.AutomateRouteDestination3).Append(" -> ")
                .Append(ship.AutomateRouteDestination4).Append(']');

            return sb.ToString();
        }

        private static string Present(UnityEngine.Object obj)
        {
            return obj == null ? "missing" : "found";
        }
    }
}
