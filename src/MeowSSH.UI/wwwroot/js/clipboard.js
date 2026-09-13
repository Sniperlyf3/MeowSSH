/*
  Copying to the clipboard from an Android web view.

  navigator.clipboard needs a secure context and is not present in every
  embedded web view, so the older selection-based path is kept as a fallback.
  Both can fail -- a view can deny the permission outright -- and the caller is
  expected to cope, because the text is on screen either way.
*/

export async function copyText(text) {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Fall through: a denied permission is not a reason to give up entirely.
    }
  }

  const staging = document.createElement('textarea');
  staging.value = text;
  // Off-screen rather than hidden: display:none elements cannot be selected,
  // and the selection is what the fallback copies from.
  staging.setAttribute('readonly', '');
  staging.style.position = 'fixed';
  staging.style.top = '-1000px';
  document.body.appendChild(staging);

  try {
    staging.select();
    return document.execCommand('copy');
  } catch {
    return false;
  } finally {
    staging.remove();
  }
}
