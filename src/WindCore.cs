// WindUnifier core (0.4.0): single FAR wind authority, NW -> KWP fallback,
// guarded slot handover. All third-party types are resolved by reflection so
// the plugin still loads when any of them is missing.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WindUnifier
{
    internal static class WindCore
    {
        private static Settings Cfg { get { return WindUnifierAddon.Cfg; } }

        public static bool Active;
        public static string Status = "init";

        private static Harmony harmony;
        private static bool slotGuard;
        private static bool patched;
        private static int patchOk;
        private static int patchFail;
        private static bool loggedOk;
        private static bool cityInjected;

        private static Type farAtm;
        private static MethodInfo farSetWind;
        private static FieldInfo farWindField;

        private static Type ovType;
        private static FieldInfo ovCity;
        private static FieldInfo ovUseCity;
        private static Type cityType;

        private static Type kwpClim;
        private static MethodInfo kwpGetWind;
        private static MethodInfo kwpGetWx2d;
        private static MethodInfo kwpWx3dClimo;
        private static MethodInfo kwpWx3dPoint;

        // 0.4.0 wind-source fallback: KWP's public climate API (preferred) and the
        // legacy climate_data entry points kept for older KWP builds.
        private static Type kwpClimateApi;
        private static MethodInfo kwpApiU;
        private static MethodInfo kwpApiV;
        private static MethodInfo kwpApiW;
        private static MethodInfo kwpClimU2;
        private static MethodInfo kwpClimV2;
        private static MethodInfo kwpClimW2;
        private static bool kwpClimateWarned;

        // FAR slot handover: remember what was installed before the first takeover.
        private static Delegate originalWindDelegate;
        private static bool slotCaptured;

        private static MethodInfo nwBridgeWind;
        private static Type nfWeather;
        private static FieldInfo nfCoverField;
        private static FieldInfo nfRainField;
        private static PropertyInfo nfFreshProp;

        /// <summary>Nature's Fury 当前风速模长（<c>KSPTornadoWeather.WindNow</c>，m/s，含湍流/涡旋）。</summary>
        private static FieldInfo nfWindNowField;

        /// <summary>Nature's Fury 世界空间水平单位风向（<c>KSPTornadoWeather.WindDirWorld</c>，字段或属性二选一）。</summary>
        private static FieldInfo nfWindDirField;
        private static PropertyInfo nfWindDirProp;
        private static Type nwWeather;
        private static FieldInfo nwOn;

        private static MethodInfo evGetList;
        private static bool cloudBroken;
        private static int cloudFail;
        private static float cloudGain = 1f;
        private static float cloudStamp = -1f;

        private static float lastCloudCover;
        private static float lastEveCover;
        private static float lastPrecip;
        private static float lastHum = -1f;
        private static double lastKwpHum = -1;
        private static float cityStamp = -1f;
        private static float cityFailStamp = -1f;
        private static Vector3d cityPos;

        // ---------------------------------------------------------------- init

        public static void Shutdown()
        {
            Active = false;
            RestoreSlot();
            slotCaptured = false;
            originalWindDelegate = null;
        }

        public static void Init(MonoBehaviour host)
        {
            try
            {
                Resolve();
                Active = true;
                Patch();
                if (Cfg.enabled && HasWindSource())
                {
                    if (InFlight) ForceSlot();
                }
                else
                {
                    // Never leave a stale takeover behind when the plugin is off
                    // or when no aerodynamic wind source is installed.
                    RestoreSlot();
                }
                host.StartCoroutine(Loop());
                Debug.Log("[WindUnifier] init: sources nw=" + (nwBridgeWind != null)
                          + " kwp=" + KwpSourceName()
                          + " far=" + (farSetWind != null)
                          + " nf=" + (nfWeather != null)
                          + " eve=" + (evGetList != null)
                          + " slot=" + SlotState()
                          + " patches=" + patchOk + "/" + (patchOk + patchFail));
            }
            catch (Exception e)
            {
                Active = false;
                Debug.LogError("[WindUnifier] init failed, plugin disabled: " + e);
            }
        }

        /// <summary>Human-readable source summary for the init log / public API.</summary>
        private static string KwpSourceName()
        {
            if (kwpApiU != null && kwpApiV != null && kwpApiW != null) return "climate_api";
            if (kwpGetWind != null) return "getWind";
            if (kwpClimU2 != null && kwpClimV2 != null && kwpClimW2 != null) return "uwind2";
            return "none";
        }

        private static string SlotState()
        {
            return SlotName() == "WindCore.UnifiedWind" ? "unified" : "original";
        }

        /// <summary>At least one aerodynamic wind source exists (NW or KWP).</summary>
        private static bool HasWindSource()
        {
            return nwBridgeWind != null || KwpClimateAvailable;
        }

        private static bool KwpClimateAvailable
        {
            get
            {
                return (kwpApiU != null && kwpApiV != null && kwpApiW != null)
                       || kwpGetWind != null
                       || (kwpClimU2 != null && kwpClimV2 != null && kwpClimW2 != null);
            }
        }

        private static bool InFlight
        {
            get { return HighLogic.LoadedScene == GameScenes.FLIGHT; }
        }

        private static void Resolve()
        {
            farAtm = FindType("FerramAerospaceResearch.FARAtmosphere");
            if (farAtm != null)
            {
                farSetWind = farAtm.GetMethod("SetWindFunction", BindingFlags.Public | BindingFlags.Static);
                farWindField = farAtm.GetField("windFunction", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            }

            ovType = FindType("NaturalWeather.Overrides");
            cityType = FindType("NaturalWeather.CityWeather");
            if (ovType != null)
            {
                ovCity = ovType.GetField("City", BindingFlags.Public | BindingFlags.Static);
                ovUseCity = ovType.GetField("UseCity", BindingFlags.Public | BindingFlags.Static);
            }

            // KWP 2.x public API: climate_api.uwind/vwind/zwind(lat, lon, alt, ut),
            // lat/lon in degrees, altitude in metres, u=east, v=north, w=up.
            kwpClimateApi = FindType("KerbalWeatherProject.climate_api");
            if (kwpClimateApi != null)
            {
                Type[] four = { typeof(double), typeof(double), typeof(double), typeof(double) };
                kwpApiU = kwpClimateApi.GetMethod("uwind", four);
                kwpApiV = kwpClimateApi.GetMethod("vwind", four);
                kwpApiW = kwpClimateApi.GetMethod("zwind", four);
            }

            kwpClim = FindType("KerbalWeatherProject.climate_data");
            if (kwpClim != null)
            {
                kwpGetWind = kwpClim.GetMethod("getWind", new[] { typeof(double), typeof(double), typeof(double) });
                kwpGetWx2d = kwpClim.GetMethod("getAmbientWx2D", new[] { typeof(double), typeof(double) });

                // Last-resort KWP fallback: climate_data.uwind(alt, ut) / vwind / zwind.
                Type[] two = { typeof(double), typeof(double) };
                kwpClimU2 = kwpClim.GetMethod("uwind", two);
                kwpClimV2 = kwpClim.GetMethod("vwind", two);
                kwpClimW2 = kwpClim.GetMethod("zwind", two);
            }

            Type kwpClimoType = FindType("KerbalWeatherProject.KerbalWxClimo");
            if (kwpClimoType != null) kwpWx3dClimo = kwpClimoType.GetMethod("getWx3D", Type.EmptyTypes);
            Type kwpPointType = FindType("KerbalWeatherProject.KerbalWxPoint");
            if (kwpPointType != null) kwpWx3dPoint = kwpPointType.GetMethod("getWx3D", Type.EmptyTypes);

            Type bridge = FindType("NaturalWeather.Bridge");
            if (bridge != null) nwBridgeWind = bridge.GetMethod("Wind", new[] { typeof(Vessel) });

            nfWeather = FindType("KSPTornadoWeather") ?? FindType("NaturesFury.KSPTornadoWeather");
            if (nfWeather != null)
            {
                nfCoverField = nfWeather.GetField("CloudCover", BindingFlags.Public | BindingFlags.Static);
                nfRainField = nfWeather.GetField("Rain", BindingFlags.Public | BindingFlags.Static);
                nfFreshProp = nfWeather.GetProperty("Fresh", BindingFlags.Public | BindingFlags.Static);
                nfWindNowField = nfWeather.GetField("WindNow", BindingFlags.Public | BindingFlags.Static);
                nfWindDirField = nfWeather.GetField("WindDirWorld", BindingFlags.Public | BindingFlags.Static);
                nfWindDirProp = nfWeather.GetProperty("WindDirWorld", BindingFlags.Public | BindingFlags.Static);
            }

            nwWeather = FindType("NaturalWeather.Weather");
            if (nwWeather != null) nwOn = nwWeather.GetField("On", BindingFlags.Public | BindingFlags.Static);

            ResolveEve();
        }

        private static float lastEveTry = -1f;

        private static void ResolveEve()
        {
            if (evGetList != null) return;
            Type evMgr = FindType("Atmosphere.CloudsManager");
            if (evMgr == null) return;
            evGetList = evMgr.GetMethod("GetObjectList", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (evGetList == null && evMgr.BaseType != null)
                evGetList = evMgr.BaseType.GetMethod("GetObjectList", BindingFlags.Public | BindingFlags.Static);
            if (evGetList != null) Debug.Log("[WindUnifier] EVE clouds bound: " + evGetList.DeclaringType.Name);
        }

        private static void Patch()
        {
            if (patched) return;
            patched = true;
            harmony = new Harmony("windunifier.core");

            if (farSetWind != null)
                SafePatch(farSetWind, null, Hm("SetWindPostfix"));

            if (ovType != null)
            {
                MethodInfo cw = ovType.GetMethod("CityWeight", BindingFlags.NonPublic | BindingFlags.Static);
                if (cw != null) SafePatch(cw, Hm("CityWeightPrefix"), null);
            }

            if (kwpWx3dClimo != null) SafePatch(kwpWx3dClimo, null, Hm("Wx3DPostfix"));
            if (kwpWx3dPoint != null) SafePatch(kwpWx3dPoint, null, Hm("Wx3DPostfix"));

            foreach (string t in new[] { "KerbalWeatherProject.KerbalWxClimo", "KerbalWeatherProject.KerbalWxPoint" })
            {
                Type ty = FindType(t);
                if (ty == null) continue;
                MethodInfo m = ty.GetMethod("getWx2D", Type.EmptyTypes);
                if (m != null) SafePatch(m, null, Hm("Wx2DPostfix"));
            }

            foreach (string t in new[] { "KerbalWeatherProject.KerbalWxClimo", "KerbalWeatherProject.KerbalWxPoint" })
            {
                Type ty = FindType(t);
                if (ty == null) continue;
                MethodInfo m = ty.GetMethod("GetTheWind", BindingFlags.Public | BindingFlags.Instance);
                if (m != null) SafePatch(m, null, Hm("KwpWindPostfix"));
            }
        }

        private static void SafePatch(MethodInfo target, HarmonyMethod prefix, HarmonyMethod postfix)
        {
            try
            {
                harmony.Patch(target, prefix: prefix, postfix: postfix);
                patchOk++;
            }
            catch (Exception e)
            {
                patchFail++;
                Debug.LogError("[WindUnifier] patch failed on " + target.DeclaringType.Name + "." + target.Name + ": " + e.Message);
            }
        }

        private static HarmonyMethod Hm(string name)
        {
            return new HarmonyMethod(typeof(WindCore).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        // ------------------------------------------------------------- patches

        // P1: KWP/climate weight for every point (the original only covers New York).
        private static bool CityWeightPrefix(ref float __result)
        {
            if (!Active || !Cfg.enabled || !cityInjected || ovCity == null || ovCity.GetValue(null) == null) return true;
            __result = Mathf.Clamp01(Cfg.weight);
            return false;
        }

        // P5: whatever KWP/NW sets, force the slot back to the unified function.
        // Never take the slot when the plugin is off or when no wind source exists:
        // otherwise we would silently replace another mod's wind with zeros.
        private static void SetWindPostfix()
        {
            if (!Active || slotGuard) return;
            if (!Cfg.enabled || !HasWindSource()) return;
            ForceSlot();
        }

        // P3: KWP's own FAR provider returns the same unified wind.
        private static void KwpWindPostfix(ref Vector3 __result)
        {
            if (!Active || !Cfg.enabled) return;
            if (!NwReady()) return;   // NW not initialised: keep KWP's own wind
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) return;
            __result = UnifiedWind(v.mainBody, null, (Vector3)v.CoMD);
        }

        // P4: KWP's own panel reads getWx3D(); rewrite its contents so the GUI shows the unified wind.
        private static void Wx3DPostfix(ref List<double> __result)
        {
            if (!Active || !Cfg.enabled || __result == null || __result.Count < 3) return;
            if (!NwReady()) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null) return;
            Vector3 w = UnifiedWind(v.mainBody, null, (Vector3)v.CoMD);
            Vector3 east = new Vector3((float)v.east.x, (float)v.east.y, (float)v.east.z);
            Vector3 north = new Vector3((float)v.north.x, (float)v.north.y, (float)v.north.z);
            Vector3 up = new Vector3((float)v.upAxis.x, (float)v.upAxis.y, (float)v.upAxis.z);
            __result[0] = Vector3.Dot(w, north);
            __result[1] = Vector3.Dot(w, east);
            __result[2] = Vector3.Dot(w, up);
            if (__result.Count > 5) __result[5] = Humidity(__result[5]);
        }

        // P4b: humidity follows cloud/rain (NW/NF/EVE), so flying through cloud reads near 100%.
        private static void Wx2DPostfix(ref List<double> __result)
        {
            if (!Active || !Cfg.enabled || __result == null || __result.Count < 7) return;
            __result[6] = Humidity(__result[6]);
        }

        private static float Humidity(double kwpHum)
        {
            // climatological cover is an area average: discount it so clear ground does not read 90%+
            float cover = Mathf.Max(lastEveCover, NfCover());
            cover = Mathf.Max(cover, lastCloudCover * 0.6f);
            float rain = NfRain();
            if (lastPrecip > 1f) rain = Mathf.Max(rain, 1f);

            float target;
            if (cover > 0.5f) target = Mathf.Lerp(90f, 100f, (cover - 0.5f) * 2f);
            else target = 100f * cover;
            if (rain > 0.1f) target = 100f;

            lastKwpHum = kwpHum;
            float hum = Mathf.Max((float)kwpHum, target);
            hum = Mathf.Clamp(hum, 0f, 99.5f);
            lastHum = hum;
            return hum;
        }

        private static float NfCover()
        {
            return NfField(nfCoverField);
        }

        private static float NfRain()
        {
            return NfField(nfRainField);
        }

        private static float NfField(FieldInfo f)
        {
            if (f == null || nfFreshProp == null) return 0f;
            try
            {
                if (!(bool)nfFreshProp.GetValue(null, null)) return 0f;
                object raw = f.GetValue(null);
                return Mathf.Clamp01(raw == null ? 0f : Convert.ToSingle(raw));
            }
            catch { return 0f; }
        }

        private static bool NwReady()
        {
            return nwOn != null && nwOn.GetValue(null) != null;
        }

        // -------------------------------------------------------------- wind

        /// <summary>
        /// Aerodynamic unified wind. Source chain (0.4.0):
        /// <list type="number">
        /// <item>NaturalWeather's local weather (dynamic, preferred).</item>
        /// <item>KWP climatological wind (fallback when NW is absent/zero).</item>
        /// <item>Zero — Nature's Fury is deliberately NOT mixed in here; NF applies its
        /// own tornado forces through <c>windPhysics</c>, so adding it to FAR would double-count.</item>
        /// </list>
        /// A zero result is returned only when no source produces wind; the FAR slot is
        /// never forced to this function unless <see cref="HasWindSource"/> is true.
        /// </summary>
        public static Vector3 UnifiedWind(CelestialBody body, Part part, Vector3 position)
        {
            if (!Active || !Cfg.enabled) return Vector3.zero;
            Vessel v = part != null ? part.vessel : FlightGlobals.ActiveVessel;
            Vector3 w = NwWind(v);
            if (w.sqrMagnitude < 1e-6f) w = KwpClimateWind(body, position);
            if (w.sqrMagnitude < 1e-6f) return Vector3.zero;

            float gain = CloudGain(position);
            w *= gain;

            float cap = Mathf.Max(1f, Cfg.maxBackground);
            float total = Mathf.Max(cap, Cfg.maxTotal);
            float mag = w.magnitude;
            if (mag > cap) w *= cap / mag;
            mag = w.magnitude;
            if (mag > total) w *= total / mag;
            return w;
        }

        /// <summary>
        /// KWP climatological wind at a world position, converted from the
        /// meteorological ENU frame (u east, v north, w up) to KSP world space.
        /// Returns zero when KWP is absent, its data is not ready, or anything throws.
        /// Never logs more than once per session.
        /// </summary>
        private static Vector3 KwpClimateWind(CelestialBody body, Vector3 position)
        {
            if (!KwpClimateAvailable) return Vector3.zero;

            try
            {
                if (body == null)
                {
                    Vessel active = FlightGlobals.ActiveVessel;
                    body = active != null ? active.mainBody : null;
                }
                if (body == null) return Vector3.zero;

                // KWP ships Kerbin climate data only; querying another body would
                // return Kerbin values at the wrong place.
                if (Cfg.onlyKerbin && !string.Equals(body.bodyName, "Kerbin", StringComparison.Ordinal))
                    return Vector3.zero;

                double latRad, lonRad, alt;
                body.GetLatLonAlt((Vector3d)position, out latRad, out lonRad, out alt);

                double latDeg = latRad * 180.0 / Math.PI;
                double lonDeg = lonRad * 180.0 / Math.PI;
                double ut = Planetarium.GetUniversalTime();

                double u = 0.0, v = 0.0, wUp = 0.0;
                bool got = false;

                if (kwpApiU != null && kwpApiV != null && kwpApiW != null)
                {
                    u = Convert.ToDouble(kwpApiU.Invoke(null, new object[] { latDeg, lonDeg, alt, ut }));
                    v = Convert.ToDouble(kwpApiV.Invoke(null, new object[] { latDeg, lonDeg, alt, ut }));
                    wUp = Convert.ToDouble(kwpApiW.Invoke(null, new object[] { latDeg, lonDeg, alt, ut }));
                    got = true;
                }
                else if (kwpGetWind != null)
                {
                    // Legacy KWP: wx_wind { uwind, vwind, zwind }.
                    object wx = kwpGetWind.Invoke(null, new object[] { latDeg, lonDeg, alt });
                    if (wx != null)
                    {
                        u = ReadNumber(wx, "u", "uwind", "windU");
                        v = ReadNumber(wx, "v", "vwind", "windV");
                        wUp = ReadNumber(wx, "w", "zwind", "windW");
                        got = true;
                    }
                }
                else if (kwpClimU2 != null && kwpClimV2 != null && kwpClimW2 != null)
                {
                    u = Convert.ToDouble(kwpClimU2.Invoke(null, new object[] { alt, ut }));
                    v = Convert.ToDouble(kwpClimV2.Invoke(null, new object[] { alt, ut }));
                    wUp = Convert.ToDouble(kwpClimW2.Invoke(null, new object[] { alt, ut }));
                    got = true;
                }

                if (!got) return Vector3.zero;
                if (double.IsNaN(u) || double.IsInfinity(u)
                    || double.IsNaN(v) || double.IsInfinity(v)
                    || double.IsNaN(wUp) || double.IsInfinity(wUp))
                    return Vector3.zero;

                Vector3 east, north, up;
                EnuBasis(body, latRad, lonRad, out east, out north, out up);
                return east * (float)u + north * (float)v + up * (float)wUp;
            }
            catch (Exception e)
            {
                if (!kwpClimateWarned)
                {
                    kwpClimateWarned = true;
                    Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                    Debug.LogWarning("[WindUnifier] KWP climate wind unavailable (returning zero): "
                                     + inner.GetType().Name + ": " + inner.Message);
                }
                return Vector3.zero;
            }
        }

        /// <summary>
        /// Local east/north/up unit vectors at a point, from the surface-position
        /// gradient (the same finite-difference trick used by the old NorthEast helper).
        /// </summary>
        private static void EnuBasis(CelestialBody body, double latRad, double lonRad,
                                     out Vector3 east, out Vector3 north, out Vector3 up)
        {
            const double step = 1e-4; // radians, roughly 0.6 km on Kerbin

            Vector3d p0 = body.GetWorldSurfacePosition(latRad, lonRad, 0.0);
            Vector3d pEast = body.GetWorldSurfacePosition(latRad, lonRad + step, 0.0);
            Vector3d pNorth = body.GetWorldSurfacePosition(latRad + step, lonRad, 0.0);
            Vector3d upV = body.GetSurfaceNVector(latRad, lonRad);

            Vector3d eV = pEast - p0;
            Vector3d nV = pNorth - p0;
            eV -= upV * Vector3d.Dot(eV, upV);
            nV -= upV * Vector3d.Dot(nV, upV);

            if (eV.sqrMagnitude > 1e-12)
            {
                eV = eV.normalized;
                if (nV.sqrMagnitude <= 1e-12) nV = Vector3d.Cross(upV, eV);
                nV = nV.normalized;
            }
            else
            {
                // Polar degeneracy: no stable east direction.
                eV = Vector3d.zero;
                nV = Vector3d.zero;
            }

            east = new Vector3((float)eV.x, (float)eV.y, (float)eV.z);
            north = new Vector3((float)nV.x, (float)nV.y, (float)nV.z);
            up = new Vector3((float)upV.x, (float)upV.y, (float)upV.z);
        }

        /// <summary>
        /// 视觉风（烟雾等纯视觉消费者用）：气动统一风与 Nature's Fury 局部风**取模长更大者**，不叠加。
        /// 气动侧（FAR 风槽）仍指向 <see cref="UnifiedWind"/>，不受 NF 影响，避免与 NF 自己的
        /// <c>windPhysics</c> AddForce 双算。
        /// </summary>
        public static Vector3 VisualWind(CelestialBody body, Part part, Vector3 position)
        {
            Vector3 baseWind = UnifiedWind(body, part, position);
            Vector3 nfWind = NfWind();
            return nfWind.sqrMagnitude > baseWind.sqrMagnitude ? nfWind : baseWind;
        }

        /// <summary>Nature's Fury 当前局地风（<c>WindDirWorld * WindNow</c>，世界 m/s）；不可用时零。</summary>
        private static Vector3 NfWind()
        {
            if (nfWindNowField == null || nfFreshProp == null)
            {
                return Vector3.zero;
            }

            try
            {
                if (!(bool)nfFreshProp.GetValue(null, null))
                {
                    return Vector3.zero;
                }

                float speed = Convert.ToSingle(nfWindNowField.GetValue(null));
                object dirRaw = nfWindDirField != null
                    ? nfWindDirField.GetValue(null)
                    : (nfWindDirProp != null ? nfWindDirProp.GetValue(null, null) : null);

                if (!(dirRaw is Vector3))
                {
                    return Vector3.zero;
                }

                Vector3 dir = (Vector3)dirRaw;
                if (speed <= 0f || dir.sqrMagnitude < 1e-6f)
                {
                    return Vector3.zero;
                }

                return dir.normalized * speed;
            }
            catch
            {
                return Vector3.zero;
            }
        }

        private static Vector3 NwWind(Vessel v)
        {
            if (nwBridgeWind == null || v == null) return Vector3.zero;
            try
            {
                object r = nwBridgeWind.Invoke(null, new object[] { v });
                if (r == null) return Vector3.zero;
                Vector3d d = (Vector3d)r;
                return new Vector3((float)d.x, (float)d.y, (float)d.z);
            }
            catch
            {
                return Vector3.zero;
            }
        }

        private static void ForceSlot()
        {
            if (farSetWind == null || slotGuard) return;
            if (!Active || !Cfg.enabled || !HasWindSource()) return;

            if (!slotCaptured) CaptureSlot();

            slotGuard = true;
            try
            {
                Type delType = farSetWind.GetParameters()[0].ParameterType;
                MethodInfo m = typeof(WindCore).GetMethod("UnifiedWind", BindingFlags.Public | BindingFlags.Static);
                Delegate d = Delegate.CreateDelegate(delType, m);
                farSetWind.Invoke(null, new object[] { d });
            }
            catch (Exception e)
            {
                if (Status != "slot-error") { Status = "slot-error"; Debug.LogWarning("[WindUnifier] ForceSlot: " + e.Message); }
            }
            finally
            {
                slotGuard = false;
            }
        }

        /// <summary>Remember the delegate installed before our first takeover.</summary>
        private static void CaptureSlot()
        {
            slotCaptured = true;
            originalWindDelegate = null;
            try
            {
                if (farWindField == null) return;
                object disp = farWindField.GetValue(null);
                if (disp == null) return;
                PropertyInfo p = disp.GetType().GetProperty("Function");
                Delegate fn = p == null ? null : p.GetValue(disp, null) as Delegate;
                if (fn != null && fn.Method.DeclaringType == typeof(WindCore)) fn = null;
                originalWindDelegate = fn;
                Debug.Log("[WindUnifier] slot original captured: "
                          + (fn == null ? "null" : fn.Method.DeclaringType.Name + "." + fn.Method.Name));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WindUnifier] could not capture original wind delegate: " + e.Message);
            }
        }

        /// <summary>
        /// Put the pre-takeover delegate back (or null = FAR's built-in zero wind).
        /// Called when the plugin is disabled and on shutdown; repeated calls are safe.
        /// </summary>
        private static void RestoreSlot()
        {
            if (!slotCaptured || farSetWind == null) return;
            slotGuard = true;
            try
            {
                farSetWind.Invoke(null, new object[] { originalWindDelegate });
                Debug.Log("[WindUnifier] slot restored: "
                          + (originalWindDelegate == null
                              ? "FAR default"
                              : originalWindDelegate.Method.DeclaringType.Name + "." + originalWindDelegate.Method.Name));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WindUnifier] RestoreSlot: " + e.Message);
            }
            finally
            {
                slotGuard = false;
            }
        }

        private static void CheckSlot()
        {
            if (!Active || !Cfg.enabled || !HasWindSource()) return;
            if (farWindField == null) return;
            try
            {
                object disp = farWindField.GetValue(null);
                if (disp == null) return;
                PropertyInfo p = disp.GetType().GetProperty("Function");
                Delegate fn = p == null ? null : p.GetValue(disp, null) as Delegate;
                if (fn == null || fn.Method.DeclaringType != typeof(WindCore)) ForceSlot();
            }
            catch { }
        }

        // ------------------------------------------------------- EVE cloud gain

        private static float CloudGain(Vector3 pos)
        {
            if (Cfg.eveCloudGain <= 0f || cloudBroken) return 1f;
            float now = Time.realtimeSinceStartup;
            if (now - cloudStamp < 1f) return cloudGain;
            cloudStamp = now;
            cloudGain = cloudBroken
                ? 1f + Cfg.eveCloudGain * Mathf.Clamp01(lastCloudCover)   // fallback: KWP climatological cloud cover
                : ComputeCloudGain(pos);
            return cloudGain;
        }

        // EVE cloud textures are GPU-only (isReadable=false), so SampleCoverage always throws.
        // Use the read-only layer altitude band + KWP climatological cloud cover instead.
        private static float ComputeCloudGain(Vector3 pos)
        {
            if (evGetList == null) return 1f + Cfg.eveCloudGain * Mathf.Clamp01(lastCloudCover);
            try
            {
                Vessel v = FlightGlobals.ActiveVessel;
                if (v == null || v.mainBody == null) return 1f;

                object list = evGetList.Invoke(null, null);
                IEnumerable en = list as IEnumerable;
                if (en == null) return 1f;

                bool inBand = false;
                foreach (object layer in en)
                {
                    if (layer == null) continue;
                    Type lt = layer.GetType();
                    PropertyInfo bodyP = lt.GetProperty("Body");
                    if (bodyP != null && !string.Equals((string)bodyP.GetValue(layer, null), v.mainBody.bodyName, StringComparison.Ordinal)) continue;

                    PropertyInfo volP = lt.GetProperty("LayerRaymarchedVolume");
                    object vol = volP == null ? null : volP.GetValue(layer, null);
                    if (vol == null) continue;

                    Type vt = vol.GetType();
                    float inner = ReadF(vt, vol, "InnerSphereRadius", 0f);
                    float outer = ReadF(vt, vol, "OuterSphereRadius", 0f);
                    float planet = ReadF(vt, vol, "PlanetRadius", 0f);
                    if (planet <= 0f || outer <= inner) continue;

                    double altInner = inner - planet;
                    double altOuter = outer - planet;
                    if (v.altitude >= altInner && v.altitude <= altOuter)
                    {
                        inBand = true;
                        break;
                    }
                }

                lastEveCover = inBand ? 1f : 0f;
                cloudFail = 0;
                float cov = Mathf.Max(lastCloudCover, lastEveCover);
                return 1f + Cfg.eveCloudGain * Mathf.Clamp01(cov);
            }
            catch (Exception e)
            {
                cloudFail++;
                if (cloudFail >= 5 && !cloudBroken)
                {
                    cloudBroken = true;
                    Exception inner = e.InnerException != null ? e.InnerException : e;
                    Debug.LogWarning("[WindUnifier] EVE layer scan disabled after 5 failures: " + inner.GetType().Name + ": " + inner.Message);
                }
                return 1f;
            }
        }
        private static Vector3 sphereCenter(object layer, Type lt, Vessel v)
        {
            PropertyInfo pt = lt.GetProperty("ParentTransform");
            Transform tr = pt == null ? null : pt.GetValue(layer, null) as Transform;
            if (tr != null) return tr.position;
            return v.mainBody.transform.position;
        }

        private static float ReadF(Type t, object obj, string prop, float fallback)
        {
            PropertyInfo p = t.GetProperty(prop);
            if (p == null) return fallback;
            object raw = p.GetValue(obj, null);
            return raw is float ? (float)raw : fallback;
        }

        // ------------------------------------------------------ climate -> NW

        private static IEnumerator Loop()
        {
            yield return new WaitForSeconds(1f);
            while (true)
            {
                try
                {
                    if (evGetList == null && Time.realtimeSinceStartup - lastEveTry > 60f)
                    {
                        lastEveTry = Time.realtimeSinceStartup;
                        ResolveEve();
                    }
                    if (InFlight)
                    {
                        CheckSlot();
                        TryRefreshCity(false);
                    }
                    LogState();
                }
                catch (Exception e)
                {
                    Status = "loop-error";
                    Debug.LogWarning("[WindUnifier] loop: " + e.Message);
                }
                yield return new WaitForSeconds(1f);
            }
        }

        private static float stateStamp;

        private static void LogState()
        {
            if (Time.realtimeSinceStartup - stateStamp < 60f) return;
            stateStamp = Time.realtimeSinceStartup;
            try
            {
                // HighLogic/FlightGlobals are not reliable during scene loading; only log in flight.
                if (!HighLogic.LoadedSceneIsFlight) return;
                Vessel v = FlightGlobals.ActiveVessel;
                string vesselName = "-", body = "-", alt = "-";
                if (v != null)
                {
                    vesselName = v.vesselName;
                    if (v.mainBody != null)
                    {
                        body = v.mainBody.bodyName;
                        alt = v.altitude.ToString("F0");
                    }
                }
                Debug.Log("[WindUnifier] state: scene=" + HighLogic.LoadedScene
                          + " slot=" + SlotName()
                          + " city=" + cityInjected
                          + " status=" + Status
                          + " eveGain=" + cloudGain.ToString("F2")
                          + " hum=" + lastHum.ToString("F1") + "(kwp=" + lastKwpHum.ToString("F1") + " cover=" + lastCloudCover.ToString("F2") + " eve=" + lastEveCover.ToString("F2") + " nf=" + NfCover().ToString("F2") + " rain=" + NfRain().ToString("F2") + ")"
                          + " vessel=" + vesselName + " body=" + body + " alt=" + alt);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[WindUnifier] state log failed: " + e.Message);
            }
        }

        private static string SlotName()
        {
            if (farWindField == null) return "no-far";
            try
            {
                object disp = farWindField.GetValue(null);
                if (disp == null) return "null-dispatch";
                PropertyInfo p = disp.GetType().GetProperty("Function");
                Delegate fn = p == null ? null : p.GetValue(disp, null) as Delegate;
                if (fn == null) return "null";
                return fn.Method.DeclaringType.Name + "." + fn.Method.Name;
            }
            catch { return "err"; }
        }
        private static void TryRefreshCity(bool force)
        {
            if (!Active || !Cfg.enabled) return;
            if (ovCity == null || cityType == null || kwpGetWind == null || kwpGetWx2d == null) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || v.mainBody == null || !v.mainBody.atmosphere) return;

            // KWP ships Kerbin climate data only; injecting zeros on other bodies would halve NW wind.
            if (Cfg.onlyKerbin && !string.Equals(v.mainBody.bodyName, "Kerbin", StringComparison.Ordinal))
            {
                cityInjected = false;
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (!force && cityFailStamp > 0f && now - cityFailStamp < 60f) return;
            if (!force && cityInjected && now - cityStamp < 30f && (v.CoMD - cityPos).magnitude < 10000.0) return;

            try
            {
                double lat = v.latitude, lon = v.longitude, alt = v.altitude;

                object windObj = kwpGetWind.Invoke(null, new object[] { lat, lon, alt });
                double u = GetD(windObj, "uwind");
                double vv = GetD(windObj, "vwind");
                float speed = (float)Math.Sqrt(u * u + vv * vv);

                double deg = Math.Atan2(-u, -vv) * 180.0 / Math.PI;
                if (deg < 0) deg += 360.0;

                object srf = kwpGetWx2d.Invoke(null, new object[] { lat, lon });
                float cloud = (float)GetD(srf, "cloudcover");
                lastCloudCover = Mathf.Clamp01(cloud);
                float precip = (float)GetD(srf, "precipitation_rate");
                lastPrecip = precip;
                float tempK = (float)GetD(srf, "temperature");

                object city = Activator.CreateInstance(cityType, true);
                SetF(city, "WindSpeed", speed);
                SetF(city, "WindFrom", (float)deg);
                SetF(city, "WindGusts", speed * 1.3f);
                SetF(city, "CloudLow", cloud);
                SetF(city, "CloudMid", cloud * 0.6f);
                SetF(city, "CloudHigh", cloud * 0.3f);
                SetF(city, "Precipitation", precip);
                SetF(city, "Snowfall", tempK < 273.15f ? precip * 0.5f : 0f);
                SetF(city, "Temperature", tempK - 273.15f);
                SetF(city, "Visibility", 20000f);
                SetI(city, "Code", CodeFor(cloud, precip, tempK - 273.15f));
                SetD(city, "Latitude", lat);
                SetD(city, "Longitude", lon);

                ovCity.SetValue(null, city);
                if (ovUseCity != null) ovUseCity.SetValue(null, true);
                cityInjected = true;
                cityStamp = now;
                cityPos = v.CoMD;
                Status = "ok " + speed.ToString("F1") + "m/s " + deg.ToString("F0") + "deg";
                if (!loggedOk) { loggedOk = true; Debug.Log("[WindUnifier] climate injection ok: " + Status + " @" + v.mainBody.bodyName); }
            }
            catch (Exception e)
            {
                cityInjected = false;
                cityFailStamp = now;
                Status = "city-error";
                Debug.LogWarning("[WindUnifier] climate injection failed: " + e.Message);
            }
        }

        private static int CodeFor(float cloud, float precip, float tempC)
        {
            if (precip > 0.05f)
            {
                if (tempC < 0f) return 71;
                if (precip > 4f) return 65;
                if (precip > 1f) return 63;
                return 61;
            }
            if (cloud < 0.2f) return 0;
            if (cloud < 0.6f) return 2;
            return 3;
        }

        private static double GetD(object obj, string field)
        {
            if (obj == null) return 0.0;
            FieldInfo f = obj.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f == null) return 0.0;
            object raw = f.GetValue(obj);
            return raw == null ? 0.0 : Convert.ToDouble(raw);
        }

        /// <summary>Read the first matching public/non-public field or property as a double.</summary>
        private static double ReadNumber(object obj, params string[] names)
        {
            if (obj == null) return 0.0;
            Type t = obj.GetType();
            foreach (string name in names)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null)
                {
                    object raw = f.GetValue(obj);
                    return raw == null ? 0.0 : Convert.ToDouble(raw);
                }
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null)
                {
                    object raw = p.GetValue(obj, null);
                    return raw == null ? 0.0 : Convert.ToDouble(raw);
                }
            }
            return 0.0;
        }

        private static void SetF(object obj, string field, float value)
        {
            FieldInfo f = obj.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) f.SetValue(obj, value);
        }

        private static void SetD(object obj, string field, double value)
        {
            FieldInfo f = obj.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) f.SetValue(obj, value);
        }

        private static void SetI(object obj, string field, int value)
        {
            FieldInfo f = obj.GetType().GetField(field, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) f.SetValue(obj, value);
        }

        // ------------------------------------------------------------- helpers

        private static Type FindType(string fullName)
        {
            foreach (AssemblyLoader.LoadedAssembly la in AssemblyLoader.loadedAssemblies)
            {
                if (la == null || la.assembly == null) continue;
                Type t = la.assembly.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }
    }
}
