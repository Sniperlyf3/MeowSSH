let hostDeleteGuardInstalled = false;

export function confirmDiscard() {
    const existing = document.querySelector('[data-testid="discard-host-draft"]');
    if (existing) return Promise.resolve(false);

    return new Promise(resolve => {
        const sheet = document.createElement('div');
        sheet.className = 'sheet';
        sheet.dataset.testid = 'discard-host-draft';
        sheet.setAttribute('role', 'alertdialog');
        sheet.setAttribute('aria-modal', 'true');
        sheet.setAttribute('aria-labelledby', 'discard-host-draft-title');
        sheet.style.zIndex = '60';

        const title = document.createElement('p');
        title.className = 'sheet__title';
        title.id = 'discard-host-draft-title';
        title.textContent = 'Discard unsaved connection changes?';

        const body = document.createElement('p');
        body.className = 'sheet__body';
        body.textContent = 'Your connection draft will stay here unless you explicitly discard it.';

        const keep = document.createElement('button');
        keep.type = 'button';
        keep.className = 'btn btn--primary btn--block';
        keep.dataset.testid = 'keep-host-draft';
        keep.textContent = 'Keep editing';

        const discard = document.createElement('button');
        discard.type = 'button';
        discard.className = 'btn btn--danger btn--block';
        discard.dataset.testid = 'discard-host-draft-confirm';
        discard.textContent = 'Discard changes';

        const finish = value => {
            sheet.remove();
            resolve(value);
        };

        keep.addEventListener('click', () => finish(false), { once: true });
        discard.addEventListener('click', () => finish(true), { once: true });

        sheet.append(title, body, keep, discard);
        document.body.appendChild(sheet);
        keep.focus();
    });
}

function showDeleteHostSheet(target) {
    if (document.querySelector('[data-testid="delete-host-confirmation"]')) return;

    const sheet = document.createElement('div');
    sheet.className = 'sheet';
    sheet.dataset.testid = 'delete-host-confirmation';
    sheet.setAttribute('role', 'alertdialog');
    sheet.setAttribute('aria-modal', 'true');
    sheet.setAttribute('aria-labelledby', 'delete-host-confirmation-title');
    sheet.style.zIndex = '60';

    const title = document.createElement('p');
    title.className = 'sheet__title';
    title.id = 'delete-host-confirmation-title';
    title.textContent = 'Delete this connection?';

    const body = document.createElement('p');
    body.className = 'sheet__body';
    body.textContent = 'This removes the saved connection from MeowSSH. This action cannot be undone.';

    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.className = 'btn btn--primary btn--block';
    cancel.dataset.testid = 'keep-host-connection';
    cancel.textContent = 'Keep connection';

    const confirm = document.createElement('button');
    confirm.type = 'button';
    confirm.className = 'btn btn--danger btn--block';
    confirm.dataset.testid = 'delete-host-confirm';
    confirm.textContent = 'Delete connection';

    cancel.addEventListener('click', () => {
        sheet.remove();
        target.focus();
    }, { once: true });

    confirm.addEventListener('click', () => {
        sheet.remove();
        target.dataset.deleteGuardBypass = 'true';
        target.click();
    }, { once: true });

    sheet.append(title, body, cancel, confirm);
    document.body.appendChild(sheet);
    cancel.focus();
}

function onDeleteHostCaptured(event) {
    const target = event.target instanceof Element
        ? event.target.closest('[data-testid="delete-host"]')
        : null;
    if (!(target instanceof HTMLElement)) return;

    if (target.dataset.deleteGuardBypass === 'true') {
        delete target.dataset.deleteGuardBypass;
        return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    event.stopPropagation();
    showDeleteHostSheet(target);
}

export function installHostDeleteGuard() {
    if (hostDeleteGuardInstalled) return;
    hostDeleteGuardInstalled = true;
    document.addEventListener('click', onDeleteHostCaptured, true);
}
