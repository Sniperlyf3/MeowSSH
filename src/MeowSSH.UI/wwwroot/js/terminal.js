/*
  Bridges an xterm.js terminal to the terminal session on the .NET side.

  Output stays byte-oriented and is coalesced on animation frames so large
  bursts do not saturate the Blazor interop bridge. Input also stays as raw
  bytes so escape sequences and split UTF-8 sequences are not rewritten.
*/

const sessions = new Map();
const preferenceEvent = "meowssh-terminal-settings-changed";

const storageKeys = {
    zoom: "meowssh.terminal.zoom",
    theme: "meowssh.terminal.theme",
    cursorStyle: "meowssh.terminal.cursorStyle",
    cursorBlink: "meowssh.terminal.cursorBlink",
    scrollback: "meowssh.terminal.scrollback",
};

const themes = {
    "meow-dark": {
        background: "#0b0e13",
        foreground: "#e7ebf2",
        cursor: "#ff8a5b",
        cursorAccent: "#0b0e13",
        selectionBackground: "rgba(255, 138, 91, 0.28)",
        black: "#12171f", red: "#f2555a", green: "#3fbf8f", yellow: "#e3b341",
        blue: "#59a9ff", magenta: "#c98bdb", cyan: "#4fc4cf", white: "#c8cfdb",
        brightBlack: "#5d6676", brightRed: "#ff7a7e", brightGreen: "#62d6a8",
        brightYellow: "#f0c75e", brightBlue: "#82bfff", brightMagenta: "#dba6e8",
        brightCyan: "#74d8e1", brightWhite: "#f2f5fa",
    },
    "solarized-dark": {
        background: "#002b36", foreground: "#839496", cursor: "#93a1a1", cursorAccent: "#002b36",
        selectionBackground: "rgba(147, 161, 161, 0.28)", black: "#073642", red: "#dc322f",
        green: "#859900", yellow: "#b58900", blue: "#268bd2", magenta: "#d33682",
        cyan: "#2aa198", white: "#eee8d5", brightBlack: "#586e75", brightRed: "#cb4b16",
        brightGreen: "#586e75", brightYellow: "#657b83", brightBlue: "#839496",
        brightMagenta: "#6c71c4", brightCyan: "#93a1a1", brightWhite: "#fdf6e3",
    },
    "solarized-light": {
        background: "#fdf6e3", foreground: "#657b83", cursor: "#586e75", cursorAccent: "#fdf6e3",
        selectionBackground: "rgba(88, 110, 117, 0.22)", black: "#073642", red: "#dc322f",
        green: "#859900", yellow: "#b58900", blue: "#268bd2", magenta: "#d33682",
        cyan: "#2aa198", white: "#eee8d5", brightBlack: "#586e75", brightRed: "#cb4b16",
        brightGreen: "#586e75", brightYellow: "#657b83", brightBlue: "#839496",
        brightMagenta: "#6c71c4", brightCyan: "#93a1a1", brightWhite: "#fdf6e3",
    },
    dracula: {
        background: "#282a36", foreground: "#f8f8f2", cursor: "#f8f8f2", cursorAccent: "#282a36",
        selectionBackground: "rgba(68, 71, 90, 0.65)", black: "#21222c", red: "#ff5555",
        green: "#50fa7b", yellow: "#f1fa8c", blue: "#bd93f9", magenta: "#ff79c6",
        cyan: "#8be9fd", white: "#f8f8f2", brightBlack: "#6272a4", brightRed: "#ff6e6e",
        brightGreen: "#69ff94", brightYellow: "#ffffa5", brightBlue: "#d6acff",
        brightMagenta: "#ff92df", brightCyan: "#a4ffff", brightWhite: "#ffffff",
    },
};

function boundedInteger(key, fallback, min, max) {
    const value = Number.parseInt(localStorage.getItem(key) ?? "", 10);
    return Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : fallback;
}

function booleanPreference(key, fallback) {
    const value = localStorage.getItem(key);
    if (value === null) return fallback;
    return value === "true";
}

export function getPreferences() {
    const requestedTheme = localStorage.getItem(storageKeys.theme) ?? "meow-dark";
    const requestedCursor = localStorage.getItem(storageKeys.cursorStyle) ?? "block";
    return {
        zoom: boundedInteger(storageKeys.zoom, 100, 80, 160),
        theme: Object.hasOwn(themes, requestedTheme) ? requestedTheme : "meow-dark",
        cursorStyle: ["block", "bar", "underline"].includes(requestedCursor) ? requestedCursor : "block",
        cursorBlink: booleanPreference(storageKeys.cursorBlink, true),
        scrollback: boundedInteger(storageKeys.scrollback, 5000, 1000, 50000),
    };
}

export function setPreference(name, value) {
    const key = storageKeys[name];
    if (!key) throw new Error(`Unknown terminal preference: ${name}`);
    localStorage.setItem(key, String(value));
    window.dispatchEvent(new CustomEvent(preferenceEvent));
}

export function resetPreferences() {
    for (const key of Object.values(storageKeys)) localStorage.removeItem(key);
    window.dispatchEvent(new CustomEvent(preferenceEvent));
    return getPreferences();
}

function applyPreferences(session) {
    const preferences = getPreferences();
    const terminal = session.terminal;
    terminal.options.fontSize = session.baseFontSize * preferences.zoom / 100;
    terminal.options.theme = themes[preferences.theme];
    terminal.options.cursorStyle = preferences.cursorStyle;
    terminal.options.cursorBlink = preferences.cursorBlink;
    terminal.options.scrollback = preferences.scrollback;
    try { session.fit.fit(); } catch { /* element can detach during navigation */ }
}

export function create(elementId, dotNetRef, options) {
    const element = document.getElementById(elementId);
    if (!element) throw new Error(`Terminal container "${elementId}" is not in the document.`);

    const preferences = getPreferences();
    const terminal = new window.Terminal({
        allowProposedApi: true,
        cursorBlink: preferences.cursorBlink,
        cursorStyle: preferences.cursorStyle,
        fontFamily: options.fontFamily,
        fontSize: options.fontSize * preferences.zoom / 100,
        letterSpacing: 0,
        scrollback: preferences.scrollback,
        theme: themes[preferences.theme],
        screenReaderMode: false,
    });

    const fit = new window.FitAddon.FitAddon();
    terminal.loadAddon(fit);
    terminal.open(element);
    fit.fit();

    const textarea = element.querySelector(".xterm-helper-textarea");
    if (textarea) {
        textarea.setAttribute("autocomplete", "off");
        textarea.setAttribute("enterkeyhint", "send");
    }

    const encoder = new TextEncoder();
    terminal.onData(data => dotNetRef.invokeMethodAsync("OnInputAsync", encoder.encode(data)));
    terminal.onResize(({ cols, rows }) => dotNetRef.invokeMethodAsync("OnResizeAsync", cols, rows));

    const observer = new ResizeObserver(() => {
        try { fit.fit(); } catch { /* element detached mid-layout */ }
    });
    observer.observe(element);

    const keepFocus = () => {
        if (!element.contains(document.activeElement)) return;
        requestAnimationFrame(() => terminal.focus());
    };

    const viewport = window.visualViewport;
    viewport?.addEventListener("resize", keepFocus);

    const preferenceListener = () => {
        const session = sessions.get(elementId);
        if (!session) return;
        applyPreferences(session);
    };
    window.addEventListener(preferenceEvent, preferenceListener);

    sessions.set(elementId, {
        terminal, fit, observer, viewport, keepFocus, preferenceListener, dotNetRef, encoder,
        baseFontSize: options.fontSize, pending: [], frame: 0,
    });
    return { cols: terminal.cols, rows: terminal.rows };
}

export function write(elementId, bytes) {
    const session = sessions.get(elementId);
    if (!session) return;

    session.pending.push(bytes);
    if (session.frame !== 0) return;

    session.frame = requestAnimationFrame(() => {
        session.frame = 0;
        const queued = session.pending;
        session.pending = [];

        let total = 0;
        for (const chunk of queued) total += chunk.length;
        const merged = new Uint8Array(total);
        let offset = 0;
        for (const chunk of queued) {
            merged.set(chunk, offset);
            offset += chunk.length;
        }
        session.terminal.write(merged);
    });
}

export function focus(elementId) {
    sessions.get(elementId)?.terminal.focus();
}

export function fit(elementId) {
    const session = sessions.get(elementId);
    if (!session) return null;
    try { session.fit.fit(); } catch { return null; }
    return { cols: session.terminal.cols, rows: session.terminal.rows };
}

export async function copySelection(elementId) {
    const session = sessions.get(elementId);
    if (!session) return false;
    const text = session.terminal.getSelection();
    if (!text) return false;
    try {
        await navigator.clipboard.writeText(text);
        session.terminal.focus();
        return true;
    } catch {
        session.terminal.focus();
        return false;
    }
}

export async function pasteClipboard(elementId) {
    const session = sessions.get(elementId);
    if (!session) return false;
    try {
        const text = await navigator.clipboard.readText();
        if (!text) return false;
        await session.dotNetRef.invokeMethodAsync("OnInputAsync", session.encoder.encode(text));
        session.terminal.focus();
        return true;
    } catch {
        session.terminal.focus();
        return false;
    }
}

export function clearTerminal(elementId) {
    const session = sessions.get(elementId);
    if (!session) return;
    session.terminal.clear();
    session.terminal.focus();
}

export function dispose(elementId) {
    const session = sessions.get(elementId);
    if (!session) return;
    if (session.frame !== 0) cancelAnimationFrame(session.frame);
    session.observer.disconnect();
    session.viewport?.removeEventListener("resize", session.keepFocus);
    window.removeEventListener(preferenceEvent, session.preferenceListener);
    session.terminal.dispose();
    sessions.delete(elementId);
}
