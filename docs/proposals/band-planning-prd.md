# PRD — Regional band planning

**Status:** Draft (2026-04-23) — Brian Keating (EI6LF), via team-lead.
**Related:** `docs/proposals/filter-visualization-prd.md` (consumes the `inBand(freqHz, mode)`
predicate this PRD exposes).
**Research:** `docs/proposals/research/thetis-bandplan.md`.

---

## 1. Problem statement

Zeus has no concept of a band plan. The VFO tunes freely; there is no visual indication when
the operator strays into a non-ham allocation, there is no sub-band label ("40M Extra CW"),
and there is no hook for the filter-visualization PRD to light up an out-of-band warning.
Operators need:

- A **default regional plan** (IARU Region 1 / 2 / 3 + UK, EI, US FCC general/extra, etc.)
  so a fresh install "just works" for their location. A UK/EI operator shouldn't see US sub-
  band labels on the panadapter (Thetis's long-standing gotcha — see
  `research/thetis-bandplan.md` §6).
- The ability to **edit the plan** when the default is wrong for them: locally-varying
  allocations (e.g. UK 60m spot channels differ from US channels; notch-permits differ by
  country) and to correct our defaults when the operator finds a mistake.
- A **consumable API** for the filter-visualization PRD to color filter passbands by in/out
  of band, and for a future TX-guard PRD to inhibit transmit.

## 2. Non-goals

- **TX inhibit / guard.** Thetis re-uses its band plan to gate MOX via `CheckValidTXFreq`
  (`console.cs:6778`). Zeus will follow — but that belongs in a separate "TX band guard" PRD
  once the plan model lands. This PRD only **surfaces** the data.
- **Full regulator parity.** We are not re-implementing every IARU member country's plan
  down to the last channelized segment. v1 ships a viable baseline for the regions Zeus
  operators actually use (based on current operator base: EI, G, US + generic IARU R1/R2/R3)
  and leaves the door open for community additions.
- **Power caps.** Thetis has none; Zeus v1 won't add them either. Reserved as a column-name
  slot in the data model so future versions can without a migration. Not surfaced in UI v1.
- **Mode-auto-switch** ("tune into the CW sub-band, mode auto-flips to CWU"). Explicit
  non-goal — operators find this annoying (per Thetis community feedback).

## 3. Data model

### 3.1 Region catalog

```csharp
namespace Zeus.Contracts;

public sealed record BandRegion(
    string Id,          // "IARU_R1", "IARU_R2", "IARU_R3", "EI", "G", "US_FCC_GENERAL", ...
    string DisplayName, // "IARU Region 1", "Ireland (EI)", "United Kingdom (G)", ...
    string ShortCode,   // "R1" | "R2" | "R3" | "EI" | "G" | "K-Gen"
    string? ParentId);  // null for pure IARU regions; set for country overrides that layer on top
```

A country region can declare a parent (e.g. `EI.ParentId = "IARU_R1"`); the effective plan is
parent segments overridden by country segments where they overlap. This is the **single
biggest departure from Thetis**, which has no parent/override relationship — Thetis collapses
everything into one flat `FRSRegion` enum. Layered regions lets us ship a minimal country
file that only overrides what's actually different.

### 3.2 Band segment

```csharp
public sealed record BandSegment(
    string RegionId,        // which region owns this segment
    long LowHz,             // inclusive
    long HighHz,            // inclusive
    string Label,           // "40M Extra CW", "60M Ch 1", "Out of Band"
    BandAllocation Allocation,   // Amateur | SWL | Broadcast | Reserved
    ModeRestriction ModeRestriction, // Any | CwOnly | PhoneOnly | DigitalOnly | CustomMask
    int? MaxPowerW,         // null = unlimited / unspecified. Reserved; unused in v1 UI.
    string? Notes);         // free-text, shown in tooltip

public enum BandAllocation : byte { Amateur, SWL, Broadcast, Reserved, Unknown }
public enum ModeRestriction : byte { Any, CwOnly, PhoneOnly, DigitalOnly }
```

- Frequencies in Hz (not MHz — wire consistency with the rest of Zeus).
- `Amateur` + `Any` is the common case.
- `ModeRestriction` is a coarse enum, not a bitmask — 95% of sub-bands are CW-only, phone-
  only, or unrestricted. If a segment needs finer control (e.g. "digital + CW, no phone"),
  the PRD adds a `CustomMask` variant with a flags field later. Not in v1.
- `MaxPowerW` is a reserved nullable field so the shape doesn't change when a TX-power PRD
  lands. UI ignores it in v1.

### 3.3 Effective plan resolution

Pseudocode for the server-side resolver that both `inBand(f, mode)` and the UI consume:

```
resolvePlan(regionId):
  acc = []
  for r in [regionId's ancestors, oldest-first] + [regionId]:
    for seg in r.segments:
      remove any overlapping segment in acc
      add seg to acc
  return acc sorted by LowHz
```

Country-level segments override parent segments by clearing the overlap first. Segments
returned by `resolvePlan` do NOT overlap each other — overlap is resolved at resolution
time, not at query time.

### 3.4 Frequency → segment lookup

```
getSegment(f):
  binary search resolvePlan(currentRegion) for the segment containing f.
  returns null if outside any Amateur segment.

inBand(f, mode):
  seg = getSegment(f)
  if seg == null -> false
  if seg.Allocation != Amateur -> false
  return seg.ModeRestriction matches mode or ModeRestriction == Any
```

Mode-matching rules:

- `CwOnly`: matches `RxMode.CWU` and `RxMode.CWL` only.
- `PhoneOnly`: matches `USB`, `LSB`, `AM`, `SAM`, `DSB`, `FM`.
- `DigitalOnly`: matches `DIGL`, `DIGU`.
- `Any`: matches everything.

## 4. Default regional data (shipped with Zeus.Server)

Ships as JSON under `Zeus.Server.Hosting/BandPlans/` (one file per region). Loaded into a new
`BandPlanStore` on startup. Layout:

```
Zeus.Server.Hosting/BandPlans/
  regions.json                 # region catalog (id, display, shortCode, parentId)
  IARU_R1.segments.json        # generic Region 1 baseline
  IARU_R2.segments.json        # generic Region 2 baseline
  IARU_R3.segments.json        # generic Region 3 baseline
  EI.segments.json             # Ireland overrides (parent: IARU_R1)
  G.segments.json              # UK overrides (parent: IARU_R1)
  US_FCC_GENERAL.segments.json # US FCC General class (parent: IARU_R2)
  US_FCC_EXTRA.segments.json   # US FCC Extra class (parent: IARU_R2)
```

**Scope for v1**: the seven files above. This is the "minimum viable set" matching the
expected initial operator base (EI maintainer + UK + US + generic IARU fallback). Additional
country files (DE, F, JA, VK, etc.) can be added as PRs without schema changes — the loader
auto-discovers any `*.segments.json` in the directory at startup.

Example `EI.segments.json` (abbreviated — just the 40m/60m slice):

```json
{
  "regionId": "EI",
  "segments": [
    { "lowHz": 7000000,  "highHz": 7039999,  "label": "40M CW",         "allocation": "Amateur", "modeRestriction": "CwOnly",   "notes": null },
    { "lowHz": 7040000,  "highHz": 7199999,  "label": "40M Mixed",      "allocation": "Amateur", "modeRestriction": "Any",      "notes": null },
    { "lowHz": 5351500,  "highHz": 5366500,  "label": "60M (secondary)","allocation": "Amateur", "modeRestriction": "Any",      "notes": "15 W EIRP cap — see ComReg" }
  ]
}
```

All default segment data is under version control in this repo; corrections land as PRs.

## 5. User edit surface

Thetis operators cannot edit the band plan without a code change (research §4). Zeus will
fix this — regulatory details vary and operators catch our mistakes before we do.

### 5.1 Setup panel (new `BandPlanEditor` component)

Placement: Settings drawer > "Band Plan" tab, alongside the existing AGC / NR / Layout tabs.

- **Region dropdown** — lists all known regions (baseline + country). Current selection is
  persisted in `DspSettingsStore` (new field `CurrentRegionId`, default `"IARU_R1"` — Ireland
  default, matches maintainer's location).
- **Segment table** — shows the effective plan for the chosen region (parent merged with
  country overrides). Columns: Low (MHz), High (MHz), Label, Allocation, Mode, Notes, Source.
  The **Source** column shows which region file owns the segment (`EI` override vs.
  `IARU_R1` inherited) so edits target the right level.
- **Row actions**: edit, delete, clone-as-override. "Edit" on an inherited row prompts
  "Override at EI level?" — an edit saves as a new country-level segment, leaving the parent
  file untouched.
- **Add row** button to insert new segments.
- **Reset to defaults** — restores the country file from the shipped JSON; inherited rows
  are untouched.

### 5.2 Persistence

User edits go into a separate LiteDB collection `BandPlanOverrides` in
`Zeus.Server`'s data file. Structure:

```csharp
public sealed record BandPlanOverrideRecord(
    string RegionId,
    IReadOnlyList<BandSegment> Segments,
    DateTime UpdatedUtc);
```

Override records SUPERSEDE the shipped JSON when present. "Reset to defaults" deletes the
record for that region. Overrides are per-region, not per-segment — simpler than trying to
diff individual segments, and the whole-region payload is small (~50 rows max).

## 6. API

### 6.1 REST

- `GET /api/bands/regions` → `BandRegion[]` (full catalog).
- `GET /api/bands/plan?region=EI` → `{ regionId, segments: BandSegment[] }` — resolved
  (parent merged with overrides).
- `PUT /api/bands/plan` — body `{ regionId, segments: BandSegment[] }` — replaces the
  override record wholesale. Server validates: no overlaps within the submitted set, sorted
  low→high, all Hz non-negative, Low ≤ High, labels non-empty. 400 on any failure.
- `DELETE /api/bands/plan/:regionId` — resets to shipped defaults.
- `GET /api/bands/current` → current region id + resolved plan (convenience for
  frontend mount).
- `POST /api/bands/current` — body `{ regionId }` — set the active region; persists via
  `DspSettingsStore`.

### 6.2 Hub (for the filter-overlay consumer)

- `BandPlanChangedEvent` broadcast on region change or plan edit. Frontend refetches
  `/api/bands/plan?region=X` on receipt.

### 6.3 Contract exposed to the filter PRD

```csharp
namespace Zeus.Server;

public interface IBandPlanService
{
    BandRegion CurrentRegion { get; }
    IReadOnlyList<BandSegment> CurrentPlan { get; }
    BandSegment? GetSegment(long freqHz);
    bool InBand(long freqHz, RxMode mode);
    event Action PlanChanged;
}
```

The filter PRD's frontend BandPlan context is populated from `GET /api/bands/plan` on mount
and on `BandPlanChangedEvent`. Same shape on both sides.

## 7. Frontend changes (`zeus-web/`)

### 7.1 New files

- `src/components/bandplan/BandPlanEditor.tsx` — the Settings > Band Plan panel (§5.1).
- `src/state/bandPlan.ts` — store: current region id, resolved segment array, edit-dirty
  flag.
- `src/context/BandPlanContext.tsx` — provides `inBand`, `getSegment`, `currentRegion` to
  consumers (filter overlay, future TX guard, future VFO label strip).

### 7.2 Modified files

- `src/api/*` — new REST client for the `/api/bands/*` endpoints.
- `src/realtime/hubClient.ts` — handle `BandPlanChangedEvent`.
- `src/layout/*` — register the Band Plan tab in the Settings drawer.

## 8. Filter PRD integration

This PRD **produces** the contract the filter PRD consumes:

```ts
// filter-visualization-prd.md §7 references this
export interface BandPlan {
  inBand(freqHz: number, mode: RxMode): boolean;
  getSegment(freqHz: number): BandSegment | null;
}
```

Both methods land in `src/context/BandPlanContext.tsx`. Before this PRD merges, the filter
PRD ships a local stub that always returns `true` for `inBand`. Once this PRD merges, the
stub is replaced by the real context provider. No changes to the filter PRD's code beyond
that wiring — the contract is stable.

The filter PRD's "Phase 4" (OOB coloring) is gated on this PRD reaching Phase 2 (editable
plan) — see §10.

## 9. Acceptance criteria

1. Fresh Zeus install with no operator config defaults to `IARU_R1` (single-operator v1
   bias; UI can be re-defaulted by shipping a `defaultRegionId` in `appsettings.json`).
2. `GET /api/bands/regions` returns the 7 baseline regions shipped in v1.
3. `GET /api/bands/plan?region=EI` returns a resolved plan with `IARU_R1` segments merged
   with the EI-specific 60m row from §4's example — no overlaps in the output.
4. The Band Plan editor lists segments with the right "Source" column; an `IARU_R1`
   inherited row shows source=`IARU_R1`, an EI override row shows source=`EI`.
5. Editing an inherited row and saving creates a new override record for EI; the `IARU_R1`
   file on disk is unchanged.
6. `InBand(7025000, RxMode.CWU)` returns `true`; `InBand(7025000, RxMode.USB)` returns
   `false` (CW-only sub-band per default EI plan).
7. `InBand(8000000, RxMode.USB)` returns `false` (outside any Amateur segment).
8. Clicking "Reset to defaults" on the editor wipes the override record and the UI
   refreshes to the inherited-only view.
9. Changing region fires `BandPlanChangedEvent`; filter-overlay consumers re-render.
10. Server refuses `PUT /api/bands/plan` with overlapping segments (HTTP 400, meaningful
    error).

## 10. Implementation phasing

- **Phase 1** — data model + shipped JSON (7 regions) + `BandPlanStore` + REST GETs +
  `IBandPlanService` + frontend context with read-only surface. No editor UI. Filter PRD
  can wire `inBand` against this.
- **Phase 2** — Band Plan editor (add/edit/delete/reset), override persistence, PUT +
  DELETE endpoints.
- **Phase 3** — (follow-up PRD) TX-guard consumer gating MOX on `InBand`.

Each phase is a separately-mergeable PR.

## 11. Open questions

- **Default region**: IARU_R1 is the safe default (maintainer's region). Should the
  installer prompt for region on first run? Default: no — operator sets it in Settings.
  Lightweight enough that the prompt would add friction.
- **Ireland vs. UK vs. generic R1**: both EI and G are R1 countries. Do operators who
  don't set their country explicitly get shown "IARU_R1" generic labels? Default: yes.
  Upgrade path: operator picks their country from the region dropdown.
- **Where does the shipped JSON live for testing?** Default: copy-on-publish from the
  source tree into `bin/Debug/net8.0/BandPlans/` via `<Content CopyToOutputDirectory>`.
- **Schema versioning**: if a future PRD needs `CustomMask`, do we bump the JSON schema?
  Default: add `$schema` at the top of each file; migration tooling deferred until we
  actually need it.
- **Multiple RX**: if RX2 is tuned outside the band, do we light it red too? Defer — when
  RX2 lands, the band-plan context is already per-frequency, so the overlay naturally picks
  up both.
- **Beacon sub-bands, calling frequencies**: Thetis's `BandText` has several one-Hz-wide
  "AM Calling Frequency" entries. We skip those in v1 (visual clutter). Label-only entries
  with no mode restriction can be added later under `Allocation = Amateur` +
  `ModeRestriction = Any` + a descriptive `Label`.

## 12. Risks

- **Regulatory accuracy**: shipping wrong default segments is worse than shipping none at
  all from a liability standpoint. Every shipped JSON file names the source regulator + the
  last-verified date in a top-of-file comment. Operators are reminded in the editor UI that
  "defaults are best-effort; you are responsible for operating within your license".
- **Schema churn**: the `BandSegment` shape is load-bearing for the filter PRD's `BandPlan`
  context. Any future additions should be backwards-compatible (new nullable fields), not
  renames/removals.
- **Plan resolution cost**: for a 50-row plan the resolver runs once per region change (not
  per-query). `getSegment` uses a pre-sorted binary search on the resolved array. No hot
  path concern.
