// WindUnifier 骨架（0.4.0）—— 加载、配置、依赖探测；具体 patch 在 WindCore 中。
// 后续 patch 逐条加入（P1/P3/P4/P5/P7）。
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace WindUnifier
{
    public class Settings
    {
        public bool enabled = true;
        public float weight = 0.55f;          // KWP 气候注入权重（0..1）
        public float eveCloudGain = 0.25f;    // EVE 云柱增益（0=关）
        public float eveCloudRef = 3000f;     // 参考云柱厚度 m
        public float maxBackground = 60f;
        public float maxTotal = 250f;
        public bool debugHud = false;
        public bool onlyKerbin = true;   // KWP climate data is Kerbin-only; do not touch other bodies

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                var nodes = GameDatabase.Instance.GetConfigNodes("WINDUNIFIER_CONFIG");
                if (nodes == null || nodes.Length == 0)
                {
                    Debug.Log("[WindUnifier] no WINDUNIFIER_CONFIG, using defaults");
                    return s;
                }
                var n = nodes[0];
                ReadBool(n, "enabled", ref s.enabled);
                ReadFloat(n, "weight", ref s.weight);
                ReadFloat(n, "eveCloudGain", ref s.eveCloudGain);
                ReadFloat(n, "eveCloudRef", ref s.eveCloudRef);
                ReadFloat(n, "maxBackground", ref s.maxBackground);
                ReadFloat(n, "maxTotal", ref s.maxTotal);
                ReadBool(n, "debugHud", ref s.debugHud);
                ReadBool(n, "onlyKerbin", ref s.onlyKerbin);
                Debug.Log("[WindUnifier] config loaded");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WindUnifier] config load failed: " + e.Message);
            }
            return s;
        }

        private static void ReadFloat(ConfigNode n, string key, ref float v)
        {
            if (!n.HasValue(key)) return;
            float parsed;
            if (float.TryParse(n.GetValue(key), out parsed)) v = parsed;
        }

        private static void ReadBool(ConfigNode n, string key, ref bool v)
        {
            if (!n.HasValue(key)) return;
            bool parsed;
            if (bool.TryParse(n.GetValue(key), out parsed)) v = parsed;
        }
    }

    [KSPAddon(KSPAddon.Startup.EveryScene, true)]
    public class WindUnifierAddon : MonoBehaviour
    {
        public static WindUnifierAddon Instance;
        public static Settings Cfg = new Settings();

        private bool dumped;
        private float msgTimer;

        public void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public void Start()
        {
            Cfg = Settings.Load();
            WindCore.Init(this);
            Debug.Log("[WindUnifier] v0.4.0 started (NW -> KWP fallback, guarded FAR slot)");
        }

        public void OnDestroy()
        {
            WindCore.Shutdown();
        }

        public void Update()
        {
            if (Cfg.debugHud && HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
            {
                msgTimer += Time.deltaTime;
                if (msgTimer >= 1f)
                {
                    msgTimer = 0f;
                    ScreenMessages.PostScreenMessage("[WindUnifier] " + WindCore.Status, 1.1f, ScreenMessageStyle.UPPER_LEFT);
                }
            }

            if (dumped) return;
            if (!HighLogic.LoadedSceneIsFlight) return;
            dumped = true;
            DumpDependencies();
        }

        private static void DumpDependencies()
        {
            var found = new List<string>();
            foreach (var la in AssemblyLoader.loadedAssemblies)
            {
                switch (la.name)
                {
                    case "KerbalWeatherProject": found.Add("KWP " + la.assembly.GetName().Version); break;
                    case "NaturalWeather": found.Add("NaturalWeather " + la.assembly.GetName().Version); break;
                    case "NaturesFury": found.Add("NaturesFury " + la.assembly.GetName().Version); break;
                    case "FerramAerospaceResearch": found.Add("FAR " + la.assembly.GetName().Version); break;
                    case "KerbalWind": found.Add("KerbalWind " + la.assembly.GetName().Version); break;
                }
            }
            Debug.Log("[WindUnifier] dependencies: " + (found.Count == 0 ? "none" : string.Join(", ", found.ToArray())));
        }
    }
}
