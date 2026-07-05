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
            Object.DontDestroyOnLoad(host);
            host.AddComponent<FleetAutomationSmokeBehaviour>();
        }
    }

    public sealed class FleetAutomationSmokeBehaviour : MonoBehaviour
    {
        private float nextLogTime;

        private void Awake()
        {
            Debug.Log("[TO2FA.Direct] Live MonoBehaviour attached. Press F8 in-game for a probe log.");
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                Debug.Log("[TO2FA.Direct] F8 probe received inside Unity update loop.");
            }

            if (Time.realtimeSinceStartup >= nextLogTime)
            {
                nextLogTime = Time.realtimeSinceStartup + 30f;
                Debug.Log("[TO2FA.Direct] Heartbeat from live direct mod.");
            }
        }
    }
}
