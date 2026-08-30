# Web accessibility foundations

Import reusable helpers from `src/accessibility` rather than adding document-wide
listeners or one-off live regions.

## Foundations

- `LiveAnnouncerProvider`, `useLiveAnnouncer`, and `LiveStatus` provide stable
  polite/assertive announcements and busy states.
- `captureFocusForRestore` and `useDialogFocusTrap` handle initial focus, Tab and
  Shift+Tab containment, Escape dismissal, and logical focus restoration.
- `useReducedMotion` and `getMotionDuration` let features replace nonessential
  motion instead of relying only on global CSS.
- `useKeyboardGrid` implements roving focus, RTL-aware arrows, Home/End,
  Control/Command+Home/End, and PageUp/PageDown. `getPreservedGridRows` expands
  a virtualized render range so the focused row stays mounted.
- `VisuallyHidden` adds screen-reader text without changing visual layout.
- `auditAccessibilityTree`, contrast/token, target-size, and reflow utilities
  provide deterministic checks for owned fixtures.

The project currently has no `axe-core`, `@axe-core/react`, or `vitest-axe`
dependency. Add an axe-backed suite only through a separately reviewed package
manifest change; the owned semantic audit is intentionally not presented as a
replacement for axe.

## Manual primary-flow checks

Run these against each feature that composes the helpers:

1. Keyboard-only: traverse every control, operate menus and grids, close dialogs
   with Escape, and confirm focus returns to the invoking control.
2. NVDA with Firefox: verify headings, control names, selection/status updates,
   dialog boundaries, and the paged/list alternative to any grid.
3. VoiceOver with Safari: repeat primary browsing, selection, upload, and
   deletion flows; verify urgent failures interrupt while progress stays polite.
4. Zoom/reflow: test 320 CSS pixels and 400% zoom without two-dimensional
   scrolling, clipped focus indicators, or obscured controls.
5. Motion: enable reduced motion and confirm transitions are removed or replaced
   without hiding state changes.
6. Touch: confirm primary mobile actions are at least 44×44 CSS pixels and other
   targets meet WCAG 2.2 minimum sizing or its spacing exceptions.
7. Contrast: audit all foreground/background token pairs, focus indicators, and
   component states; do not infer contrast from token names.

The automated owned baseline must keep zero `serious` or `critical` findings.
Run it with:

```bash
npm --prefix src/Vistara.Web run test -- --run src/accessibility
```

The roadmap's planned `test:a11y` alias requires a package-manifest change
outside this directory's ownership.
