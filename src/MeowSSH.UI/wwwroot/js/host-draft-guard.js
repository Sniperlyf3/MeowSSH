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
