# RCA — RF2K-S plugin crashes on /tuner poll — Rf2kTuner DTO type mismatch (issue #552)

**Date:** 2026-05-30  
**Reported by:** Craig Honeyfiled (craighoneyfield)  
**Fixed in:** `openhpsdr-zeus-plugins` PR targeting `develop` (see below)

---

## Symptom

Clicking **Test REST** in the RF2K-S plugin settings page shows:

```
✓ Reached RF2K-S at 192.168.50.209:8080
The JSON value could not be converted to Openhpsdr.Zeus.Plugins.Rf2k.Rf2kTuner.
Path: $.L | LineNumber: 0 | BytePositionInLine: 38.
```

The first line is the successful result of the `/info` reachability check. The second line
is the background poll loop failing when it tries to deserialise the `/tuner` response. The
error appears in the `status.error` field on the settings panel, which is rendered just
below the test result toast.

---

## Root Cause

The amp's `/tuner` endpoint returns `L`, `C`, and `tuned_frequency` as
`{value, unit}` objects — the same wrapper shape used by every other measurement
endpoint (`band`, `frequency`, `temperature`, `voltage`, `current`,
`forward`, `reflected`, `swr`).

The `Rf2kTuner` record in `Rf2kPlugin.cs` (and its predecessor in the
in-tree `Zeus.Contracts/Rf2kDtos.cs` before commit `b02f794`) declared
those three fields as `double?`:

```csharp
// Before — wrong:
public sealed record Rf2kTuner(string? Mode, string? Setup, double? L, double? C, double? TunedFrequency);
```

Actual wire response from the amp:

```json
{
  "mode": "AUTO",
  "setup": "LC",
  "L":               {"value": 255,  "unit": "nH"},
  "C":               {"value": 51,   "unit": "pF"},
  "tuned_frequency": {"value": 7255, "unit": "kHz"},
  "segment_size":    {"value": 25,   "unit": "kHz"}
}
```

When `System.Text.Json` tries to populate `double? L` from `{"value": 255, "unit": "nH"}`,
it throws `JsonException` because an object cannot be coerced into a scalar. Every other
field in the DTO (`Mode`, `Setup`) is a `string?` and deserialises correctly.
`segment_size` was also missing from the DTO entirely.

The `Test REST` button itself only calls `/info` (which succeeds), so the success toast
appears first. The crash comes from the independent background poll that hits all eight
endpoints, including `/tuner`.

---

## Fix

**Location:** `openhpsdr-zeus-plugins/amplifiers/Rf2k/Rf2kPlugin.cs`  
**and:** `openhpsdr-zeus-plugins/amplifiers/Rf2k/ui/rf2k.tsx`

### Backend — Rf2kPlugin.cs

```diff
-public sealed record Rf2kTuner(string? Mode, string? Setup, double? L, double? C, double? TunedFrequency);
+public sealed record Rf2kTuner(string? Mode, string? Setup, Rf2kReading? L, Rf2kReading? C, Rf2kReading? TunedFrequency, Rf2kReading? SegmentSize);
```

`Rf2kReading` is already defined in the same file:
```csharp
public sealed record Rf2kReading(double Value, string? Unit);
```

### Frontend — ui/rf2k.tsx

```diff
 type Rf2kTuner = {
   mode: string | null;
   setup: string | null;
-  l: number | null;
-  c: number | null;
-  tunedFrequency: number | null;
+  l: Rf2kReading | null;
+  c: Rf2kReading | null;
+  tunedFrequency: Rf2kReading | null;
+  segmentSize: Rf2kReading | null;
 };
```

`Rf2kReading` is already defined as `{ value: number; unit: string | null }` in the same file.

The UI does not currently display `L`, `C`, or `TunedFrequency` beyond the `mode` string,
so no component code needs updating.

---

## Why the original DTO had `double?`

The `Rf2kTuner` record originated in `Zeus.Contracts/Rf2kDtos.cs` (removed in commit
`b02f794` — "refactor(rf2k): extract RF2K-S amplifier integration to plugin"). The comment
at the top of that file acknowledges the `{value, unit}` wrapper convention:

> Most measurement endpoints wrap values as `{value, unit}` and forward/reflected/swr add
> `max_value` for the firmware's peak hold.

Despite that comment, `Rf2kTuner` used `double?` for all three tuner fields rather than
`Rf2kReading?`. The bug was present from the initial RF2K implementation and was not caught
in testing because the firmware's `/tuner` endpoint returns `null` for the `L`, `C`, and
`tuned_frequency` fields when the tuner is in `OFF` or `BYPASS` mode — JSON `null`
deserialises into `null` for both `double?` and `Rf2kReading?`, so testing with the tuner
inactive masked the mismatch.

---

## Timeline

| Date       | Event |
|------------|-------|
| 2026-05-26 | Craig files issue #552 with `curl` output showing `{"value": 255, "unit": "nH"}` shape |
| 2026-05-30 | Root cause confirmed; diff prepared for `openhpsdr-zeus-plugins` |

---

## Prevention

The `{value, unit}` wrapper convention is documented in the original `Rf2kDtos.cs` header
comment and is consistent across all other measurement fields in the plugin.
Any future endpoint additions in `Rf2kPlugin.cs` should use `Rf2kReading?` (or
`Rf2kPeakReading?` for readings that include `max_value`) for every field that carries a
physical measurement with a unit — never a bare `double?`.
