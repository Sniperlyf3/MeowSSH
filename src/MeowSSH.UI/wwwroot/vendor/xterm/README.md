# Vendored xterm.js

- `xterm.js`, `xterm.css` — [@xterm/xterm](https://github.com/xtermjs/xterm.js) 5.5.0
- `addon-fit.js` — @xterm/addon-fit 0.10.0

MIT licensed, copyright The xterm.js authors.

Vendored rather than loaded from a CDN on purpose. The terminal has to render
inside an Android WebView with no network of its own, and an SSH client that
cannot draw its own terminal without reaching the public internet would be broken
exactly when someone is using it to fix a network. It also avoids announcing to a
third party every time the app opens a session.

Update by re-fetching the same paths from `https://cdn.jsdelivr.net/npm/` at a
pinned version, never by tracking `latest`.
