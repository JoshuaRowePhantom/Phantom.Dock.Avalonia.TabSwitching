# Phantom.Dock.Avalonia.TabSwitching

A reusable, separately-releasable Avalonia library that adds **numeric keyboard tab-switching**
(e.g. `Alt+1`..`Alt+0`) and **on-tab number badges** to an arbitrary
[Avalonia.Dock](https://github.com/wieslawsoltes/Dock) docking area, decoupled from any particular
view-model.

It depends only on **Avalonia** and **Dock.Avalonia** — never on any Phantom.Workspaces type — so it
can be released independently as the NuGet package `Phantom.Dock.Avalonia.TabSwitching`.

See the design document `docs/design/dock-tab-switching-api.md` (on the `design` branch of
Phantom.Workspaces) and epic #1073 for the full API and roadmap.

## Status

Scaffold + skeleton. The public `DockTabSwitch` attached properties and the per-`DockControl`
controller are being built incrementally under the #1073 epic; gestures, scope resolution, badge
theming, numbering, and header integration land in later commits.
