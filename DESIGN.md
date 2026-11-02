---
name: Tunnel Security
description: Mission-critical IoT monitoring for underground tunnel infrastructure in Hanoi, Vietnam.
colors:
  accent-blue: "#2979FF"
  accent-blue-mid: "#3B82F6"
  accent-blue-light: "#60A5FA"
  accent-blue-deep: "#144BB8"
  accent-blue-border: "#1D4ED8"
  severity-critical: "#FF5252"
  severity-high: "#FF8C42"
  severity-medium: "#FFD166"
  severity-low: "#3FCF8E"
  severity-offline: "#7B7E85"
  bg-base: "#060B17"
  bg-monitoring: "#0F1526"
  bg-surface: "#1C222D"
  bg-panel: "#171E33"
  bg-inset: "#111621"
  border-default: "#2D3545"
  border-subtle: "#1F2937"
  text-primary: "#E6EEF3"
  text-secondary: "#94A3B8"
  text-muted: "#64748B"
  text-dim: "#475569"
typography:
  display:
    fontFamily: "Segoe UI Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "32px"
    fontWeight: 700
    lineHeight: 1.2
  headline:
    fontFamily: "Segoe UI Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "24px"
    fontWeight: 600
    lineHeight: 1.3
  title:
    fontFamily: "Segoe UI Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "20px"
    fontWeight: 600
    lineHeight: 1.4
  body:
    fontFamily: "Segoe UI Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "14px"
    fontWeight: 400
    lineHeight: 1.5
  label:
    fontFamily: "Segoe UI Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: 1.4
  caption:
    fontFamily: "Segoe UI Variable, Segoe UI, system-ui, sans-serif"
    fontSize: "10px"
    fontWeight: 700
    lineHeight: 1.3
    letterSpacing: "0.06em"
rounded:
  sm: "4px"
  md: "8px"
  lg: "12px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "12px"
  lg: "16px"
  xl: "20px"
  2xl: "24px"
components:
  button-primary:
    backgroundColor: "{colors.accent-blue-deep}"
    textColor: "#FFFFFF"
    rounded: "{rounded.md}"
    padding: "9px 16px"
  button-primary-hover:
    backgroundColor: "{colors.accent-blue-border}"
    textColor: "#FFFFFF"
    rounded: "{rounded.md}"
    padding: "9px 16px"
  button-secondary:
    backgroundColor: "{colors.bg-inset}"
    textColor: "{colors.text-secondary}"
    rounded: "{rounded.md}"
    padding: "9px 16px"
  filter-pill:
    backgroundColor: "transparent"
    textColor: "{colors.text-secondary}"
    rounded: "{rounded.md}"
    padding: "7px 14px"
  filter-pill-active:
    backgroundColor: "#0D1D3B"
    textColor: "{colors.accent-blue-light}"
    rounded: "{rounded.md}"
    padding: "7px 14px"
  card:
    backgroundColor: "{colors.bg-surface}"
    rounded: "{rounded.lg}"
    padding: "16px"
  sensor-card:
    backgroundColor: "{colors.bg-surface}"
    rounded: "{rounded.md}"
    padding: "10px"
  input:
    backgroundColor: "{colors.bg-inset}"
    textColor: "{colors.text-primary}"
    rounded: "{rounded.md}"
    padding: "8px 12px"
---

# Design System: Tunnel Security

## 1. Overview

**Creative North Star: "The Tactical Display"**

This is infrastructure-grade software designed for 24/7 control room operation. The visual system is built around a single organizing principle: mission-critical information on dark glass. Like submarine sonar or air traffic control displays, every element earns its place by communicating something that matters. Decoration is absent. Hierarchy is everything. When a sensor spikes or an intrusion is detected, the operator must know in under two seconds — no hunting, no ambiguity, no interface in the way.

The palette is purpose-built for low-light control rooms: three layers of deep navy establish depth through tonal contrast rather than shadows, a single operational-blue accent signals interactivity with surgical precision, and four severity colors (green through red) form an unambiguous alarm grammar that operators read in peripheral vision. Dark mode is the true operating environment, optimized for large 1920×1080+ displays where information density should increase at scale, not just stretch.

This system explicitly rejects consumer-app aesthetics, generic SaaS admin layouts, gratuitous animation, and the empty-state minimal prototype look. There are no gradient buttons, no playful card shadows, no decorative transitions. The interface should look purpose-built — the kind of software that runs in a room with serious stakes, where the operator's attention is the most valuable resource.

**Key Characteristics:**
- Tonal depth through three background layers (#060B17 → #1C222D → #111621), not shadows
- Four-level severity color grammar applied consistently across all alert and status surfaces
- Solid, decisive controls — no ambiguity about what is interactive
- Single variable sans-serif; hierarchy through weight and size, not type mixing
- Patterns repeat exactly across all screens — operator predictability is a feature


## 2. Colors: The Tactical Palette

A closed system: one accent, four severity levels, three depth layers. Each color does one job.

### Primary
- **Operational Blue** (#2979FF): The singular interactive signal. Primary button fill, active borders, focus rings. Appears only where something is clickable or selected — its scarcity signals interactivity.
- **Mid Blue** (#3B82F6): Hover and processing states. Interactive border highlight on pointer-over. Never used as a primary fill in isolation.
- **Pale Blue** (#60A5FA): Selected navigation item text, secondary accent indicators. Lower-contrast application for labels and nav markers, not for primary CTAs.
- **Deep Navy Blue** (#144BB8): Primary button background (DkPrimaryButtonStyle). Darker than Operational Blue — creates visual weight without excess brightness.
- **Accent Border** (#1D4ED8): Active filter pills, focused elements, hover state for deep-navy buttons.

### Secondary
- **Severity: Healthy Green** (#3FCF8E): Normal operation, online nodes, success states. Background tint: #1A3A2E.
- **Severity: Warning Amber** (#FFD166): Medium severity, radar warnings, temperature caution. Background tint: #3D2E14.
- **Severity: High Orange** (#FF8C42): High severity alerts, intermediate escalation. Background tint: #3D2218.
- **Severity: Critical Red** (#FF5252): Critical alerts, intrusion detection, temperature critical. Triggers full-canvas red flash animation overlay. Background tint: #3D1A1A.

### Tertiary
- **Offline Gray** (#7B7E85): Disconnected nodes and offline devices. Status-only — not a severity level.

### Neutral
- **Base** (#060B17): Deepest canvas — navigation content area, page background in monitoring mode.
- **Monitoring Canvas** (#0F1526): Monitoring panels, navigation sidebar pane.
- **Surface** (#1C222D): Cards, panels, header bars — the primary raised layer.
- **Panel** (#171E33): Secondary containers, form backgrounds.
- **Inset** (#111621): Input fields, embedded data sections. Recessed below surface.
- **Primary Text** (#E6EEF3): All primary content on dark backgrounds.
- **Secondary Text** (#94A3B8): Metadata, timestamps, section labels.
- **Muted Text** (#64748B): Placeholders, uppercase captions, tertiary labels.
- **Dim Text** (#475569): Disabled state text.
- **Default Border** (#2D3545): Panel and card borders throughout dark UI.
- **Subtle Border** (#1F2937): Internal card dividers, inset boundaries.

**The Four-Levels Rule.** The severity scale is a closed system. #3FCF8E = healthy. #FFD166 = warning. #FF8C42 = high. #FF5252 = critical. These four colors are permanently reserved for operational status. Using critical red for form validation or healthy green for a non-sensor success message corrupts the alarm grammar. Prohibited.

**The One Accent Rule.** #2979FF appears only on interactive and selected elements. Every decorative blue use dilutes the signal that something is clickable. When in doubt, use a neutral.


## 3. Typography

**All levels:** Segoe UI Variable (Segoe UI, system-ui, sans-serif fallback)

**Character:** A single optical-size variable sans-serif across all roles. Hierarchy is expressed through weight and size contrast, not typeface mixing. Segoe UI Variable reads cleanly at both the 10px uppercase caption and the 32px dashboard stat.

### Hierarchy
- **Display** (700, 32px, 1.2): Large stat numbers in system overview panels, aggregate counts, station names. Maximum one instance per panel section.
- **Headline** (600, 24px, 1.3): Page and section titles. Used sparingly — one per major panel.
- **Title** (600, 20px, 1.4): Sub-section headings, card headers, dialog titles.
- **Body** (400, 14px, 1.5): Primary reading text, device labels, alert descriptions. Baseline for all data content.
- **Label** (400, 12px, 1.4): Timestamps, metadata, secondary descriptors within cards.
- **Caption** (700, 10px, 1.3, +0.06em letter-spacing, #64748B): Uppercase muted category markers via `DkLabelStyle`. At most one per section group.

**The Single-Family Rule.** All type is Segoe UI Variable. Do not introduce a second typeface. Weight and size contrast is the complete typographic system. Adding a second family in a dense monitoring interface adds noise, not character.


## 4. Elevation

This system uses **tonal layering** as its primary depth mechanism. Depth is communicated through background value steps, not shadows.

Three elevation layers:
1. **Base** (#060B17 / #0F1526): Page canvas and navigation pane. Nothing sits below this.
2. **Surface** (#1C222D / #171E33): Cards, panels, header bars. The primary interactive layer.
3. **Inset** (#111621): Input backgrounds, embedded data sections. Visually recessed.

### Shadow Vocabulary
- **Floating / Modal** (`ThemeShadow` — WinUI ambient + directional): ContentDialogs, popups, flyouts. Signals genuine z-axis separation from page content.
- **Standard panels:** No shadow. Tonal contrast with the base background does the work.

**The No-Decorative-Shadow Rule.** Shadows are reserved for surfaces that genuinely float above the page layout. A panel card within the page grid does not receive a shadow for visual interest. Tonal contrast is sufficient; shadows are earned by actual elevation.


## 5. Components

### Buttons
Solid, filled, and decisive. Minimum height 36–40px. Radius 8px across all variants. No ambiguity about what is interactive.

- **Primary (Dark — DkPrimaryButtonStyle):** #144BB8 fill, white SemiBold 13px, 16/9px padding. Hover → #1D4ED8. Pressed → #1E40AF.
- **Primary (Light theme):** #1E3A8A fill, white SemiBold 14px, 20/12px padding. Micro-scale 0.98 on press.
- **Secondary (Dark — DkSecondaryButtonStyle):** #111621 background, #2D3545 border 1px, muted text. Hover → background #1C222D, border #475569.
- **Icon (DkIconButtonStyle):** 34×34px, transparent, radius 6. Hover → #1C222D. Pressed → #2D3545.
- **Ghost (DkGhostButtonStyle):** Transparent, no border, minimal padding. Hover → #0D1D3B. For text-adjacent secondary actions.

### Filter Pills / Chips
Immediate state change — no transition animation.
- **Inactive:** Transparent, #2D3545 1px border, radius 8px, 14/7px padding. Text: #94A3B8.
- **Active:** #0D1D3B background, #1D4ED8 border 1px. Text: #60A5FA.

### Cards / Containers
- **Dark Card (DkCardStyle):** #1C222D background, #2D3545 1px border, radius 12px. Standard panel container in all monitoring views.
- **Sensor Card (DkSensorCardStyle):** Same surface, radius 8px, padding 10px. For dense data grids.
- **Header Bar (DkHeaderBarStyle):** #1C222D background, 1px bottom border #2D3545, 24/14px horizontal/vertical padding. Sticky to top of each page view.
- **Elevated / Dialog (CardBorderStyle):** Theme-aware, radius 12px, padding 20px. ThemeShadow applied for floating/modal contexts only.

### Inputs / Fields
- **Dark Search (DkSearchBoxStyle):** #111621 background, #2D3545 border, radius 8, min-height 36px. Placeholder #64748B. Focus: border → #2979FF.
- **Standard TextBox:** Panel background, 1px border, radius 8, 12/10px padding, min-height 40px.
- **Dark ComboBox (DkComboBoxStyle):** Inset background, default border, radius 8, 12/8px padding.

### Icons, Tags and Status Markers
Domain concepts are never drawn as pictograms or emoji. The interface already speaks in instrument tags (`SEN-002`, `CAM-004`, `RAD-001`), so the same vocabulary is used wherever a device or sensor type needs a visual cue.

- **Instrument tag:** 10px Bold, +0.08em letter-spacing, `#94A3B8` text, `#111621` fill, 1px `#2D3545` border, radius 4, padding 8/3. Codes: `CAM`, `SEN`, `RAD`, and per sensor type `TEMP`, `HUM`, `VIB`, `PRES`, `GAS`, `LVL`, `FIRE`, `MOT`. Tint may follow the sensor identity colors (never the severity colors).
- **Status marker:** a geometric shape beside the status label so state never relies on color alone. Filled circle = online / healthy, triangle = warning or fault, hollow ring = offline, square = disabled. Drawn as a `Polygon` / `Ellipse` / `Rectangle` in XAML, or `● ▲ ○ ■` in text.
- **Trend direction:** an 8px filled `Polygon` triangle, not a text arrow.
- **Counts at zero:** a severity-colored count (for example warnings) falls back to muted text when it is 0.
- **Action icons:** Segoe Fluent glyphs are allowed for generic actions only (add, edit, delete, search, chevrons, settings, open in window). Delete is neutral at rest and turns severity-critical only on hover.
- **Identity:** the signed-in user is a two-letter monogram, never a generic person glyph.

### Alert Severity Containers
Four variants (Healthy / Warning / High / Critical). Each applies a severity background tint with a full 1px colored border on all four sides. Radius 4px. Padding 12px. Severity is always reinforced with icon or text label — not color alone.

### Navigation (WinUI NavigationView)
- **Sidebar pane:** #0F1526 background. Recessive — operator attention goes to content.
- **Default item:** #E6EEF3 text, transparent background.
- **Hover:** Background #1F2429. Text → #60A5FA.
- **Selected:** Background #1E3A8A. Text #60A5FA. 3px left-column indicator (structural layout element within the item).

### Alert Flash Overlay (Signature Component)
On critical alert, a translucent red overlay (#991B1B, reduced opacity) covers the monitoring canvas and pulses via Storyboard. The 8×8px pulsing ellipse on badge components follows the same trigger pattern. This is the only animation in the system — it fires because a real operational event has occurred. Never decorative.


## 6. Do's and Don'ts

### Do:
- **Do** reserve the four severity colors exclusively for operational states: green = healthy, amber = warning, orange = high, red = critical.
- **Do** express depth through tonal background stepping: base (#060B17) → surface (#1C222D) → inset (#111621). Reach for a background step before adding a border or shadow.
- **Do** keep all primary interactive controls solid and filled. Operators must never guess whether something is clickable.
- **Do** use full-border + tinted-background treatment on alert containers: 1px colored border on all four sides plus severity background tint.
- **Do** reinforce severity colors with icon and label — never rely on color alone for accessibility.
- **Do** scale information density upward at 1920×1080+ — the layout should present more at large sizes, not just stretch.
- **Do** apply `DkLabelStyle` (10px Bold uppercase, #64748B) at most once per section group, for category labels only.

### Don't:
- **Don't** use side-stripe borders (`BorderThickness="2,0,0,0"` or equivalent left-only colored borders) on alert containers or any surface. Use full 1px border with tinted background instead. This is the primary migration target in the existing codebase.
- **Don't** use #2979FF (Operational Blue) decoratively. Its rarity signals interactivity — every non-interactive use trains operators to ignore it as an action cue.
- **Don't** use #FF5252 (Critical Red) for general UI error states unrelated to monitoring severity. Form validation errors are distinct from critical sensor alarms.
- **Don't** animate anything except operational state transitions (press scale, critical alert flash). No entrance reveals, section choreography, or decorative loading spinners.
- **Don't** introduce warm or cream-tinted backgrounds. The palette is cold-navy. Warm tones erode the tactical-display character and degrade severity red perception.
- **Don't** build consumer-style UI: no pill buttons, no gradient CTAs, no hover-lift shadows, no playful icon or avatar treatments.
- **Don't** replicate generic SaaS layout: sidebar + uniform metric widget grid + donut chart per section. Organize by information hierarchy, not template.
- **Don't** add a second typeface. Weight and size contrast within Segoe UI Variable is the complete typographic system. Monospace (Consolas) is not part of the system, including for IDs and table headers.
- **Don't** use emoji or decorative pictograms for sensors, cameras, hubs, settings or status. Use instrument tags and status markers (see Icons, Tags and Status Markers).
- **Don't** ship placeholder controls: pagination buttons that do nothing, empty rounded boxes where an empty state belongs. Every list needs a real empty state.
