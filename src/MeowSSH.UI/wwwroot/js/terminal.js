/*
  Bridges an xterm.js terminal to the SSH shell on the .NET side.

  Two things here are load-bearing and not obvious:

  1. Output is batched before it crosses into .NET's renderer. A command like
     `cat` on a large file delivers thousands of small chunks, and marshalling
     each one individually saturates the interop bridge and locks the UI. They
     are coalesced on an animation frame instead, which is also the fastest the
     screen could show them anyway.

  2. Input is sent as raw bytes, not as a string. Terminal input is a byte
     stream -- escape sequences, control characters, partial UTF-8 from a fast
     paste -- and re-encoding it through a JS string mangles anything that is
     not valid text.
*/

const sessions = new Map();

function readZoomPercent() {
    const raw = Number.parseInt(localStorage.getItem("meowssh.terminal.zoom") ?? "100", 10);
    return Number.isFinite(raw) ? Math.min(160, Math.max(80, raw)) : 100;
}

export function create(elementId, dotNetRef, options) {
    const element = document.getElementById(elementId);
    if (!element) throw new Error(`Terminal container "${elementId}" is not in the document.`);

    const terminal = new window.Terminal({
        allowProposedApi: true,
        cursorBlink: true,
        // Match the app's own mono face so the terminal does not read as a
        // foreign widget dropped into the page.
        fontFamily: options.fontFamily,
        fontSize: options.fontSize * readZoomPercent() / 100,
        letterSpacing: 0,
        scrollback: 5000,
        theme: options.theme,
        // A phone keyboard's autocorrect would otherwise rewrite shell commands.
        screenReaderMode: false,
    });

    const fit = new window.FitAddon.FitAddon();
    terminal.loadAddon(fit);
    terminal.open(element);
    fit.fit();

    // xterm already asks for no autocorrect, no autocapitalise and no
    // spellcheck. Gboard composes anyway: it holds the word being typed as
    // uncommitted composition text, shows it over the terminal, and leaves the
    // real cursor at the last committed position -- which is why the caret sat
    // thin and one word behind until space committed it.
    //
    // The vendored xterm helper is a hidden password-style input. Do not set an
    // inputmode here: on Android/Samsung that can override the password editor
    // classification and bring IME composition back even when suggestions are
    // hidden. Let type="password" be the strongest signal to the keyboard.
    const textarea = element.querySelector(".xterm-helper-textarea");
    if (textarea) {
        textarea.setAttribute("autocomplete", "off");
        textarea.setAttribute("enterkeyhint", "send");
    }

    const encoder = new TextEncoder();
    terminal.onData(data => {
        // The Uint8Array goes across as-is. Blazor has optimized byte-array
        // interop in both directions; Array.from() would turn this into a plain
        // JS array, which does not bind to a byte[] parameter and throws once
        // per keystroke.
        dotNetRef.invokeMethodAsync("OnInputAsync", encoder.encode(data));
    });

    // xterm reports the size it actually achieved after fitting, which is what
    // the remote pty must be told -- not the size we asked for.
    terminal.onResize(({ cols, rows }) => {
        dotNetRef.invokeMethodAsync("OnResizeAsync", cols, rows);
    });

    const observer = new ResizeObserver(() => {
        try { fit.fit(); } catch { /* element detached mid-layout */ }
    });
    observer.observe(element);

    // A soft keyboard opening resizes the window, and on the way through that
    // the terminal can end up blurred -- at which point xterm draws the thin
    // inactive cursor and stops moving it, so the caret sits where it was before
    // you started typing even though the characters arrive. Refocusing after the
    // layout settles keeps the caret where the text is.
    //
    // Only when the terminal already had focus: stealing it back from a dialog
    // that opened over the session would be worse than a misdrawn cursor.
    const keepFocus = () => {
        if (!element.contains(document.activeElement)) return;
        requestAnimationFrame(() => terminal.focus());
    };

    // visualViewport is what actually reports a keyboard on Android; a resize
    // event on window does not fire reliably when the window is only panned.
    const viewport = window.visualViewport;
    viewport?.addEventListener("resize", keepFocus);

    sessions.set(elementId, { terminal, fit, observer, viewport, keepFocus, pending: [], frame: 0 });
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
        // Write the bytes, not a decoded string: xterm's own UTF-8 decoder
        // handles a multi-byte character split across two network chunks.
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

export function setTheme(elementId, theme) {
    const session = sessions.get(elementId);
    if (session) session.terminal.options.theme = theme;
}

export function dispose(elementId) {
    const session = sessions.get(elementId);
    if (!session) return;
    if (session.frame !== 0) cancelAnimationFrame(session.frame);
    session.observer.disconnect();
    // visualViewport outlives the page, so a listener left on it holds the
    // disposed terminal alive and refocuses something that no longer exists.
    session.viewport?.removeEventListener("resize", session.keepFocus);
    session.terminal.dispose();
    sessions.delete(elementId);
}
