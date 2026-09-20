// C2 (host name truncation): a fixed-width column can only give a long host
// name so much room, so once it is truncated the rest of the name is only
// readable if it comes to the user rather than the other way around. This
// scrolls it into view when the row is focused (keyboard/AT navigation) or
// selected (its manage panel is opened), and only then -- never on hover,
// which would fight the vertically scrolling host list for the same
// pointer gesture.
//
// Installed once, globally, via a document-level delegated listener rather
// than one listener per row: HostsPage and FilesLandingPage both mount many
// HostRow instances, and rows come and go as the directory changes, so a
// per-row listener would need its own attach/detach lifecycle for no benefit
// -- focusin/focusout already bubble to document, and installHostNameMarquee
// is idempotent, so every HostRow can call it on first render without
// double-attaching.
let installed = false;

function reducedMotion() {
    try {
        return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    } catch {
        // matchMedia is unavailable in some embedded WebViews; failing closed
        // (no motion) is the safe default, not failing the whole feature.
        return true;
    }
}

function hostButtonOf(element) {
    return element instanceof Element ? element.closest(".host") : null;
}

function activate(hostButton) {
    if (!(hostButton instanceof HTMLElement) || reducedMotion()) return;

    const nameEl = hostButton.querySelector(".host__name");
    const textEl = nameEl && nameEl.querySelector(".host__name-text");
    if (!nameEl || !textEl) return;

    // Measure the overflow on .host__name itself, never on .host__name-text.
    // The text span is display:inline in the resting state (so the parent's
    // text-overflow:ellipsis keeps working, which it does not do over an
    // inline-block child), and scrollWidth is defined as 0 for a non-replaced
    // inline box -- reading it here made `distance` permanently negative and
    // this whole function a no-op. The parent's own scrollWidth-clientWidth
    // is the distance to travel anyway, and it is measured before the class
    // below changes the child's display, so the layout it describes is the
    // resting one.
    const distance = nameEl.scrollWidth - nameEl.clientWidth;

    // Nothing to scroll: the name already fits, so leave the ellipsis (if
    // any) alone rather than starting a pointless animation.
    if (distance <= 0) return;

    nameEl.style.setProperty("--marquee-distance", `${distance}px`);
    nameEl.classList.add("host__name--marquee");
}

function deactivate(hostButton) {
    const nameEl = hostButton instanceof HTMLElement && hostButton.querySelector(".host__name");
    if (!nameEl) return;
    nameEl.classList.remove("host__name--marquee");
    nameEl.style.removeProperty("--marquee-distance");
}

function onFocusIn(event) {
    const hostButton = hostButtonOf(event.target);
    if (hostButton) activate(hostButton);
}

function onFocusOut(event) {
    const hostButton = hostButtonOf(event.target);
    if (hostButton) deactivate(hostButton);
}

// "Selected" for a host row is its manage panel being open
// (HostRow.razor's aria-expanded on the manage trigger) -- the app has no
// separate multi-select state on this list. Watching the attribute (rather
// than, say, a click listener on the trigger) means this reacts correctly
// however the state changes, including Blazor re-rendering the row.
function onManageExpandedChanged(mutations) {
    for (const mutation of mutations) {
        if (mutation.type !== "attributes" || mutation.attributeName !== "aria-expanded") continue;
        const trigger = mutation.target;
        if (!(trigger instanceof HTMLElement)) continue;

        const wrap = trigger.closest(".host-wrap");
        const hostButton = wrap && wrap.querySelector(".host");
        if (!hostButton) continue;

        if (trigger.getAttribute("aria-expanded") === "true") activate(hostButton);
        else deactivate(hostButton);
    }
}

export function installHostNameMarquee() {
    if (installed) return;
    installed = true;

    document.addEventListener("focusin", onFocusIn);
    document.addEventListener("focusout", onFocusOut);

    new MutationObserver(onManageExpandedChanged).observe(document.body, {
        attributes: true,
        attributeFilter: ["aria-expanded"],
        subtree: true,
    });
}
