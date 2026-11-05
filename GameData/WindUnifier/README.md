# WindUnifier 0.4.0

One unified wind for **FAR**, **NaturalWeather (NW)** and **Kerbal Weather Project (KWP)** —
with a fallback chain that **never zeroes another mod's wind just because a wind mod is missing**.

WindUnifier does not edit any third-party mod file. Everything is done at runtime with
Harmony patches and reflection, and every optional source is probed before use.

## Features

| Feature | Behaviour |
|---|---|
| **Single aerodynamic wind** | Patches `FARAtmosphere.SetWindFunction`; the FAR slot always points at `WindCore.UnifiedWind` while a source is available. A 1 s watchdog repairs the slot if KWP/NW overwrite it. |
| **Wind source fallback** | `NaturalWeather.Bridge.Wind` first; if it is absent or returns zero, KWP climatological wind (`climate_api.uwind/vwind/zwind`); otherwise zero. See the chain below. |
| **Safe slot handover** | Before the first takeover the current FAR wind delegate is saved. `enabled = false` and `Shutdown()` restore it. With no NW/KWP source the slot is **never touched**, so other mods keep their own wind. |
| **KWP → NaturalWeather** | Every 30 s a `CityWeather` built from KWP `getWind` / `getAmbientWx2D` is written to `NaturalWeather.Overrides.City`, and `CityWeight` returns `weight` (default 0.55) world-wide. NW keeps running its own mesoscale model. |
| **KWP panel follows** | Patches `KerbalWxClimo/KerbalWxPoint.getWx3D()` and rewrites `[0]` = north, `[1]` = east, `[2]` = up to the unified wind. |
| **Humidity follows** | `getWx3D()[5]` and `getWx2D()[6]` become `max(KWP value, cloud/rain-derived value)`: > 0.5 cloud cover → 90–100 %, precipitation → 100 %. |
| **EVE cloud gain** | Samples the altitude band of EVE volumetric-cloud layers and applies `wind × (1 + eveCloudGain · thickness / eveCloudRef)`; direction is unchanged. Falls back to KWP climatological cloud cover if sampling fails. |
| **Layered caps** | Background wind 60 m/s, total 250 m/s. |
| **Nature's Fury** | Left alone: NF keeps `windPhysics` and applies tornado forces itself. NF wind is exposed only through `VisualWind` (visual effects), **not** mixed into FAR, to avoid double-counting. |
| **Public API** | `WindUnifier.API` exposes `IsActive`, `Status`, `UnifiedWind()` and `VisualWind()` for other mods. |

## Wind source fallback chain (0.4.0)

```
enabled && Active ?
  ├─ NaturalWeather Bridge.Wind(vessel)        (dynamic weather, preferred)
  ├─ if zero → KWP climate_api 4-arg wind      (climatology, Kerbin only)
  └─ if still zero → zero
then: EVE cloud gain × layered caps
```

Nature's Fury is deliberately **not** part of this chain. It is only used by `VisualWind()`:

```
VisualWind = the larger of (UnifiedWind, NaturesFury WindDirWorld × WindNow)
```

Details:

- **NW available**: its local wind is used; KWP fills in only when NW returns zero.
- **NW missing, KWP present**: `climate_api.uwind/vwind/zwind(lat, lon, alt, ut)` is called with
  latitude/longitude in **degrees** and altitude in **metres**; the ENU result is converted to
  world space with the local east/north basis. Older KWP builds that lack `climate_api` fall back
  to `climate_data.getWind(lat, lon, alt)` (`wx_wind`), then to the two-argument
  `climate_data.uwind/vwind/zwind(alt, ut)`.
- **Neither NW nor KWP**: `UnifiedWind()` returns zero, but the FAR slot is **not** taken over.
  A missing wind mod therefore cannot zero the wind that FAR or another mod provides.
- KWP climate data exists for **Kerbin only**; `onlyKerbin = true` keeps other bodies untouched.

## Dependencies

| Dependency | Required? | Effect when missing |
|---|---|---|
| **Harmony (HarmonyKSP, 2.x)** | **Yes** | Plugin cannot load. No patches, no slot takeover. |
| KSP 1.12.5 | Yes | — |
| NaturalWeather | Optional | KWP climate wind is used directly. |
| KerbalWeatherProject | Optional | NW wind is used directly. |
| FerramAerospaceResearch | Optional | No FAR wind slot exists; the public API still works. |
| Nature's Fury | Optional | No visual NF wind enhancement; NF itself is untouched. |
| EVE / EVE Redux + volumetric clouds | Optional | Cloud gain stays 1.0 (or uses KWP cloud cover). |

## Installation

1. Install **HarmonyKSP** first.
2. Copy the `GameData/WindUnifier/` folder into your KSP `GameData/` folder.
3. Start KSP. The log should show the `[WindUnifier] init: sources ...` line.

No ModuleManager config is required.

## Configuration — `GameData/WindUnifier/WindUnifier.cfg`

| Key | Default | Meaning |
|---|---|---|
| `enabled` | `true` | Master switch. When `false`, the FAR slot is restored and all patches become no-ops. |
| `weight` | `0.55` | How strongly KWP climate sets NW's base state. `1.0` suppresses NW's own medium-scale variation. |
| `eveCloudGain` | `0.25` | EVE cloud-column gain; `0` disables it. |
| `eveCloudRef` | `3000` | Reference cloud thickness (m); at this thickness the gain is `1 + eveCloudGain`. |
| `maxBackground` | `60` | Background wind cap (m/s). |
| `maxTotal` | `250` | Numerical safety cap (m/s). |
| `onlyKerbin` | `true` | KWP ships Kerbin climate data only; do not query other bodies. |
| `debugHud` | `false` | Show `[WindUnifier] <status>` in the upper-left every second in flight. |

Restart KSP after editing the cfg; there is no in-game UI.

## Compatibility matrix

| Installed combination | `UnifiedWind()` | FAR wind slot | Notes |
|---|---|---|---|
| NW + KWP + FAR | NW (KWP if NW returns zero) | unified | Full feature set. |
| NW + FAR, no KWP | NW | unified | KWP panel/humidity patches are skipped. |
| KWP + FAR, no NW | KWP climate (Kerbin) | unified | 0.4.0's main fix. |
| FAR only, no NW/KWP | zero | **untouched** | No takeover; FAR/default or another mod's wind survives. |
| NW + KWP, no FAR | NW → KWP | no slot exists | API-only; nothing to take over. |
| NF only, no NW/KWP/FAR | zero | untouched | NF keeps its own `windPhysics` forces. |
| `enabled = false` | zero | original delegate restored | No zeroing of another mod's wind. |
| Harmony missing | — | untouched | Plugin does not load. |

The important distinction: a **zero API result** does not mean the FAR slot is zeroed.
When no aerodynamic source is installed, WindUnifier leaves the slot exactly as it found it.

## Logs

Look for these lines in `KSP.log`:

```
[WindUnifier] v0.4.0 started (NW -> KWP fallback, guarded FAR slot)
[WindUnifier] init: sources nw=True kwp=climate_api far=True nf=True eve=True slot=unified patches=6/6
[WindUnifier] slot original captured: KerbalWeatherProject.KerbalWxClimo.GetTheWind
[WindUnifier] climate injection ok: 12.3m/s 270deg @Kerbin
[WindUnifier] state: scene=FLIGHT slot=WindCore.UnifiedWind city=True status=ok 12.3m/s 270deg ...
[WindUnifier] slot restored: FAR default
```

`sources` fields:

- `nw=True/False` — NaturalWeather `Bridge.Wind` found.
- `kwp=climate_api | getWind | uwind2 | none` — best KWP fallback available.
- `far=True/False` — FAR `SetWindFunction` slot found.
- `nf=True/False` — Nature's Fury weather type found.
- `eve=True/False` — EVE clouds manager found.
- `slot=unified` — the FAR slot currently points at `WindCore.UnifiedWind`; `original` — it does not.

## Public API

```csharp
using WindUnifier;

bool active = API.IsActive;
string status = API.Status;
Vector3 aero = API.UnifiedWind(body, part, position);   // NW -> KWP -> zero, capped
Vector3 visual = API.VisualWind(body, part, position);  // max(unified, NF), for FX only
```

Do **not** feed `VisualWind()` into FAR or aerodynamic forces: Nature's Fury already applies
its own forces, so using it aerodynamically would double-count tornadoes.

## Uninstall / rollback

1. Delete `GameData/WindUnifier/` entirely. No files outside that folder are touched.
2. If you are rolling back to 0.3.0, restore the backed-up `GameData/WindUnifier/` folder and
   restart KSP. The FAR slot always starts fresh on the next start; no persistent state is written.
3. If a previous session left the slot pointing at WindUnifier, shutting KSP down (or setting
   `enabled = false` and restarting) restores the original delegate via `RestoreSlot()`.

## Known limitations

- KWP is a **climatology** (month/season averages), not live weather. It provides the regional
  tendency; NW provides the day-to-day variation.
- KWP climate data covers **Kerbin only**. Other bodies only get NW wind (or zero if NW is absent).
- KWP panel: only wind (`[0..2]`) follows; temperature/pressure/cloud cover stay KWP values.
- When NW is installed but switched off (`Weather.On = false`) and KWP is also absent, the unified
  wind is zero. The FAR slot is still taken over because the NW assembly is present; use
  `enabled = false` in that configuration if another mod should own the slot.
- Nature's Fury tornadoes do not enter FAR aerodynamics; aircraft are pushed by NF's direct forces.
- No in-game UI; edit the cfg and restart.

---

## 中文附录

### 这个 mod 做什么

把 KWP 气候、自然天气（NW）和 FAR 的风统一成一份可解释的合成风：

- **NW 优先**：有 NW 时用它的实时局地风。
- **KWP 兜底**：NW 缺失或返回零时，用 KWP `climate_api` 的气候风（经纬度单位为**度**，高度为米）。
- **NF 只做视觉**：Nature's Fury 的风只进入 `VisualWind()`，不进入 FAR，避免与 NF 自己的
  `windPhysics` 双算。
- **FAR 槽保护**：接管前保存原始委托；`enabled=false` 或插件关闭时恢复。没有任何风源时
  **完全不接管**，不会把别的 mod 的风清零。
- 另有：KWP 气候注入 NW、KWP 面板跟随、湿度跟随、EVE 云柱增益、分层限幅（60 / 250 m/s）。

### 依赖

- **必需**：KSP 1.12.5、Harmony（HarmonyKSP 2.x）。
- **可选**：NaturalWeather、KerbalWeatherProject、FerramAerospaceResearch、Nature's Fury、
  EVE / EVE Redux + 体积云。

缺 Harmony 时插件无法加载；缺 NW 或 KWP 时仍有一个风源；两个都缺时 API 返回零，但
**FAR 槽保持原样**。

### 安装 / 回滚

把 `GameData/WindUnifier/` 复制进 KSP 的 `GameData/`。回滚就删除这个目录，或换回旧版
`GameData/WindUnifier/`。插件不写任何持久化状态，重启即恢复。

### 配置

`WindUnifier.cfg`：`enabled`、`weight`、`eveCloudGain`、`eveCloudRef`、`maxBackground`、
`maxTotal`、`onlyKerbin`、`debugHud`。改完重启 KSP，无游戏内 UI。

### 日志

`[WindUnifier] init: sources nw=True kwp=climate_api far=True nf=True eve=True slot=unified`
能一眼看出当前有哪些风源、FAR 槽是否已接管。`slot=original` 表示未接管。

### 已知限制

KWP 是气候平均而非实时天气；KWP 只有 Kerbin 数据；NF 涡旋不进 FAR 气动；无游戏内 UI。

## Credits

Developed by **ThreeMACH**. Implementation, testing and documentation assisted by **DeepSeek** (https://github.com/deepseek-ai).
