# R / Cut mark

The portfolio owner's selected mark is a single geometric **R**, with a lime
incision in its diagonal leg. It identifies the portfolio, not the R AI assistant.

- `frontend/src/shared/brand/brand-mark.tsx` is the inline header mark, reused by
  the home and project pages. Its body inherits `currentColor`; the accent is
  the existing `#c7ff36`. The mark has a `128 x 128` view box and no font dependency.
- The surrounding home link owns the accessible name, `Rafael portfolio home`.
  The SVG is decorative, hidden from assistive technology and not focusable.
  The link retains a visible keyboard-focus outline and a 48-pixel target.
- `frontend/src/app/icon.svg` uses the same paths in paper white on an ink tile,
  with the lime accent. The tile works on both light and dark browser chrome.
- `frontend/src/app/favicon.ico` is the raster fallback exported from that SVG,
  with 16, 32 and 48 pixel PNG frames. Re-export it whenever the icon changes;
  do not keep the framework's starter favicon.

The brand tests compare the SVG paths, verify both header consumers, and check
the ICO frame structure. Build both Next.js and Vinext after an icon change,
then check the actual icon links emitted in HTML, their status and MIME type.
Remote dev icons must remain behind the existing Worker admission gate.
