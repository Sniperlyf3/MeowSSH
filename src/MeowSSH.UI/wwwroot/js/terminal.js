/*
  Bridges an xterm.js terminal to the terminal session on the .NET side.

  Output stays byte-oriented and is coalesced on animation frames so large
  bursts do not saturate the Blazor interop bridge. Input also stays as raw
  bytes so escape sequences and split UTF-8 sequences are not rewritten.
*/

import { copyText } from "./clipboard.js";

const sessions = new Map();
const preferenceEvent = "meowssh-terminal-settings-changed";

const storageKeys = {
    zoom: "meowssh.terminal.zoom",
    theme: "meowssh.terminal.theme",
    cursorStyle: "meowssh.terminal.cursorStyle",
    cursorBlink: "meowssh.terminal.cursorBlink",
    scrollback: "meowssh.terminal.scrollback",
    customTheme: "meowssh.terminal.customTheme",
};

const defaultThemeId = "meow-dark";
const customThemeId = "custom";

// Pro-only (PremiumFeature.PremiumCustomization), along with the custom theme
// and per-host themes. Enforced here, where the theme is applied, not just by
// the Appearance page: a stored premium choice from a lapsed or refunded
// purchase renders as the default, and returns untouched if Pro does.
const premiumThemeIds = new Set(["nord", "gruvbox-dark", "one-dark", "tokyo-night", "production-red"]);
let premiumAllowed = false;
const hexColor = /^#[0-9a-f]{6}$/i;

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
    nord: {
        background: "#2e3440", foreground: "#d8dee9", cursor: "#d8dee9", cursorAccent: "#2e3440",
        selectionBackground: "rgba(136, 192, 208, 0.25)", black: "#3b4252", red: "#bf616a",
        green: "#a3be8c", yellow: "#ebcb8b", blue: "#81a1c1", magenta: "#b48ead",
        cyan: "#88c0d0", white: "#e5e9f0", brightBlack: "#4c566a", brightRed: "#bf616a",
        brightGreen: "#a3be8c", brightYellow: "#ebcb8b", brightBlue: "#81a1c1",
        brightMagenta: "#b48ead", brightCyan: "#8fbcbb", brightWhite: "#eceff4",
    },
    "gruvbox-dark": {
        background: "#282828", foreground: "#ebdbb2", cursor: "#ebdbb2", cursorAccent: "#282828",
        selectionBackground: "rgba(235, 219, 178, 0.22)", black: "#282828", red: "#cc241d",
        green: "#98971a", yellow: "#d79921", blue: "#458588", magenta: "#b16286",
        cyan: "#689d6a", white: "#a89984", brightBlack: "#928374", brightRed: "#fb4934",
        brightGreen: "#b8bb26", brightYellow: "#fabd2f", brightBlue: "#83a598",
        brightMagenta: "#d3869b", brightCyan: "#8ec07c", brightWhite: "#ebdbb2",
    },
    "one-dark": {
        background: "#282c34", foreground: "#abb2bf", cursor: "#528bff", cursorAccent: "#282c34",
        selectionBackground: "rgba(97, 175, 239, 0.25)", black: "#282c34", red: "#e06c75",
        green: "#98c379", yellow: "#e5c07b", blue: "#61afef", magenta: "#c678dd",
        cyan: "#56b6c2", white: "#abb2bf", brightBlack: "#5c6370", brightRed: "#e06c75",
        brightGreen: "#98c379", brightYellow: "#e5c07b", brightBlue: "#61afef",
        brightMagenta: "#c678dd", brightCyan: "#56b6c2", brightWhite: "#ffffff",
    },
    "tokyo-night": {
        background: "#1a1b26", foreground: "#c0caf5", cursor: "#c0caf5", cursorAccent: "#1a1b26",
        selectionBackground: "rgba(122, 162, 247, 0.25)", black: "#15161e", red: "#f7768e",
        green: "#9ece6a", yellow: "#e0af68", blue: "#7aa2f7", magenta: "#bb9af7",
        cyan: "#7dcfff", white: "#a9b1d6", brightBlack: "#414868", brightRed: "#f7768e",
        brightGreen: "#9ece6a", brightYellow: "#e0af68", brightBlue: "#7aa2f7",
        brightMagenta: "#bb9af7", brightCyan: "#7dcfff", brightWhite: "#c0caf5",
    },
    // Meant for per-host use: a session that is unmistakably production.
    "production-red": {
        background: "#2a0f12", foreground: "#f5e1e3", cursor: "#ff5c5c", cursorAccent: "#2a0f12",
        selectionBackground: "rgba(255, 92, 92, 0.30)", black: "#1a0709", red: "#ff5c5c",
        green: "#7fd18b", yellow: "#f2c66d", blue: "#7aa9ff", magenta: "#e38bd6",
        cyan: "#6fd6d6", white: "#e8d3d5", brightBlack: "#6e3a40", brightRed: "#ff8080",
        brightGreen: "#9be3a6", brightYellow: "#f7d890", brightBlue: "#9dc0ff",
        brightMagenta: "#eeaae5", brightCyan: "#93e4e4", brightWhite: "#fff5f6",
    },
};

function isKnownTheme(id) {
    return id === customThemeId || Object.hasOwn(themes, id);
}

function isPremiumTheme(id) {
    return id === customThemeId || premiumThemeIds.has(id);
}

/** Called with the app's entitlement by every page that creates or configures a terminal. */
export function setPremiumAllowed(value) {
    const next = value === true;
    if (next === premiumAllowed) return;
    premiumAllowed = next;
    window.dispatchEvent(new CustomEvent(preferenceEvent));
}

/**
 * Everything the Appearance page needs in one interop round trip. Each extra
 * await before the page has its state is a window in which a user's change
 * can be overwritten by the late load (SettingsTabPersistsTerminalZoom caught
 * exactly that when this took three calls).
 */
export function initializeAppearance(premium) {
    setPremiumAllowed(premium);
    return { preferences: getPreferences(), customTheme: getCustomTheme() };
}

export function getCustomTheme() {
    const fallback = {
        base: defaultThemeId,
        background: themes[defaultThemeId].background,
        foreground: themes[defaultThemeId].foreground,
        cursor: themes[defaultThemeId].cursor,
    };
    try {
        const stored = JSON.parse(localStorage.getItem(storageKeys.customTheme) ?? "null");
        if (!stored) return fallback;
        return {
            base: Object.hasOwn(themes, stored.base) ? stored.base : fallback.base,
            background: hexColor.test(stored.background) ? stored.background : fallback.background,
            foreground: hexColor.test(stored.foreground) ? stored.foreground : fallback.foreground,
            cursor: hexColor.test(stored.cursor) ? stored.cursor : fallback.cursor,
        };
    } catch {
        return fallback;
    }
}

export function setCustomTheme(value) {
    if (!value || !Object.hasOwn(themes, value.base) ||
        ![value.background, value.foreground, value.cursor].every(color => hexColor.test(color))) {
        throw new Error("A custom theme needs a built-in base and three #rrggbb colours.");
    }
    localStorage.setItem(storageKeys.customTheme, JSON.stringify({
        base: value.base, background: value.background, foreground: value.foreground, cursor: value.cursor,
    }));
    window.dispatchEvent(new CustomEvent(preferenceEvent));
}

/** The id that actually renders for a requested theme, after the Pro gate. */
function effectiveThemeId(id) {
    if (!isKnownTheme(id)) return defaultThemeId;
    return isPremiumTheme(id) && !premiumAllowed ? defaultThemeId : id;
}

function resolveTheme(id) {
    const effective = effectiveThemeId(id);
    if (effective !== customThemeId) return themes[effective];
    const custom = getCustomTheme();
    return {
        ...themes[custom.base],
        background: custom.background,
        foreground: custom.foreground,
        cursor: custom.cursor,
        cursorAccent: custom.background,
    };
}

/** A per-host theme is itself a Pro feature, whichever theme it names. */
function sessionThemeId(session, preferences) {
    return premiumAllowed && session.hostTheme && isKnownTheme(session.hostTheme)
        ? session.hostTheme
        : preferences.theme;
}

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
    const requestedTheme = localStorage.getItem(storageKeys.theme) ?? defaultThemeId;
    const requestedCursor = localStorage.getItem(storageKeys.cursorStyle) ?? "block";
    const theme = isKnownTheme(requestedTheme) ? requestedTheme : defaultThemeId;
    return {
        zoom: boundedInteger(storageKeys.zoom, 100, 80, 160),
        theme,
        effectiveTheme: effectiveThemeId(theme),
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
    terminal.options.theme = resolveTheme(sessionThemeId(session, preferences));
    terminal.options.cursorStyle = preferences.cursorStyle;
    terminal.options.cursorBlink = preferences.cursorBlink;
    terminal.options.scrollback = preferences.scrollback;
    try { session.fit.fit(); } catch { /* element can detach during navigation */ }
}

export function create(elementId, dotNetRef, options) {
    const element = document.getElementById(elementId);
    if (!element) throw new Error(`Terminal container "${elementId}" is not in the document.`);

    setPremiumAllowed(options.premium === true);
    const hostTheme = typeof options.hostTheme === "string" ? options.hostTheme : null;
    const preferences = getPreferences();
    const terminal = new window.Terminal({
        allowProposedApi: true,
        cursorBlink: preferences.cursorBlink,
        cursorStyle: preferences.cursorStyle,
        fontFamily: options.fontFamily,
        fontSize: options.fontSize * preferences.zoom / 100,
        letterSpacing: 0,
        scrollback: preferences.scrollback,
        theme: resolveTheme(sessionThemeId({ hostTheme }, preferences)),
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

    const touchSelection = attachTouchSelection(element, terminal);
    terminal.onSelectionChange(() =>
        dotNetRef.invokeMethodAsync("OnSelectionChangedAsync", terminal.hasSelection()).catch(() => { /* circuit gone */ }));

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
        terminal, fit, observer, viewport, keepFocus, preferenceListener, dotNetRef, encoder, touchSelection,
        baseFontSize: options.fontSize, hostTheme, pending: [], frame: 0,
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

/**
 * Copies the selection, or the visible screen when nothing is selected --
 * on a phone the whole screen is often exactly what is wanted, and it needs
 * no selection gesture at all.
 * @returns {"selection"|"screen"|"empty"|"failed"} what happened, for the caller to say.
 */
export async function copySelection(elementId) {
    const session = sessions.get(elementId);
    if (!session) return "failed";
    const terminal = session.terminal;
    const fromSelection = terminal.hasSelection();
    const text = fromSelection ? terminal.getSelection() : visibleText(terminal);
    if (!text.trim()) return "empty";
    // clipboard.js, not navigator.clipboard directly: the Android web view
    // does not always offer it, and the selection-based fallback there works.
    const copied = await copyText(text);
    if (copied && fromSelection) terminal.clearSelection();
    terminal.focus();
    return copied ? (fromSelection ? "selection" : "screen") : "failed";
}

function visibleText(terminal) {
    const buffer = terminal.buffer.active;
    const lines = [];
    for (let row = 0; row < terminal.rows; row++) {
        lines.push(buffer.getLine(buffer.viewportY + row)?.translateToString(true) ?? "");
    }
    return lines.join("\n").replace(/\s+$/, "");
}

const longPressMs = 450;
const moveTolerancePx = 10;

/**
 * Touch selection. xterm.js selects only with a mouse: on a phone a finger
 * scrolls, and a long press becomes a context menu -- for which xterm moves
 * its hidden input textarea under the finger, so Android offers that
 * textarea's paste and password-autofill popup and nothing can be selected
 * or copied. Here a long press selects the word under the finger instead,
 * dragging after it extends the selection, and a tap clears it.
 */
function attachTouchSelection(element, terminal) {
    let timer = 0;
    let start = null;
    let anchor = null; // { start, end } cell indexes of the long-pressed word
    let selecting = false;

    const cellAt = (x, y) => {
        const screen = element.querySelector(".xterm-screen");
        if (!screen) return null;
        const rect = screen.getBoundingClientRect();
        const col = Math.min(terminal.cols - 1, Math.max(0, Math.floor((x - rect.left) / (rect.width / terminal.cols))));
        const row = Math.min(terminal.rows - 1, Math.max(0, Math.floor((y - rect.top) / (rect.height / terminal.rows))));
        return { col, row: terminal.buffer.active.viewportY + row };
    };

    const index = cell => cell.row * terminal.cols + cell.col;

    const selectRange = (from, to) => {
        const first = Math.min(from, to);
        terminal.select(first % terminal.cols, Math.floor(first / terminal.cols), Math.abs(to - from) + 1);
    };

    // Whitespace-delimited, not xterm's word rule: what people copy from a
    // shell is a path, a host, an IP or a hash, all of which contain
    // punctuation a "word" would stop at.
    const wordAt = cell => {
        const text = terminal.buffer.active.getLine(cell.row)?.translateToString(false) ?? "";
        const base = cell.row * terminal.cols;
        if (!text[cell.col] || /\s/.test(text[cell.col])) return { start: index(cell), end: index(cell) };
        let from = cell.col;
        let to = cell.col;
        while (from > 0 && !/\s/.test(text[from - 1])) from--;
        while (to < text.length - 1 && !/\s/.test(text[to + 1])) to++;
        return { start: base + from, end: base + to };
    };

    const cancel = () => {
        clearTimeout(timer);
        timer = 0;
    };

    element.addEventListener("touchstart", event => {
        cancel();
        if (event.touches.length !== 1) return;
        const touch = event.touches[0];
        start = { x: touch.clientX, y: touch.clientY, moved: false };
        timer = setTimeout(() => {
            timer = 0;
            const cell = cellAt(start.x, start.y);
            if (!cell) return;
            anchor = wordAt(cell);
            selecting = true;
            selectRange(anchor.start, anchor.end);
            navigator.vibrate?.(10);
        }, longPressMs);
    }, { passive: true });

    element.addEventListener("touchmove", event => {
        const touch = event.touches[0];
        if (!touch || !start) return;
        if (!selecting) {
            if (Math.hypot(touch.clientX - start.x, touch.clientY - start.y) > moveTolerancePx) {
                start.moved = true;
                cancel();
            }
            return;
        }
        // Dragging extends the selection instead of scrolling the terminal.
        event.preventDefault();
        const cell = cellAt(touch.clientX, touch.clientY);
        if (!cell) return;
        const at = index(cell);
        if (at >= anchor.start) selectRange(anchor.start, Math.max(at, anchor.end));
        else selectRange(at, anchor.end);
    }, { passive: false });

    const end = () => {
        const wasTap = timer !== 0 && start && !start.moved;
        cancel();
        if (selecting) {
            selecting = false;
        } else if (wasTap && terminal.hasSelection()) {
            terminal.clearSelection();
        }
        start = null;
    };
    element.addEventListener("touchend", end);
    element.addEventListener("touchcancel", end);

    // Capture phase, before xterm sees it and moves its textarea under the
    // finger: the popup that follows is the paste/autofill menu for that
    // textarea, never a selection.
    const suppressContextMenu = event => event.preventDefault();
    element.addEventListener("contextmenu", suppressContextMenu, true);

    return { cancel };
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

/**
 * What Ask AI offers to send: the xterm selection if there is one, otherwise
 * the last lines of the screen. Returned for the user to review and edit --
 * nothing here sends anything anywhere.
 */
export function getAssistContext(elementId, maxLines) {
    const session = sessions.get(elementId);
    if (!session) return { selection: "", recent: "" };
    const terminal = session.terminal;
    const selection = terminal.hasSelection() ? terminal.getSelection() : "";

    const buffer = terminal.buffer.active;
    const end = buffer.baseY + buffer.cursorY;
    const lines = [];
    for (let i = Math.max(0, end - maxLines + 1); i <= end; i++) {
        lines.push(buffer.getLine(i)?.translateToString(true) ?? "");
    }
    return { selection, recent: lines.join("\n").replace(/\s+$/, "") };
}

/**
 * Types an AI-suggested command at the prompt without running it. Through
 * xterm's own paste, so a shell with bracketed paste on treats it as pasted
 * text; the caller passes one line only, so there is no newline to execute.
 */
export function insertText(elementId, text) {
    const session = sessions.get(elementId);
    if (!session) return false;
    session.terminal.paste(text.replace(/[\r\n]+/g, ""));
    session.terminal.focus();
    return true;
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
    session.touchSelection.cancel();
    session.observer.disconnect();
    session.viewport?.removeEventListener("resize", session.keepFocus);
    window.removeEventListener(preferenceEvent, session.preferenceListener);
    session.terminal.dispose();
    sessions.delete(elementId);
}
