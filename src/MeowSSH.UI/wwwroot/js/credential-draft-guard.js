let installed = false;

function fieldValue(testId) {
    const element = document.querySelector(`[data-testid="${testId}"]`);
    return element && 'value' in element ? String(element.value ?? '') : '';
}

function hasGeneratedMaterial() {
    return Boolean(
        document.querySelector('[data-testid="generated-key-ready"]') ||
        document.querySelector('[data-testid="generated-hardware-key-status"]') ||
        document.querySelector('[data-testid="generated-hardware-public-key"]'));
}

function hasAnyCredentialDraft() {
    return fieldValue('credential-label').trim().length > 0 ||
        fieldValue('credential-username').trim().length > 0 ||
        fieldValue('credential-secret').length > 0 ||
        fieldValue('credential-passphrase').length > 0 ||
        fieldValue('credential-public-key').trim().length > 0 ||
        hasGeneratedMaterial();
}

function hasFlowSpecificDraft() {
    return fieldValue('credential-secret').length > 0 ||
        fieldValue('credential-passphrase').length > 0 ||
        fieldValue('credential-public-key').trim().length > 0 ||
        hasGeneratedMaterial();
}

function showDiscardSheet(target, message) {
    if (document.querySelector('[data-testid="discard-credential-draft"]')) return;

    const sheet = document.createElement('div');
    sheet.className = 'sheet';
    sheet.dataset.testid = 'discard-credential-draft';
    sheet.setAttribute('role', 'alertdialog');
    sheet.setAttribute('aria-modal', 'true');
    sheet.setAttribute('aria-labelledby', 'discard-credential-draft-title');
    sheet.style.zIndex = '60';

    const title = document.createElement('p');
    title.className = 'sheet__title';
    title.id = 'discard-credential-draft-title';
    title.textContent = 'Discard unsaved credential changes?';

    const body = document.createElement('p');
    body.className = 'sheet__body';
    body.textContent = message;

    const keep = document.createElement('button');
    keep.type = 'button';
    keep.className = 'btn btn--primary btn--block';
    keep.dataset.testid = 'keep-credential-draft';
    keep.textContent = 'Keep editing';

    const discard = document.createElement('button');
    discard.type = 'button';
    discard.className = 'btn btn--danger btn--block';
    discard.dataset.testid = 'discard-credential-draft-confirm';
    discard.textContent = 'Discard changes';

    const close = () => sheet.remove();
    keep.addEventListener('click', () => {
        close();
        target.focus();
    }, { once: true });
    discard.addEventListener('click', () => {
        close();
        target.dataset.draftGuardBypass = 'true';
        target.click();
    }, { once: true });

    sheet.append(title, body, keep, discard);
    document.body.appendChild(sheet);
    keep.focus();
}

function onCapturedClick(event) {
    const target = event.target instanceof Element
        ? event.target.closest('[data-testid="cancel-credential"], [data-testid="change-credential-type"]')
        : null;
    if (!(target instanceof HTMLElement)) return;

    if (target.dataset.draftGuardBypass === 'true') {
        delete target.dataset.draftGuardBypass;
        return;
    }

    const isChangeType = target.dataset.testid === 'change-credential-type';
    const dirty = isChangeType ? hasFlowSpecificDraft() : hasAnyCredentialDraft();
    if (!dirty) return;

    event.preventDefault();
    event.stopImmediatePropagation();
    event.stopPropagation();

    showDiscardSheet(
        target,
        isChangeType
            ? 'Changing credential type will clear the key, password, or public-key material entered for this type.'
            : 'Your credential draft will stay here unless you explicitly discard it.');
}

export function installCredentialDraftGuard() {
    if (installed) return;
    installed = true;
    document.addEventListener('click', onCapturedClick, true);
}
