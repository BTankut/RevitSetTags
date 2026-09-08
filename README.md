# revAgent tag tool (Revit 2027)

The tag-arrangement tool of revAgent, currently developed as a standalone Revit add-in
(it will be folded into revAgent later). It takes the tags of a view and arranges them in
clean columns and rows with leaders: equal spacing, level leader shoulders, aligned elbows,
no crossing leaders, and every leader still pointing exactly where it pointed before. A
whole floor plan can be tagged "outside the plan" in one click with perimeter lanes.

## Palette

The **revAgent tag tool** palette is a small modeless tool window opened from
**Add-Ins → revAgent tag tool → Tag Tool**.

| Control | What it does |
| --- | --- |
| `Get tags` | Reads the tags currently selected in Revit (window selection is fine, no Finish click). With nothing selected it starts a pick mode that only accepts tags (Finish to end). The selection stays pending for the next step. |
| filter (drop-down) | Built from the pending selection: `All selected tags` or one tag category / family with its count. Narrows the pending selection. |
| `Pick direction` | One click: the column origin. The tags are stacked straight down from that point and the command ends (the filter resets). Right-click or Ctrl+click the button to pick a second point that sets the column direction (rows, slanted columns). Without a pending selection it uses the tags selected in the view, and without those it moves the last group. |
| `Auto lanes` | Lays the pending selection (or the selected tags, or every tag of the view) out around the elements: left/right columns and top/bottom rows derived from the elements' extents, outer rings with a half-pitch stagger when the capacity runs out. Every element goes to the nearest lane that still has room, closest elements first. |
| `Lane per family` (check box) | Auto lanes option: elements are grouped by the side of their bearing from the centroid, and on every side the families follow each other along the same line, one run per family with a gap between runs, ordered like their elements along that side. Each run shows one kind of tag and no leader crosses another family's texts. A second ring is opened only when a line overflows. |
| `Tags count: N` | Selected / filtered / placed tag count. |
| `Spacing x:` `[0.6]` `-` `+` | Pitch between tags along the column or row, in meters. Typing or stepping (0.1) re-lays the affected groups out live. It is a minimum: the pitch never drops below the text height (rows: text width), so tags never overlap. |
| `Shift x:` `[0.30]` `-` `+` | Leader shoulder length from the text edge to the elbow, in meters. Live as well. |
| status line | Result of the last action, or the error text. |

Live adjustments apply to the groups that contain the tags selected in the view (several
groups at once if the selection spans them; selected tags that belong to no group become a
new group starting at their first tag), or, with nothing selected, to every group of the last
action (one column, or all Auto lanes).

## What a layout does

- **Rows.** The first tag lands on the picked origin, the others follow along the column axis
  at the pitch: `Row(i) = origin + i × pitch × axis`. The axis is straight down the view unless a
  direction point was picked.
- **Text alignment.** The label geometry of every tag family is read once (from the family
  document, cached) and the text width is computed from the font's advance widths, so the text
  block of each tag is centred on its row with its edge facing the elements exactly on the
  column line, whatever the family's origin convention. Families whose geometry cannot be read
  fall back to placing the tag head on the line.
- **Leaders.** Leader ends are switched to free and restored after the move, so arrows stay
  where they pointed instead of sliding to the nearest, often hidden, point of the element.
  Every leader gets an elbow one Shift x away from the column line towards the elements: level
  shoulders, aligned elbows, equal visible shoulder length. Leaders are turned on when missing.
- **Order.** Rows are ordered by an angular fan from the column centre towards the leader
  ends, then any two crossing leaders (diagonals and shoulders) are swapped until none cross.
- **Views.** Locked 3D, plan and section views. In 3D views a temporary work plane through
  the tags, parallel to the screen, is used for the point picks and removed afterwards.
- **Units.** Values are entered in meters and converted to internal units.

## Installation

1. Build:
   ```
   dotnet build -c Release
   ```
2. Copy the output next to a manifest in the Revit 2027 add-ins folder:
   ```
   %AppData%\Autodesk\Revit\Addins\2027\RevitSetTags\RevitSetTags.dll
   %AppData%\Autodesk\Revit\Addins\2027\RevitSetTags.addin
   ```
   Put the full path of the DLL in the manifest's `<Assembly>` element (a relative
   `RevitSetTags.dll` also works when the DLL sits next to the manifest).
3. Start Revit 2027 → **Add-Ins** → **revAgent tag tool** → **Tag Tool**.

> The project targets `net10.0-windows`; Revit 2027 runs on .NET 10 and the
> Nice3point 2027.2.0 API packages match Revit 2027.2 (27.2.0.39). `IndependentTag.SetLeaderElbow`
> (2022+) and `GetTaggedLocalElements()` are used, so older Revit versions need code changes as
> well as package changes. Revit 2027 did not show the unsigned add-in prompt on the
> development machine; other installations may ask for "Always Load" on first start.

## Typical use

1. Select tags with Revit's selection tool → **Get tags**.
2. Optionally pick a family in the filter.
3. **Pick direction** → click the column origin; the tags stack downwards. Right-click or
   Ctrl+click for a direction point (horizontal rows, slanted columns).
4. For a whole floor plan: select the tags (or nothing) → tick **Lane per family** if wanted
   → **Auto lanes**. Set the view scale first (for example 1:200); pitches follow the text size.
5. Fine-tune with **Spacing x** / **Shift x** while the group is selected (or nothing is
   selected for the last action). Each action is one "Order Tags" transaction, so Ctrl+Z
   undoes it.

## Limits

- Tags whose families use straight-only leaders keep their placement but get no elbow.
- 3D views must be locked (Revit keeps tags only in locked 3D views).
- Auto lanes uses a plain perimeter ring; it does not exploit notches of L-shaped plans, so
  some leaders get long on such plans.
- Tag bounding boxes in 3D views are not usable for text measurement; the text metrics come
  from the family labels instead, which is why mixed-family columns still line up.

## Project layout

| File | Role |
| --- | --- |
| `App.cs` | "revAgent tag tool" panel and icon button on the Add-Ins tab |
| `Commands/ShowSetTagsCommand.cs` | Opens the modeless palette |
| `UI/SetTagsWindow.cs` | Palette: Get tags / filter / Pick direction / Auto lanes (+ Lane per family) / Spacing x / Shift x with steppers, live updates |
| `Handlers/SetTagsHandler.cs` | `IExternalEventHandler`: selection, filter, origin/direction picks (temporary work plane in 3D), group memory, live adjustment targets |
| `Services/TagOrderingService.cs` | Column core: text metrics, leader end preservation, ordering and uncrossing, shoulders and elbows |
| `Services/LaneLayoutService.cs` | Auto lanes: perimeter lanes, sector assignment, per-family runs, capacity, one `PlaceColumn` per lane or run |
| `Resources/tagtool-16.png`, `tagtool-32.png` | Ribbon icons (embedded resources) |
| `RevitSetTags.addin` | Revit manifest (revAgent tag tool, vendor DPE) |
