# ECL Addons: theme packs and plugins

Reference for third-party developers. The full walkthrough (with the rationale behind each
validation rule) is in **[Chinese: docs/ADDONS.zh-CN.md](ADDONS.zh-CN.md)** — this file is
the compact English reference.

Contract version: `apiVersion = 1`, launcher **1.0.x**.

---

## The three APIs

| API | For | Carrier | Runs code | Purpose |
|---|---|---|---|---|
| **ECL Interface API** | any language | `addon.json` + assets | no | theme colours, metadata, capability declarations |
| **ECL Addition API** | C# | assembly implementing `IEclAddon` | yes (in-process) | launch hooks, logging, background work |
| **ECLUi C# API** | C# | assembly implementing `IEclUiExtension` | yes (in-process) | inject WPF elements into four slots |

C# addons load **in-process** with full launcher privileges. There is no sandbox; only
install addons you trust. `capabilities` documents intent, it does not enforce anything.

---

## Where addons live

```
<data-root>/addons/
    aurora/addon.json              theme pack
    my-plugin/addon.json           plugin
    my-plugin/MyPlugin.dll
```

Install → Settings → Appearance → **Addons and theme packs** → **Import addon** (pick a `.zip`
containing `addon.json`), or drop the folder in by hand and press **Rescan**. **Remove selected**
moves the folder to `addons/.trash` (nothing is deleted). Rejections are
written to `startup.log` next to `ECL.exe`:

```
addon rejected: …/addon.json (theme.colors.Accent = 'purple' is not #RRGGBB or #AARRGGBB)
```

---

## `addon.json`

| Field | Required | Notes |
|---|---|---|
| `schemaVersion` | yes | must be `1` |
| `apiVersion` | no | default `1`; higher than the launcher supports → rejected |
| `id` | yes | 2–48 chars, starts with a lowercase letter, `a-z 0-9 . _ -` only, unique |
| `name` | no | display name; falls back to `id` |
| `version` / `author` / `description` | no | shown in the UI and logs |
| `type` | no | `theme` (default), `plugin`, `mixed` |
| `entry.assembly` | plugins | dll file name, relative to the addon folder |
| `entry.type` | no | full type name; **leave empty** to auto-pick the single implementing type (required for obfuscated builds) |
| `theme` | themes | see below |
| `capabilities` | no | string array, e.g. `["launch.before"]` |

### Validation (a bad pack is rejected, never fatal)

`schemaVersion != 1` · `apiVersion > 1` · malformed `id` · missing `entry.assembly` or file ·
theme block missing · colour not `#RRGGBB`/`#AARRGGBB` · `opacity` outside 0.5–1.0 ·
**background overridden without any `Text*` colour** · duplicate `id`.

---

## Theme packs

Minimal (everything else inherits from the built-in palette):

```json
{ "schemaVersion": 1, "id": "aurora", "name": "Aurora", "type": "theme",
  "theme": { "colors": { "Accent": "#7B5CFF" } } }
```

Full form adds a base palette and surface colours:

```json
{
  "schemaVersion": 1, "apiVersion": 1,
  "id": "aurora", "name": "Aurora", "version": "1.0.0", "author": "you",
  "type": "theme",
  "theme": {
    "baseFamily": "acrylic",
    "baseAccent": "purple",
    "opacity": 0.92,
    "colors": {
      "Accent": "#7B5CFF", "AccentDim": "#5B3FD6", "AccentAlt": "#9D7BFF",
      "AccentTint": "#337B5CFF", "OnAccent": "#FFFFFF",
      "BgWindow": "#0E0E16", "BgSidebar": "#12121C", "BgCard": "#1A1A26",
      "BgPanel": "#15151F", "BgInput": "#0B0B12",
      "TextPrimary": "#F2F0FF", "TextSecondary": "#B9B4D4", "TextMuted": "#7E7899",
      "BorderSubtle": "#2A2740", "BorderStrong": "#3B3760"
    }
  }
}
```

`baseFamily`: `original` | `acrylic` | `windows`. `baseAccent`: `green` `blue` `red` `white` `purple`.

Overridable keys: `Accent*`, `OnAccent`, `BgWindow` `BgSidebar` `BgCard` `BgCardHover`
`BgPanel` `BgInput`, `TextPrimary` `TextSecondary` `TextMuted`, `BorderSubtle`
`BorderStrong` `BorderHover`, `Danger` `Warn` `WarnText` `WarnCardBg` `WarnCardBorder`.

**Rule**: repainting any `Bg*`/`Border*` requires at least one `Text*`, otherwise the pack
is rejected — an early version shipped white-on-white and made the UI unreadable.

Activation: pick it in the dropdown, then **Save settings** (the palette is merged before
the window exists, so the launcher restarts once).

---

## Plugins (C#)

```xml
<Reference Include="ECL">
  <HintPath>$(EclInstallDir)\ECL.dll</HintPath>
  <Private>false</Private>   <!-- never copy ECL.dll into your addon folder -->
</Reference>
```

```csharp
using ECL.Addons.Api;

public sealed class Entry : IEclAddon, IEclUiExtension
{
    public string Id => "my-plugin";                    // must equal addon.json id
    public void OnLoad(IEclHost host)
    {
        host.Log("hello");
        host.BeforeLaunch += (_, e) => host.Log($"launching {e.VersionId}");
    }
    public void OnUnload() { }

    public System.Windows.FrameworkElement? CreateView(EclUiSlot slot) =>
        slot == EclUiSlot.HomeCard ? new System.Windows.Controls.TextBlock { Text = "hi" } : null;
}
```

`addon.json`:

```json
{ "schemaVersion": 1, "apiVersion": 1, "id": "my-plugin", "name": "My Plugin",
  "version": "1.0.0", "type": "plugin",
  "entry": { "assembly": "MyPlugin.dll", "type": "" },
  "capabilities": ["launch.before", "ui.card"] }
```

### Lifecycle

| Call | When | Thread |
|---|---|---|
| constructor | after the main window exists | UI |
| `OnLoad(host)` | right after construction | UI |
| `BeforeLaunch` handlers | before the game process is created | background |
| `OnUnload()` | launcher shutdown | UI |

`BeforeLaunch` runs on the launch path: return quickly. Exceptions are logged and swallowed.

### UI slots

`HomeCard`, `SettingsCard`, `AccountRow`, `HomeToolbar`. `CreateView` runs on the UI thread
and may be called repeatedly — return a fresh element each time, or `null`.

### Host surface

| Member | Notes |
|---|---|
| `LauncherVersion` | e.g. `1.0.1` |
| `DataRoot` | absolute path of the active data root |
| `Log(string)` | prefixed with the addon id |
| `BeforeLaunch` | `EclLaunchEventArgs { VersionId, GameDir, JvmArgs }` |

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `另有 N 个被拒绝` / rejected count > 0 | manifest validation | read the `addon rejected:` line in `startup.log` |
| `no type implements IEclAddon/IEclUiExtension` | wrong `entry.type`, or interface not implemented | leave `entry.type` empty |
| `as IEclAddon` is always null | you shipped `ECL.dll` with the plugin → two type identities | set `Private=false` and delete the copy |
| theme pack ignored | not selected / not saved | select it, then Save settings (restart) |
| `would break contrast` | background changed without text colours | add at least one `Text*` |

---

## Licence

The launcher is MPL-2.0 ([LICENSE](../LICENSE)). **Your addon is your own work** and may use
any licence. If you modify launcher source files, those files stay MPL-2.0 with notices
intact. Do not bundle `ECL.dll`, game files, or account tokens.

Ready-made example: [`samples/aurora-theme/`](../samples/aurora-theme/addon.json).
